using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Builds the stylised island into the open scene from the arena art kit (Assets/Art/Arena, made by Tools/ArenaKit):
    /// a painted terrain with dirt paths, a pine forest with ferns, plants, mushrooms and rocks, a village of intact houses,
    /// the user's BattlefieldTown buildings (little towns, single houses, walls; see PlanTown) and, when MilitaryCamps is on,
    /// a military base (barracks, watchtower, tents, sandbags, fences). Everything is static and batch-friendly for WebGL.
    /// Also lays out the 16 player spawn points and the loot spawn points.
    /// </summary>
    public static partial class ArenaBuilder
    {
        public class Site
        {
            public string model, kind;
            public Vector3 pos;
            public float yaw, fx, fz;
            public Vector3 hub;      // settlement centre the house's path leads to
        }

        /// <summary>The island is MapScale times the size of the first version (2 = four times the area). Buildings keep their size.</summary>
        public const float MapScale = 2f;
        const float TerrainSize = 150f * MapScale;
        const float Cell = 2.5f;
        const int Seed = 2026;

        struct FlatZone { public float x, z, h, inner, outer; }

        static float noiseX, noiseZ, forestX, forestZ;
        static readonly List<FlatZone> zones = new List<FlatZone>();
        static readonly List<Site> sites = new List<Site>();
        static readonly List<Vector4> avoid = new List<Vector4>();                 // x, z, radius (nothing grows here)
        static readonly List<(Vector2 a, Vector2 b)> pathSegments = new List<(Vector2, Vector2)>();
        static readonly List<Vector3> lootSpots = new List<Vector3>();
        static readonly List<Vector3> hubs = new List<Vector3>();                  // hamlet / outpost centres besides the village
        static float[] pathGrid;                                                   // 1 m distance-to-path field, so the bakers stay fast
        static int pathGridN;
        static Vector3 baseCentre, villageCentre;
        static float baseYaw;
        static Transform root;

        public static IReadOnlyList<Site> Sites => sites;

        // ---------------------------------------------------------------- fields used by TerrainBaker / MinimapBaker
        public static float RawHeight(float x, float z)
        {
            float n = Mathf.PerlinNoise(x * 0.018f + noiseX, z * 0.018f + noiseZ) * 3.4f
                    + Mathf.PerlinNoise(x * 0.095f + noiseX, z * 0.095f + noiseZ) * 0.8f;
            float r = Mathf.Sqrt(x * x + z * z);
            float land = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(50f * MapScale, 69f * MapScale, r));    // island falloff
            return n * land - (1f - land) * 3.6f - 0.4f + 0.9f * land;
        }

        public static float Height(float x, float z)
        {
            float h = RawHeight(x, z);
            foreach (var zone in zones)
            {
                float d = Mathf.Sqrt((x - zone.x) * (x - zone.x) + (z - zone.z) * (z - zone.z));
                float w = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(zone.inner, zone.outer, d));
                if (w > 0f) h = Mathf.Lerp(h, zone.h, w);
            }
            return h;
        }

        public static float PathDistance(float x, float z)
        {
            if (pathGrid == null) return PathDistanceExact(x, z);
            float half = TerrainSize * 0.5f;
            float fx = Mathf.Clamp(x + half, 0f, TerrainSize - 0.001f), fz = Mathf.Clamp(z + half, 0f, TerrainSize - 0.001f);
            int i = (int)fx, j = (int)fz;
            float tx = fx - i, tz = fz - j;
            float a = Mathf.Lerp(pathGrid[j * pathGridN + i], pathGrid[j * pathGridN + i + 1], tx);
            float b = Mathf.Lerp(pathGrid[(j + 1) * pathGridN + i], pathGrid[(j + 1) * pathGridN + i + 1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        static void BuildPathGrid()
        {
            pathGrid = null;
            pathGridN = Mathf.CeilToInt(TerrainSize) + 1;
            var grid = new float[pathGridN * pathGridN];
            float half = TerrainSize * 0.5f;
            for (int j = 0; j < pathGridN; j++)
                for (int i = 0; i < pathGridN; i++)
                    grid[j * pathGridN + i] = PathDistanceExact(-half + i, -half + j);
            pathGrid = grid;
        }

        static float PathDistanceExact(float x, float z)
        {
            float best = 1e9f;
            var p = new Vector2(x, z);
            foreach (var (a, b) in pathSegments)
            {
                var ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
                float d = (p - (a + ab * t)).sqrMagnitude;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        /// <summary>1 under the buildings' level pads, fading to 0 just outside them.</summary>
        public static float PadWeight(float x, float z)
        {
            float best = 0f;
            foreach (var zone in zones)
            {
                float d = Mathf.Sqrt((x - zone.x) * (x - zone.x) + (z - zone.z) * (z - zone.z));
                best = Mathf.Max(best, 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(zone.inner * 0.85f, zone.inner * 1.3f, d)));
            }
            return best;
        }

        public static float Forest(float x, float z) =>
            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.38f, 0.62f, Mathf.PerlinNoise(x * 0.030f + forestX, z * 0.030f + forestZ)));

        // ---------------------------------------------------------------- entry point
        public static void Build()
        {
            Random.InitState(Seed);
            noiseX = Random.value * 100f; noiseZ = Random.value * 100f;
            forestX = Random.value * 100f; forestZ = Random.value * 100f;
            zones.Clear(); sites.Clear(); avoid.Clear(); pathSegments.Clear(); lootSpots.Clear(); hubs.Clear(); pathGrid = null;

            EnsureFolder("Assets/Art/Generated");
            ArenaAssets.Prepare();
            ForestAssets.Prepare();
            TownAssets.Prepare();

            root = new GameObject("Arena").transform;

            PlanSites();
            PlanPaths();
            BuildPathGrid();
            var terrainTexture = TerrainBaker.Bake();
            BuildTerrain(terrainTexture);
            BuildSea();
            BuildBoundary();
            PlaceBuildings();
            BuildSpawnPoints(16, 50f * MapScale);
            BuildOpenLootPoints();
            ScatterNature();
            if (TownAssets.Available) BuildZombieArena();   // the Zombie Survival town, far north (ArenaBuilder.Zombie)
            BuildLootRoot();
            SetupLighting();
        }

        // ---------------------------------------------------------------- planning
        static Vector3 Polar(float angleDeg, float dist) => new Vector3(Mathf.Sin(angleDeg * Mathf.Deg2Rad) * dist, 0f, Mathf.Cos(angleDeg * Mathf.Deg2Rad) * dist);

        static Vector3 Local(Vector3 centre, float yaw, float lx, float lz)
        {
            var p = Quaternion.Euler(0f, yaw, 0f) * new Vector3(lx, 0f, lz);
            return new Vector3(centre.x + p.x, 0f, centre.z + p.z);
        }

        static float FaceYaw(Vector3 from, Vector3 to) => Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;

        static void AddZone(float x, float z, float inner, float outer)
        {
            zones.Add(new FlatZone { x = x, z = z, h = Height(x, z), inner = inner, outer = outer });
        }

        static bool FarFromSites(float x, float z, float margin)
        {
            foreach (var a in avoid)
            {
                float d = Mathf.Sqrt((a.x - x) * (a.x - x) + (a.y - z) * (a.y - z));
                if (d < a.z + margin) return false;
            }
            return true;
        }

        /// <summary>Military base + the two outposts (barracks, watchtowers, tents, sandbags, fences). Off since 2026-10-27 (user);
        /// the base angle is still drawn because the village is placed opposite it.</summary>
        const bool MilitaryCamps = false;

        static void PlanSites()
        {
            // military base: a pad on solid ground
            float baseAngle = 0f;
            for (int t = 0; t < 200; t++)
            {
                baseAngle = Random.Range(0f, 360f);
                var c = Polar(baseAngle, 31f * MapScale);
                if (RawHeight(c.x, c.z) > 1.2f) { baseCentre = c; break; }
            }
            baseYaw = FaceYaw(baseCentre, Vector3.zero) + 90f;        // the long axis runs across the line to the map centre
            if (MilitaryCamps)
            {
                AddZone(baseCentre.x, baseCentre.z, 21f, 30f);
                avoid.Add(new Vector4(baseCentre.x, baseCentre.z, 24f, 0f));
            }

            // village on the opposite side
            for (int t = 0; t < 200; t++)
            {
                var c = Polar(baseAngle + 180f + Random.Range(-35f, 35f), Random.Range(22f, 28f) * MapScale);
                if (RawHeight(c.x, c.z) > 1.0f) { villageCentre = c; break; }
            }
            int[] order = { 0, 1, 0, 1, 0, 1, 0 };
            int placed = 0, tries = 0;
            while (placed < order.Length && tries++ < 600)
            {
                var c = villageCentre + Polar(Random.Range(0f, 360f), Random.Range(8f, 15f));
                if (RawHeight(c.x, c.z) < 0.9f || !FarFromSites(c.x, c.z, 8f)) continue;
                sites.Add(new Site { model = order[placed] == 0 ? "house_a" : "house_b", kind = "house", pos = c, yaw = FaceYaw(c, villageCentre) + Random.Range(-12f, 12f), hub = villageCentre });
                AddZone(c.x, c.z, 7f, 11.5f);
                avoid.Add(new Vector4(c.x, c.z, 8f, 0f));
                placed++;
            }

            // two small hamlets on the flanks of the island
            for (int h = 0; h < 2; h++)
            {
                bool found = false;
                var hc = Vector3.zero;
                for (int t = 0; t < 400 && !found; t++)
                {
                    hc = Polar(baseAngle + 90f + h * 180f + Random.Range(-25f, 25f), Random.Range(48f, 78f));
                    found = RawHeight(hc.x, hc.z) > 1.0f && FarFromSites(hc.x, hc.z, 16f);
                }
                if (!found) continue;
                hubs.Add(hc);
                avoid.Add(new Vector4(hc.x, hc.z, 4f, 0f));
                int houses = 0;
                for (int t = 0; t < 400 && houses < 4; t++)
                {
                    var c = hc + Polar(Random.Range(0f, 360f), Random.Range(8f, 14f));
                    if (RawHeight(c.x, c.z) < 0.9f || !FarFromSites(c.x, c.z, 8f)) continue;
                    sites.Add(new Site { model = (houses + h) % 2 == 0 ? "house_a" : "house_b", kind = "house", pos = c, yaw = FaceYaw(c, hc) + Random.Range(-12f, 12f), hub = hc });
                    AddZone(c.x, c.z, 7f, 11.5f);
                    avoid.Add(new Vector4(c.x, c.z, 8f, 0f));
                    houses++;
                }
            }

            // two military outposts (watchtower, tents, cover) between the base and the hamlets
            for (int o = 0; o < (MilitaryCamps ? 2 : 0); o++)
            {
                for (int t = 0; t < 400; t++)
                {
                    var c = Polar(baseAngle + (o == 0 ? 50f : -50f) + Random.Range(-15f, 15f), Random.Range(60f, 88f));
                    if (RawHeight(c.x, c.z) < 1.0f || !FarFromSites(c.x, c.z, 16f)) continue;
                    float yaw = Random.Range(0f, 360f);
                    AddZone(c.x, c.z, 11f, 17f);
                    avoid.Add(new Vector4(c.x, c.z, 13f, 0f));
                    OutpostItem(c, yaw, "watchtower", "tower", 0f, 0f, 0f, 6.2f, 14f);
                    OutpostItem(c, yaw, "tent", "tent", -7f, 4.5f, 0f, 6.5f, 5.3f);
                    OutpostItem(c, yaw, "tent", "tent", -7f, -4.5f, 0f, 6.5f, 5.3f);
                    OutpostItem(c, yaw, "sandbag_corner", "cover", 5f, 3.5f, 180f, 3.6f, 3.6f);
                    OutpostItem(c, yaw, "crate_stack", "cover", 2f, -5f, Random.Range(0f, 360f), 2.8f, 1.9f);
                    OutpostItem(c, yaw, "crate_stack", "cover", -2f, 6.5f, Random.Range(0f, 360f), 2.8f, 1.9f);
                    for (int k = 0; k < 3; k++) OutpostItem(c, yaw, "barrel", "cover", Random.Range(-4f, 4f), Random.Range(-8f, -6f), 0f, 0.9f, 0.9f);
                    hubs.Add(c);
                    break;
                }
            }

            // the user's BattlefieldTown buildings (2026-10-27, replace my procedural ruins): little towns + singles + walls
            if (TownAssets.Available) PlanTown();

            // base buildings (local frame: +x along the long axis)
            void BaseItem(string model, string kind, float lx, float lz, float yawOffset, float fx, float fz)
            {
                var p = Local(baseCentre, baseYaw, lx, lz);
                sites.Add(new Site { model = model, kind = kind, pos = p, yaw = baseYaw + yawOffset, fx = fx, fz = fz });
            }
            if (!MilitaryCamps) return;
            BaseItem("barracks", "barracks", 0f, -7.5f, 0f, 17.5f, 9.7f);
            BaseItem("barracks", "barracks", 0f, 7.5f, 180f, 17.5f, 9.7f);
            BaseItem("watchtower", "tower", 14.5f, -9.5f, 0f, 6.2f, 14f);
            for (int i = -1; i <= 1; i++) BaseItem("tent", "tent", -15f, i * 8f, 0f, 6.5f, 5.3f);
            BaseItem("sandbag_corner", "cover", 12f, -4f, 0f, 3.6f, 3.6f);
            BaseItem("sandbag_corner", "cover", 12f, 4.5f, 180f, 3.6f, 3.6f);
            BaseItem("sandbag_wall", "cover", -11f, 0f, 90f, 4.5f, 1.2f);
            BaseItem("sandbag_wall", "cover", 18.5f, 3f, 90f, 4.5f, 1.2f);
            BaseItem("flagpole", "prop", 0f, 0f, 0f, 1f, 1f);
            for (int i = 0; i < 4; i++) BaseItem("crate_stack", "cover", Random.Range(-9f, 9f), Random.Range(-2.2f, 2.2f), Random.Range(0f, 360f), 2.8f, 1.9f);
            for (int i = 0; i < 7; i++) BaseItem("barrel", "cover", Random.Range(-13f, 13f), Random.Range(-2.6f, 2.6f), 0f, 0.9f, 0.9f);
            for (float x = -16f; x <= 16f; x += 3.6f)
            {
                BaseItem("fence", "fence", x, -16f, 0f, 3.6f, 0.5f);
                BaseItem("fence", "fence", x, 16f, 0f, 3.6f, 0.5f);
            }
        }

        // BattlefieldTown models (Assets/Art/BattlefieldTown/Models/Buildings) and their footprints in metres (measured in Blender).
        static readonly string[] TownHouses = { "SM_House_1F_A", "SM_House_1F_B", "SM_House_2F_A", "SM_House_2F_B", "SM_House_Ruined" };
        static readonly float[] TownHouseWeights = { 3f, 3f, 2f, 2f, 2f };
        static readonly Dictionary<string, Vector2> TownFootprint = new Dictionary<string, Vector2>
        {
            { "SM_House_1F_A", new Vector2(6.7f, 6.4f) }, { "SM_House_1F_B", new Vector2(5.0f, 5.7f) },
            { "SM_House_2F_A", new Vector2(7.8f, 6.8f) }, { "SM_House_2F_B", new Vector2(8.0f, 7.2f) },
            { "SM_House_Ruined", new Vector2(8.2f, 8.1f) }, { "SM_Wall_Broken", new Vector2(4.0f, 1.6f) }, { "SM_Wall_Compound", new Vector2(5.0f, 0.4f) },
        };

        /// <summary>The BattlefieldTown models are built for a person; the cats (1.75 m capsule) need more head room on the stairs.
        /// Measured 2026-10-27: least clearance above a step 1.09 m (2F_A roof stairs), 1.31 (Ruined), 1.47 (2F_B) -> x1.7.
        /// Steps become 0.34-0.37 m, so the player's CharacterController.stepOffset is 0.4 (Phase1Setup).</summary>
        public const float TownScale = 1.7f;

        static void TownSite(string model, string kind, Vector3 pos, float yaw, bool flatten)
        {
            var fp = TownFootprint[model] * TownScale;
            sites.Add(new Site { model = model, kind = kind, pos = pos, yaw = yaw, fx = fp.x, fz = fp.y });
            float r = fp.magnitude * 0.5f;
            if (flatten) AddZone(pos.x, pos.z, r + 1.2f, r + 5f);
            avoid.Add(new Vector4(pos.x, pos.z, r + 0.8f, 0f));
        }

        /// <summary>Two (three on a big enough island) little towns: buildings on a loose 3 x 3 grid of 12 m blocks around an open
        /// square, compound walls around the square; then single houses scattered over the island and broken walls as cover.</summary>
        static void PlanTown()
        {
            const float S = TownScale;
            float block = 12f * S;   // grid spacing between the houses of a town
            int towns = 0;
            for (int t = 0; t < 600 && towns < 3; t++)
            {
                var c = Polar(Random.Range(0f, 360f), Random.Range(20f, 44f) * MapScale);
                if (RawHeight(c.x, c.z) < 1.0f || !FarFromSites(c.x, c.z, 24f * S)) continue;
                // the whole town sits on one level area
                AddZone(c.x, c.z, 17f * S, 26f * S);
                float townYaw = Random.Range(0f, 90f);
                var cells = new List<Vector2Int>();
                for (int gx = -1; gx <= 1; gx++) for (int gz = -1; gz <= 1; gz++) if (gx != 0 || gz != 0) cells.Add(new Vector2Int(gx, gz));
                int count = Random.Range(5, 8);
                for (int k = 0; k < count && cells.Count > 0; k++)
                {
                    var cell = cells[Random.Range(0, cells.Count)]; cells.Remove(cell);
                    var p = Local(c, townYaw, cell.x * block + Random.Range(-1.2f, 1.2f), cell.y * block + Random.Range(-1.2f, 1.2f));
                    TownSite(Pick(TownHouses, TownHouseWeights), "town", p, townYaw + 90f * Random.Range(0, 4) + Random.Range(-4f, 4f), true);
                }
                // walls around the square, some broken
                for (int w = 0; w < 3; w++)
                {
                    float side = Random.Range(0, 4) * 90f;
                    var p = Local(c, townYaw + side, Random.Range(-3f, 3f) * S, 5.5f * S);
                    TownSite(Random.value < 0.6f ? "SM_Wall_Compound" : "SM_Wall_Broken", "townwall", p, townYaw + side + Random.Range(-6f, 6f), false);
                }
                avoid.Add(new Vector4(c.x, c.z, 20f * S, 0f));   // no trees inside the town
                hubs.Add(c);                                  // a path to the island centre
                towns++;
            }

            // single houses scattered over the island
            int placed = 0, tries = 0;
            while (placed < 9 && tries++ < 1500)
            {
                var c = Polar(Random.Range(0f, 360f), Random.Range(14f, 48f) * MapScale);
                if (RawHeight(c.x, c.z) < 0.9f || !FarFromSites(c.x, c.z, 9f * S)) continue;
                TownSite(Pick(TownHouses, TownHouseWeights), "town", c, Random.Range(0f, 360f), true);
                placed++;
            }

            // broken walls as cover in the open
            placed = 0; tries = 0;
            while (placed < 10 && tries++ < 1500)
            {
                var c = Polar(Random.Range(0f, 360f), Random.Range(10f, 50f) * MapScale);
                if (RawHeight(c.x, c.z) < 0.7f || !FarFromSites(c.x, c.z, 5f)) continue;
                TownSite(Random.value < 0.65f ? "SM_Wall_Broken" : "SM_Wall_Compound", "townwall", c, Random.Range(0f, 360f), true);
                placed++;
            }
        }

        static void OutpostItem(Vector3 centre, float yaw, string model, string kind, float lx, float lz, float yawOffset, float fx, float fz)
        {
            sites.Add(new Site { model = model, kind = kind, pos = Local(centre, yaw, lx, lz), yaw = yaw + yawOffset, fx = fx, fz = fz });
        }

        // ---------------------------------------------------------------- paths
        static void PlanPaths()
        {
            var centre = Vector3.zero;
            var gate = Local(baseCentre, baseYaw, 19.5f, 0f);
            Connect(villageCentre, centre);
            if (MilitaryCamps) Connect(centre, gate);
            foreach (var h in hubs) Connect(h, centre);
            foreach (var s in sites)
            {
                if (s.kind == "house")
                {
                    var door = Local(s.pos, s.yaw, 0f, 5.2f);
                    Connect(door, s.hub);
                }
                else if (s.kind == "ruin")
                {
                    var door = Local(s.pos, s.yaw, 0f, 4.5f);
                    var targets = new List<Vector3> { villageCentre, centre };
                    if (MilitaryCamps) targets.Add(gate);
                    targets.AddRange(hubs);
                    var best = targets[0];
                    foreach (var t in targets) if ((t - door).sqrMagnitude < (best - door).sqrMagnitude) best = t;
                    Connect(door, best);
                }
            }
        }

        static void Connect(Vector3 a, Vector3 b)
        {
            var A = new Vector2(a.x, a.z); var B = new Vector2(b.x, b.z);
            float len = Vector2.Distance(A, B);
            int n = Mathf.Max(2, Mathf.CeilToInt(len / 5f));
            var perp = new Vector2(-(B - A).y, (B - A).x).normalized;
            float phase = Random.value * 10f, amp = Random.Range(1.2f, 2.6f);
            var prev = A;
            for (int i = 1; i <= n; i++)
            {
                float t = i / (float)n;
                var p = Vector2.Lerp(A, B, t) + perp * (Mathf.Sin(t * 3.14159f) * Mathf.Sin(t * 6f + phase) * amp);
                if (i == n) p = B;
                pathSegments.Add((prev, p));
                prev = p;
            }
        }

        // ---------------------------------------------------------------- terrain and water
        static void BuildTerrain(Texture2D albedo)
        {
            int cells = Mathf.RoundToInt(TerrainSize / Cell);
            float half = TerrainSize * 0.5f;
            var verts = new Vector3[(cells + 1) * (cells + 1)];
            var uvs = new Vector2[verts.Length];
            for (int j = 0; j <= cells; j++)
                for (int i = 0; i <= cells; i++)
                {
                    float x = -half + i * Cell, z = -half + j * Cell;
                    int k = j * (cells + 1) + i;
                    verts[k] = new Vector3(x, Height(x, z), z);
                    uvs[k] = new Vector2((x + TerrainBaker.Extent) / (TerrainBaker.Extent * 2f), (z + TerrainBaker.Extent) / (TerrainBaker.Extent * 2f));
                }
            var tris = new List<int>(cells * cells * 6);
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int a = j * (cells + 1) + i, b = a + 1, c = a + cells + 1, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    tris.Add(a); tris.Add(d); tris.Add(b);
                }
            var mesh = new Mesh { name = "Terrain", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts; mesh.uv = uvs; mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            SaveAsset(mesh, "Assets/Art/Generated/Terrain.asset");

            var mat = LoadOrCreate("Assets/Art/Arena/Materials/Arena_Terrain.mat", "Universal Render Pipeline/Lit");
            mat.SetTexture("_BaseMap", albedo);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);

            var go = new GameObject("Terrain");
            go.transform.SetParent(root);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            MakeStatic(go);
        }

        static void BuildSea()
        {
            var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
            sea.name = "Sea";
            sea.transform.SetParent(root);
            sea.transform.position = new Vector3(0f, -0.95f, 0f);
            sea.transform.localScale = new Vector3(60f, 1f, 60f);
            Object.DestroyImmediate(sea.GetComponent<Collider>());
            var mat = LoadOrCreate("Assets/Art/Arena/Materials/Arena_Water.mat", "Universal Render Pipeline/Lit");
            mat.SetColor("_BaseColor", new Color(0.22f, 0.58f, 0.74f));
            mat.SetFloat("_Smoothness", 0.7f);
            EditorUtility.SetDirty(mat);
            sea.GetComponent<Renderer>().sharedMaterial = mat;
            sea.AddComponent<Unity.AI.Navigation.NavMeshModifier>().ignoreFromBuild = true;
            MakeStatic(sea);
        }

        // Invisible ring so nobody walks off the island into the sea.
        static void BuildBoundary()
        {
            int n = 40;
            float radius = 68f * MapScale;
            var parent = new GameObject("Boundary").transform;
            parent.SetParent(root);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                var wall = new GameObject("Boundary_" + i);
                wall.transform.SetParent(parent);
                wall.transform.position = new Vector3(Mathf.Cos(a) * radius, 5f, Mathf.Sin(a) * radius);
                wall.transform.rotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                var box = wall.AddComponent<BoxCollider>();
                box.size = new Vector3(2f * Mathf.PI * radius / n + 1f, 20f, 2f);
            }
        }

        // ---------------------------------------------------------------- buildings
        static void PlaceBuildings()
        {
            var parent = new GameObject("Buildings").transform;
            parent.SetParent(root);
            foreach (var s in sites)
            {
                bool town = s.kind == "town" || s.kind == "townwall";
                var prefab = town ? TownAssets.Building(s.model) : ArenaAssets.Model(s.model);
                if (prefab == null) { Debug.LogWarning("ArenaBuilder: missing model " + s.model); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = s.model;
                go.transform.SetParent(parent);
                go.transform.SetPositionAndRotation(new Vector3(s.pos.x, Height(s.pos.x, s.pos.z), s.pos.z), Quaternion.Euler(0f, s.yaw, 0f));
                if (town) go.transform.localScale = Vector3.one * TownScale;
                AddMeshColliders(go, false);
                MakeStatic(go);
                if (town) { if (s.kind == "town") AddTownLoot(go); continue; }

                var data = LoadKit(s.model);
                if (data != null)
                {
                    if (s.kind == "house" || s.kind == "ruin" || s.kind == "barracks" || s.kind == "tower" || s.kind == "tent")
                        foreach (var l in data.loot) lootSpots.Add(go.transform.TransformPoint(new Vector3(l.x, l.y + 0.1f, l.z)));
                    if (s.fx <= 0f) { s.fx = data.fx; s.fz = data.fz; }
                }
            }
        }

        // Loot inside a BattlefieldTown house: on the ground floor and, for the two-storey ones, upstairs (found by casting down
        // onto the floors; the ray starts inside the room so it misses the roof).
        static void AddTownLoot(GameObject go)
        {
            Physics.SyncTransforms();
            var t = go.transform;
            float[] starts = go.name.Contains("2F") ? new[] { 1.6f, 5.2f } : new[] { 1.6f };
            foreach (float y in starts)
                foreach (var off in new[] { new Vector3(0.6f, 0f, 0.4f), new Vector3(-1.2f, 0f, -0.9f) })
                {
                    var from = t.TransformPoint(new Vector3(off.x, y, off.z));   // local: scales with TownScale
                    if (Physics.Raycast(from, Vector3.down, out var hit, 2.4f * TownScale) && hit.collider.transform.IsChildOf(t) && hit.normal.y > 0.8f)
                    {
                        lootSpots.Add(hit.point + Vector3.up * 0.1f);
                        break;
                    }
                }
        }

        [System.Serializable] class V3 { public float x, y, z; }
        [System.Serializable] class KitData { public List<V3> loot = new List<V3>(); public string kind; public float pad, fx, fz; }

        static KitData LoadKit(string model)
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Art/Arena/Data/" + model + ".json");
            return text == null ? null : JsonUtility.FromJson<KitData>(text.text);
        }

        static void AddMeshColliders(GameObject go, bool convex)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = convex;
            }
        }

        // ---------------------------------------------------------------- spawn points and loot markers
        static void BuildSpawnPoints(int count, float radius)
        {
            var parent = new GameObject("SpawnPoints");
            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                var sp = new GameObject("SpawnPoint_" + i.ToString("00"));
                sp.transform.SetParent(parent.transform);
                float x = Mathf.Sin(a) * radius, z = Mathf.Cos(a) * radius;
                sp.transform.position = new Vector3(x, Height(x, z) + 0.15f, z);
                sp.transform.rotation = Quaternion.LookRotation(new Vector3(-x, 0f, -z));
                sp.AddComponent<SpawnPoint>();
                avoid.Add(new Vector4(x, z, 3.5f, 0f));
            }
        }

        static void BuildOpenLootPoints()
        {
            for (int i = 0; i < 2500 && lootSpots.Count < 260; i++)
            {
                float angle = Random.value * Mathf.PI * 2f, dist = Mathf.Sqrt(Random.value) * 56f * MapScale;
                float x = Mathf.Cos(angle) * dist, z = Mathf.Sin(angle) * dist;
                if (Height(x, z) < 0.4f || !FarFromSites(x, z, 0f)) continue;
                lootSpots.Add(new Vector3(x, Height(x, z) + 0.1f, z));
                avoid.Add(new Vector4(x, z, 1.4f, 0f));
            }
        }

        static void BuildLootRoot()
        {
            var parent = new GameObject("LootPoints");
            int index = 0;
            foreach (var p in lootSpots)
            {
                var g = new GameObject("Loot_" + index++);
                g.transform.SetParent(parent.transform);
                g.transform.position = p;
                g.AddComponent<LootSpawnPoint>();
            }
        }

        // ---------------------------------------------------------------- lighting
        static void SetupLighting()
        {
            var light = Object.FindFirstObjectByType<Light>();
            if (light != null && light.type == LightType.Directional)
            {
                light.transform.rotation = Quaternion.Euler(48f, -38f, 0f);
                light.color = new Color(1f, 0.95f, 0.84f);
                light.intensity = 1.3f;
                light.shadows = LightShadows.Hard;   // cheap enough for WebGL
                light.shadowStrength = 0.6f;
            }

            var sky = LoadOrCreate("Assets/Art/Arena/Materials/Arena_Sky.mat", "Skybox/Procedural");
            sky.SetFloat("_SunSize", 0.045f);
            sky.SetFloat("_SunSizeConvergence", 5f);
            sky.SetFloat("_AtmosphereThickness", 0.85f);
            sky.SetColor("_SkyTint", new Color(0.50f, 0.66f, 0.95f));
            sky.SetColor("_GroundColor", new Color(0.62f, 0.70f, 0.58f));
            sky.SetFloat("_Exposure", 1.3f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.78f, 0.98f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.70f, 0.52f);
            RenderSettings.ambientGroundColor = new Color(0.40f, 0.36f, 0.28f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.80f, 0.89f, 0.95f);
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 340f;

            // Shadows are the most expensive thing in a browser: keep them close to the player and use a single cascade.
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var urp = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (urp == null) continue;
                urp.shadowDistance = 45f;
                urp.shadowCascadeCount = 1;
                EditorUtility.SetDirty(urp);
            }
        }

        // ---------------------------------------------------------------- helpers
        // Small things are batched (fewer draw calls); big objects like trees stay separate so they are culled one by one.
        static void MakeStatic(GameObject go, bool batch = true)
        {
            var flags = StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ContributeGI;
            if (batch) flags |= StaticEditorFlags.BatchingStatic;
            foreach (var t in go.GetComponentsInChildren<Transform>()) GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        }

        static Material LoadOrCreate(string path, string shader)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }

        static void SaveAsset(Object asset, string path)
        {
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
