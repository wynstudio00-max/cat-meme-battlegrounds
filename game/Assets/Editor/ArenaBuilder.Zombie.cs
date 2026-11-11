using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    // The Zombie Survival map (2026-10-28, user: "a smaller area with only houses and trees"): a compact town of the user's
    // BattlefieldTown houses inside a thick ring of pines, built in the same scene but far north of the battle-royale island
    // (out of sight both ways: the sea ends at 300 m, the camera far clip is 650 m and the fog closes at 420 m).
    //  * ground: 220 m square, flat in the town, gentle bumps in the forest; one baked texture (TerrainBaker.BakeZombie) that blends
    //    grass with dirt streets, the dirt square, bare yards round the houses, patches and a darker forest floor (built last)
    //  * town: 5 x 5 grid of 21 m blocks; the middle one is the open square (player start, object "ZombieStart"), 14 houses
    //    (3x 2F_A, 3x 2F_B, 3x Ruined, 3x 1F_A, 2x 1F_B - all the stair houses so the fight goes up and down), walls as cover,
    //    a few pines between; loot inside the houses (AddTownLoot)
    //  * no rocks or bushes (nothing to camp on); invisible walls at +-56 m keep everyone in the town, the pine ring beyond hides
    //    the edge of the world. Zombies rise inside the walls (ZombieWaveManager path check).
    // The NavMesh bake (Phase2Setup) collects every collider in the scene, so this town is baked with the island.
    public static partial class ArenaBuilder
    {
        public static readonly Vector3 ZombieCentre = new Vector3(0f, 0f, 900f);
        public const float ZombieHalfSize = 56f;            // invisible walls, the playable square is 112 m across
        const float ZombieGroundHalf = 110f, ZombieBlock = 21f, ZombieGroundY = 0.5f;

        static float ZombieHeight(float lx, float lz)
        {
            float r = Mathf.Max(Mathf.Abs(lx), Mathf.Abs(lz));
            float bumps = (Mathf.PerlinNoise(lx * 0.05f + 31f, lz * 0.05f + 7f) - 0.5f) * 1.6f;
            return ZombieGroundY + bumps * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(50f, 70f, r));
        }

        static void BuildZombieArena()
        {
            var zroot = new GameObject("ZombieArena").transform;
            zroot.SetParent(root);
            zroot.position = ZombieCentre;
            zombiePads.Clear();   // the ground (built last) paints bare yards around these

            var start = new GameObject("ZombieStart");
            start.transform.SetParent(zroot, false);
            start.transform.localPosition = new Vector3(0f, ZombieGroundY + 0.1f, 0f);

            // town: houses on the grid, square in the middle
            var buildings = new GameObject("Buildings").transform; buildings.SetParent(zroot, false);
            var cells = new List<Vector2Int>();
            for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++) if (i != 0 || j != 0) cells.Add(new Vector2Int(i, j));
            for (int k = cells.Count - 1; k > 0; k--) { int r = Random.Range(0, k + 1); (cells[k], cells[r]) = (cells[r], cells[k]); }
            var houses = new List<string>();
            foreach (var (model, count) in new[] { ("SM_House_2F_A", 3), ("SM_House_2F_B", 3), ("SM_House_Ruined", 3), ("SM_House_1F_A", 3), ("SM_House_1F_B", 2) })
                for (int c = 0; c < count; c++) houses.Add(model);
            int cell = 0;
            foreach (var model in houses)
            {
                var g = cells[cell++];
                var lp = new Vector3(g.x * ZombieBlock + Random.Range(-1.5f, 1.5f), 0f, g.y * ZombieBlock + Random.Range(-1.5f, 1.5f));
                PlaceZombieBuilding(model, lp, 90f * Random.Range(0, 4) + Random.Range(-5f, 5f), buildings, true);
            }
            // the empty blocks: walls to fight around, a pine or two
            var trees = new GameObject("Trees").transform; trees.SetParent(zroot, false);
            for (; cell < cells.Count; cell++)
            {
                var g = cells[cell];
                var c = new Vector3(g.x * ZombieBlock, 0f, g.y * ZombieBlock);
                for (int w = 0; w < 2; w++)
                {
                    var off = new Vector3(Random.Range(-6f, 6f), 0f, Random.Range(-6f, 6f));
                    PlaceZombieBuilding(Random.value < 0.6f ? "SM_Wall_Broken" : "SM_Wall_Compound", c + off, Random.Range(0, 4) * 90f + Random.Range(-15f, 15f), buildings, false);
                }
                for (int p = 0; p < 2; p++)
                    ZombiePine(c + new Vector3(Random.Range(-8f, 8f), 0f, Random.Range(-8f, 8f)), trees, Random.Range(0.9f, 1.25f));
            }
            // walls along the square
            for (int s = 0; s < 4; s++)
            {
                var q = Quaternion.Euler(0f, s * 90f, 0f);
                PlaceZombieBuilding(Random.value < 0.5f ? "SM_Wall_Compound" : "SM_Wall_Broken", q * new Vector3(Random.Range(-4f, 4f), 0f, 8.5f), s * 90f, buildings, false);
            }

            // the forest ring: dense pines between the walls and the edge of the ground
            int placed = 0;
            var spots = new List<Vector2>();
            for (int tries = 0; tries < 6000 && placed < 520; tries++)
            {
                var p = new Vector2(Random.Range(-ZombieGroundHalf + 4f, ZombieGroundHalf - 4f), Random.Range(-ZombieGroundHalf + 4f, ZombieGroundHalf - 4f));
                float cheb = Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y));
                if (cheb < ZombieHalfSize - 1f) continue;
                bool crowded = false;
                float gap = cheb < ZombieHalfSize + 12f ? 3.2f : 4.5f;   // thickest right behind the walls
                foreach (var o in spots) if ((o - p).sqrMagnitude < gap * gap) { crowded = true; break; }
                if (crowded) continue;
                spots.Add(p);
                ZombiePine(new Vector3(p.x, 0f, p.y), trees, Random.Range(0.95f, 1.45f));
                placed++;
            }

            // invisible walls
            var walls = new GameObject("Boundary").transform; walls.SetParent(zroot, false);
            for (int s = 0; s < 4; s++)
            {
                var w = new GameObject("Wall_" + s);
                w.transform.SetParent(walls, false);
                var dir = Quaternion.Euler(0f, s * 90f, 0f) * Vector3.forward;
                w.transform.localPosition = dir * (ZombieHalfSize + 1f) + Vector3.up * 8f;
                w.transform.localRotation = Quaternion.LookRotation(dir);
                w.AddComponent<BoxCollider>().size = new Vector3(ZombieHalfSize * 2f + 4f, 20f, 2f);
            }
            BuildZombieGround(zroot);
            Debug.Log($"ArenaBuilder: zombie town at {ZombieCentre}: {houses.Count} houses, {placed} forest pines.");
        }

        static void PlaceZombieBuilding(string model, Vector3 local, float yaw, Transform parent, bool house)
        {
            var prefab = TownAssets.Building(model);
            if (prefab == null) { Debug.LogWarning("ArenaBuilder: missing town model " + model); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = model;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(local.x, ZombieHeight(local.x, local.z), local.z);
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * TownScale;
            AddMeshColliders(go, false);
            MakeStatic(go);
            if (house) AddTownLoot(go);
            var fp = TownFootprint.TryGetValue(model, out var f) ? f : new Vector2(5f, 5f);
            // yard: a ring of ~2 m of bare ground just outside the walls (the house covers the rest of the circle)
            zombiePads.Add(new Vector3(local.x, local.z, fp.magnitude * 0.5f * TownScale * (house ? 0.72f : 0.45f)));
        }

        static readonly List<Vector3> zombiePads = new List<Vector3>();   // local x, z, yard radius

        static void ZombiePine(Vector3 local, Transform parent, float scale)
        {
            string[] pines = { "Pine_5", "Pine_2", "Pine_4", "Pine_1", "Pine_3" };
            var world = ZombieCentre + new Vector3(local.x, ZombieHeight(local.x, local.z) - 0.1f, local.z);
            var tree = Spawn(Pick(pines, new[] { 3f, 2f, 2f, 1f, 1f }), world, Random.value * 360f, scale, parent, false);
            if (Random.value < 0.25f) SwapMaterial(tree, "Leaves_Pine", "Leaves_Pine_Light");
            var cap = tree.AddComponent<CapsuleCollider>();
            cap.radius = 0.4f; cap.height = 4f; cap.center = new Vector3(0f, 2f, 0f);
        }

        static void BuildZombieGround(Transform zroot)
        {
            const float cell = 2f;
            int n = Mathf.RoundToInt(ZombieGroundHalf * 2f / cell);
            var verts = new Vector3[(n + 1) * (n + 1)];
            var uvs = new Vector2[verts.Length];
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float x = -ZombieGroundHalf + i * cell, z = -ZombieGroundHalf + j * cell;
                    verts[j * (n + 1) + i] = new Vector3(x, ZombieHeight(x, z), z);
                    uvs[j * (n + 1) + i] = new Vector2((x + ZombieGroundHalf) / (2f * ZombieGroundHalf), (z + ZombieGroundHalf) / (2f * ZombieGroundHalf));
                }
            var tris = new List<int>(n * n * 6);
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(d); tris.Add(a); tris.Add(d); tris.Add(b);
                }
            var mesh = new Mesh { name = "ZombieGround", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts; mesh.uv = uvs; mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            SaveAsset(mesh, "Assets/Art/Generated/ZombieGround.asset");

            // one baked texture: grass blended with dirt streets, the square, yards, patches and the forest floor (TerrainBaker.BakeZombie)
            var albedo = TerrainBaker.BakeZombie(ZombieGroundHalf, ZombieBlock, ZombieHalfSize, zombiePads);
            var mat = LoadOrCreate("Assets/Art/Arena/Materials/Zombie_Ground.mat", "Universal Render Pipeline/Lit");
            mat.SetTexture("_BaseMap", albedo);
            mat.SetTextureScale("_BaseMap", Vector2.one);
            mat.SetColor("_BaseColor", new Color(0.92f, 0.94f, 0.9f));   // a touch greyer: a gloomier town
            mat.SetFloat("_Smoothness", 0f);
            EditorUtility.SetDirty(mat);
            var ground = new GameObject("Ground");
            ground.transform.SetParent(zroot, false);
            ground.AddComponent<MeshFilter>().sharedMesh = mesh;
            ground.AddComponent<MeshRenderer>().sharedMaterial = mat;
            ground.AddComponent<MeshCollider>().sharedMesh = mesh;
            MakeStatic(ground);
        }
    }
}
