using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>Builds the networked loot prefabs (medkit, armor, ammo; weapons are fixed per character, not loot) and the MatchSystem prefab.</summary>
    public static class MatchSetup
    {
        const string LootDir = "Assets/Prefabs/Loot";
        const string MatchPrefabPath = "Assets/Prefabs/MatchSystem.prefab";

        public class LootPrefabs
        {
            public GameObject medkit, armor, ammo;
            public IEnumerable<GameObject> All() { yield return medkit; yield return armor; yield return ammo; }
        }

        // ---------------------------------------------------------------- loot
        public static LootPrefabs BuildLoot()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(LootDir)) AssetDatabase.CreateFolder("Assets/Prefabs", "Loot");

            // weapons used to be loot; remove the old prefabs so nothing refers to them
            foreach (var old in new[] { "Loot_Weapon_SMG", "Loot_Weapon_Heavy", "Loot_Weapon_Sniper" }) AssetDatabase.DeleteAsset($"{LootDir}/{old}.prefab");

            var white = Lit("Loot_White", new Color(0.95f, 0.95f, 0.95f));
            var red = Lit("Loot_Red", new Color(0.85f, 0.15f, 0.15f));
            var blue = Lit("Loot_Blue", new Color(0.25f, 0.5f, 0.95f));
            var yellow = Lit("Loot_Yellow", new Color(0.95f, 0.8f, 0.2f));
            var dark = Lit("Loot_Dark", new Color(0.15f, 0.16f, 0.18f));

            var result = new LootPrefabs();
            bool models = LootAssets.Prepare();   // Blender models in Assets/Art/Loot (medkit, ammo crate, armor crate)

            // 2026-10-28 (user): no light beams; the items lie on the floor like in PUBG (y 0 = the floor, LootVisual settles them)
            result.medkit = BuildLootPrefab("Loot_Medkit", LootType.Medkit, 40f, root =>
            {
                if (models && Model(root, "Medkit")) return;
                Box(root, "Case", white, new Vector3(0f, 0.11f, 0f), new Vector3(0.5f, 0.22f, 0.36f));
                Box(root, "CrossA", red, new Vector3(0f, 0.225f, 0f), new Vector3(0.26f, 0.012f, 0.08f));   // red cross on the lid
                Box(root, "CrossB", red, new Vector3(0f, 0.225f, 0f), new Vector3(0.08f, 0.012f, 0.26f));
                Box(root, "Handle", dark, new Vector3(0f, 0.25f, 0f), new Vector3(0.18f, 0.03f, 0.04f));
            });

            result.armor = BuildLootPrefab("Loot_Armor", LootType.Armor, 50f, root =>
            {
                if (models && Model(root, "ArmorBox")) return;
                // a vest lying flat
                Box(root, "Plate", blue, new Vector3(0f, 0.05f, 0f), new Vector3(0.5f, 0.1f, 0.6f));
                Box(root, "Inner", white, new Vector3(0f, 0.105f, 0.02f), new Vector3(0.3f, 0.02f, 0.4f));
                Box(root, "StrapL", dark, new Vector3(-0.17f, 0.11f, -0.27f), new Vector3(0.08f, 0.03f, 0.14f));
                Box(root, "StrapR", dark, new Vector3(0.17f, 0.11f, -0.27f), new Vector3(0.08f, 0.03f, 0.14f));
            });

            result.ammo = BuildLootPrefab("Loot_Ammo", LootType.Ammo, 60f, root =>
            {
                if (models && Model(root, "AmmoBox")) return;
                Box(root, "Box", yellow, new Vector3(0f, 0.11f, 0f), new Vector3(0.45f, 0.22f, 0.3f));
                Box(root, "Strip", dark, new Vector3(0f, 0.11f, 0.152f), new Vector3(0.38f, 0.07f, 0.01f));
                Box(root, "Lid", dark, new Vector3(0f, 0.225f, 0f), new Vector3(0.47f, 0.012f, 0.32f));
            });

            AssetDatabase.SaveAssets();
            return result;
        }

        static GameObject BuildLootPrefab(string name, LootType type, float amount, System.Action<GameObject> buildVisual)
        {
            var root = new GameObject(name);
            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);
            buildVisual(visual);

            root.AddComponent<NetworkObject>();
            var item = root.AddComponent<LootItem>();
            var so = new SerializedObject(item);
            so.FindProperty("type").enumValueIndex = (int)type;
            so.FindProperty("amount").floatValue = amount;
            so.ApplyModifiedPropertiesWithoutUndo();

            var trigger = root.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 1.1f;
            trigger.center = new Vector3(0f, 0.6f, 0f);
            var body = root.AddComponent<Rigidbody>();   // makes trigger events fire against the player's trigger capsule
            body.isKinematic = true;
            body.useGravity = false;

            var vis = root.AddComponent<LootVisual>();
            var vso = new SerializedObject(vis);
            vso.FindProperty("visual").objectReferenceValue = visual.transform;
            vso.ApplyModifiedPropertiesWithoutUndo();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{LootDir}/{name}.prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }

        // The Blender model (origin at its bottom centre) as the item's visual; false when it is missing (the primitive shapes follow).
        static bool Model(GameObject parent, string name)
        {
            var prefab = LootAssets.Model(name);
            if (prefab == null) return false;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);   // the root's trigger does the pickup
            return true;
        }

        static void Box(GameObject parent, string name, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Object.DestroyImmediate(go.GetComponent<Collider>());
        }

        // ---------------------------------------------------------------- match system
        public static GameObject BuildMatchSystem(LootPrefabs loot, CharacterCatalog catalog, GameObject[] zombies)
        {
            var root = new GameObject("MatchSystem");
            root.AddComponent<NetworkObject>();
            root.AddComponent<ZoneController>();
            root.AddComponent<LootSpawner>();
            root.AddComponent<MatchManager>();
            root.AddComponent<MatchHud>();
            var waves = root.AddComponent<ZombieWaveManager>();

            var wavesSo = new SerializedObject(waves);
            var zombieList = wavesSo.FindProperty("zombiePrefabs");
            zombieList.arraySize = zombies.Length;
            for (int i = 0; i < zombies.Length; i++) zombieList.GetArrayElementAtIndex(i).objectReferenceValue = zombies[i];
            wavesSo.ApplyModifiedPropertiesWithoutUndo();

            var matchSo = new SerializedObject(root.GetComponent<MatchManager>());
            matchSo.FindProperty("catalog").objectReferenceValue = catalog;
            matchSo.ApplyModifiedPropertiesWithoutUndo();

            var zoneSo = new SerializedObject(root.GetComponent<ZoneController>());
            zoneSo.FindProperty("wallMaterial").objectReferenceValue = ZoneWallMaterial();
            zoneSo.FindProperty("ringMaterial").objectReferenceValue = Unlit("Zone_Ring", new Color(1f, 1f, 1f));
            zoneSo.ApplyModifiedPropertiesWithoutUndo();

            var table = new (GameObject prefab, float weight)[]
            {
                (loot.medkit, 18f), (loot.armor, 12f), (loot.ammo, 30f),
            };
            var lootSo = new SerializedObject(root.GetComponent<LootSpawner>());
            var list = lootSo.FindProperty("table");
            list.arraySize = table.Length;
            for (int i = 0; i < table.Length; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("prefab").objectReferenceValue = table[i].prefab;
                element.FindPropertyRelative("weight").floatValue = table[i].weight;
            }
            lootSo.ApplyModifiedPropertiesWithoutUndo();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, MatchPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // Transparent double-sided blue wall (URP Unlit, transparent surface set up through its properties).
        static Material ZoneWallMaterial()
        {
            const string path = "Assets/Art/Materials/Zone_Wall.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", new Color(0.25f, 0.55f, 1f, 0.28f));
            mat.SetFloat("_Surface", 1f);                      // transparent
            mat.SetFloat("_Blend", 0f);                        // alpha
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", 0f);                         // both sides
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------------------------------------------------------------- materials
        static Material Lit(string name, Color color)
        {
            var path = $"Assets/Art/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Unlit(string name, Color color)
        {
            var path = $"Assets/Art/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
