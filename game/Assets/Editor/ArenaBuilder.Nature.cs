using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    // Nature scattering for the arena, using the stylised nature pack in Assets/Art/Forest.
    public static partial class ArenaBuilder
    {
        // Everything is scaled by NatureDensity: lower it (e.g. 0.6) if the WebGL build runs too slowly.
        const float NatureDensity = 1f;

        // Counts below are written for the original 60 m island; they are scaled by the map area (MapScale squared).
        static int Count(int n) => Mathf.Max(1, Mathf.RoundToInt(n * NatureDensity * MapScale * MapScale));

        static void ScatterNature()
        {
            ResetWindMeshes();   // foliage meshes get a wind weight (ArenaBuilder.Wind)
            var trees = NewRoot("Trees"); var under = NewRoot("Undergrowth"); var stones = NewRoot("Rocks");
            var treePoints = new List<Vector2>();

            // --- trees: groves with open clearings, never on paths, pads or spawn points.
            // Pines only (2026-10-27, user): they take the count of the removed broadleaf / twisted / dead trees too.
            PlaceTrees(trees, treePoints, new[] { "Pine_5", "Pine_2", "Pine_4", "Pine_1", "Pine_3" }, new[] { 3f, 2f, 2f, 1f, 1f },
                       Count(65), 5.2f, 0.9f, 1.3f, 0.4f, "Leaves_Pine", new[] { "Leaves_Pine_Light" }, new[] { 0.25f });

            // --- bushes, ferns and plants hug the trees
            // (bushes removed 2026-10-27, user)
            ScatterGroup(new[] { "Fern_1" }, new[] { 1f }, Count(55), under, 0.4f, 1.8f, 0.28f, 0.5f);
            ScatterGroup(new[] { "Plant_1", "Plant_1_Big", "Plant_7", "Plant_7_Big", "Clover_1", "Clover_2" }, new[] { 2f, 1f, 2f, 1f, 2f, 2f }, Count(70), under, 0.2f, 1.6f, 0.6f, 1.0f);

            // --- no grass tufts (2026-10-27, user); the painted terrain texture is the lawn. ScatterClumps kept for later.

            // --- mushrooms in the shade (flowers removed 2026-10-27, user)
            ScatterGroup(new[] { "Mushroom_Common", "Mushroom_Laetiporus" }, new[] { 3f, 1f }, Count(14), under, 0.5f, 2.0f, 1.0f, 1.6f);

            // --- rocks and pebbles
            ScatterGroup(new[] { "Rock_Medium_1", "Rock_Medium_2", "Rock_Medium_3" }, new[] { 1f, 1f, 1f }, Count(36), stones, -0.2f, 2.5f, 0.8f, 1.5f, collider: true, batch: false);
            ScatterGroup(new[] { "Pebble_Round_1", "Pebble_Round_2", "Pebble_Round_3", "Pebble_Square_1", "Pebble_Square_2", "Pebble_Square_3" }, new[] { 1f, 1f, 1f, 1f, 1f, 1f }, Count(70), stones, -0.2f, 1.2f, 1.6f, 3.2f);

            // --- stepping stones leading away from the doors
            var steps = NewRoot("SteppingStones");
            string[] stepModels = { "RockPath_Round_Small_1", "RockPath_Square_Small_1", "RockPath_Round_Small_2", "RockPath_Square_Small_2", "RockPath_Round_Small_3", "RockPath_Square_Small_3" };
            foreach (var site in sites)
            {
                if (site.kind != "house" && site.kind != "ruin") continue;
                for (int i = 0; i < 2; i++)
                {
                    var p = Local(site.pos, site.yaw, Random.Range(-0.5f, 0.5f), 5.6f + i * 1.9f);
                    Spawn(stepModels[Random.Range(0, stepModels.Length)], new Vector3(p.x, Height(p.x, p.z) + 0.03f, p.z), Random.value * 360f, Random.Range(1.4f, 1.8f), steps, true);
                }
            }
        }

        static void PlaceTrees(Transform parent, List<Vector2> points, string[] models, float[] weights, int count, float spacing,
                               float scaleMin, float scaleMax, float colliderRadius, string leafMaterial, string[] variants, float[] variantChance)
        {
            int placed = 0, tries = 0;
            while (placed < count && tries++ < count * 300)
            {
                var p = RandomLand(60f * MapScale);
                float x = p.x, z = p.y;
                if (Random.value > Forest(x, z) * 0.95f + 0.05f) continue;
                if (PathDistance(x, z) < 3.4f || !FarFromSites(x, z, 1.5f) || Height(x, z) < 0.45f) continue;
                bool crowded = false;
                foreach (var q in points) if ((q - p).sqrMagnitude < spacing * spacing) { crowded = true; break; }
                if (crowded) continue;
                points.Add(p);

                var tree = Spawn(Pick(models, weights), new Vector3(x, Height(x, z) - 0.1f, z), Random.value * 360f, Random.Range(scaleMin, scaleMax), parent, false);
                if (leafMaterial != null && variants != null)
                    for (int v = 0; v < variants.Length; v++)
                        if (Random.value < variantChance[v]) { SwapMaterial(tree, leafMaterial, variants[v]); break; }

                var cap = tree.AddComponent<CapsuleCollider>();
                cap.radius = colliderRadius; cap.height = 4f; cap.center = new Vector3(0f, 2f, 0f);
                placed++;
            }
        }

        static void SwapMaterial(GameObject go, string from, string to)
        {
            var replacement = ForestAssets.Material(to);
            if (replacement == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i].name == from) { mats[i] = replacement; changed = true; }
                if (changed) r.sharedMaterials = mats;
            }
        }

        static Transform NewRoot(string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root);
            return t;
        }

        static Vector2 RandomLand(float maxRadius)
        {
            float angle = Random.value * Mathf.PI * 2f, dist = Mathf.Sqrt(Random.value) * maxRadius;
            return new Vector2(Mathf.Cos(angle) * dist, Mathf.Sin(angle) * dist);
        }

        static string Pick(string[] models, float[] weights)
        {
            float total = 0f; foreach (var w in weights) total += w;
            float r = Random.value * total;
            for (int i = 0; i < models.Length; i++) { r -= weights[i]; if (r <= 0f) return models[i]; }
            return models[models.Length - 1];
        }

        // forestBias: how much the spot prefers forest (+) or open meadow (-). meadowsOnly skips dense forest.
        static void ScatterGroup(string[] models, float[] weights, int count, Transform parent, float forestBias, float minPathDist,
                                 float scaleMin, float scaleMax, bool meadowsOnly = false, bool collider = false, bool batch = true)
        {
            int placed = 0, tries = 0;
            while (placed < count && tries++ < count * 40)
            {
                var p = RandomLand(61f * MapScale);
                float x = p.x, z = p.y;
                float h = Height(x, z);
                if (h < 0.35f || !FarFromSites(x, z, 0.2f)) continue;
                float forest = Forest(x, z);
                if (meadowsOnly && forest > 0.45f) continue;
                float chance = Mathf.Clamp01(0.55f + forestBias * (forest - 0.5f) * 2f);
                if (Random.value > chance) continue;
                if (PathDistance(x, z) < minPathDist * 0.55f) continue;       // keep the dirt road clear
                if (PadWeight(x, z) > 0.35f) continue;                        // not on the building pads
                var go = Spawn(Pick(models, weights), new Vector3(x, h - (collider ? 0.15f : 0.02f), z), Random.value * 360f, Random.Range(scaleMin, scaleMax), parent, batch);
                if (collider) AddMeshColliders(go, true);
                placed++;
            }
        }

        // A tuft: several small blades scattered tightly around one spot. Batched, so it costs few draw calls.
        static void ScatterClumps(string[] models, float[] weights, int clumps, Transform parent, int perClump, float spread,
                                  float scaleMin, float scaleMax, float forestBias)
        {
            int placed = 0, tries = 0;
            while (placed < clumps && tries++ < clumps * 30)
            {
                var p = RandomLand(61f * MapScale);
                float x = p.x, z = p.y;
                if (Height(x, z) < 0.35f || !FarFromSites(x, z, 0.2f)) continue;
                float chance = Mathf.Clamp01(0.6f + forestBias * (Forest(x, z) - 0.5f) * 2f);
                if (Random.value > chance) continue;
                if (PathDistance(x, z) < 1.0f || PadWeight(x, z) > 0.35f) continue;   // keep the dirt road and building pads clear
                for (int k = 0; k < perClump; k++)
                {
                    var o = Random.insideUnitCircle * spread;
                    float px = x + o.x, pz = z + o.y, ph = Height(px, pz);
                    if (ph < 0.3f || PathDistance(px, pz) < 0.8f) continue;
                    Spawn(Pick(models, weights), new Vector3(px, ph - 0.02f, pz), Random.value * 360f, Random.Range(scaleMin, scaleMax), parent, true);
                }
                placed++;
            }
        }

        static GameObject Spawn(string model, Vector3 pos, float yaw, float scale, Transform parent, bool batch)
        {
            var prefab = ForestAssets.Model(model);
            if (prefab == null) { Debug.LogWarning("ArenaBuilder: missing model " + model); return new GameObject("missing_" + model); }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            go.transform.localScale = Vector3.one * scale;
            ApplyWind(go, model);
            MakeStatic(go, batch);
            return go;
        }
    }
}
