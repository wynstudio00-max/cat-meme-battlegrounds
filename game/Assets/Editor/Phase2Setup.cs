using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CatBattleground
{
    // Menu: CatBattleground > Build Phase 2 Multiplayer Scene
    // Builds the networked player prefabs, the loot prefabs, the match system and a low-poly island arena with
    // 16 spawn points, a NetworkManager and the connect menu.
    public static class Phase2Setup
    {
        const string ScenePath = "Assets/Scenes/Phase2_Network.unity";
        const string PrefabListPath = "Assets/DefaultNetworkPrefabs.asset";
        const string PlayerPrefabPath = "Assets/Prefabs/Player_Army.prefab";

        [MenuItem("CatBattleground/Build Phase 2 Multiplayer Scene")]
        public static void Build()
        {
            Phase1Setup.BuildAllPrefabs();
            var catalog = CatalogBuilder.Build();
            var playerPrefab = catalog.Original != null ? catalog.Original.playerPrefab : AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            var loot = MatchSetup.BuildLoot();
            var zombies = ZombieSetup.BuildZombies();
            var matchSystem = MatchSetup.BuildMatchSystem(loot, catalog, zombies);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            ArenaBuilder.Build();   // terrain, props, spawn points, loot points, lighting
            BuildMinimap();
            BuildNetworkManager(playerPrefab, catalog, loot, matchSystem, zombies);

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 40f, -120f);
                cam.transform.rotation = Quaternion.Euler(22f, 0f, 0f);   // menu view until a player spawns
                cam.farClipPlane = 380f;
                if (cam.GetComponent<ThirdPersonCamera>() == null) cam.gameObject.AddComponent<ThirdPersonCamera>();
            }

            EditorSceneManager.SaveScene(scene, ScenePath);   // the NavMesh asset is stored next to the saved scene
            BakeNavMesh();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log("CatBattleground: Phase 2 scene ready (Assets/Scenes/Phase2_Network.unity). Press Play, then Host / Join.");
        }

        // Pre-rendered top-down map of the island; MinimapHud draws the zone and the player on top of it at runtime.
        static void BuildMinimap()
        {
            var texture = MinimapBaker.Bake();
            var go = new GameObject("Minimap");
            var hud = go.AddComponent<MinimapHud>();
            var so = new SerializedObject(hud);
            so.FindProperty("map").objectReferenceValue = texture;
            so.FindProperty("worldExtent").floatValue = MinimapBaker.Extent;
            var zombieMap = BakeZombieMinimap(out float zombieExtent);
            if (zombieMap != null)
            {
                so.FindProperty("zombieMap").objectReferenceValue = zombieMap;
                so.FindProperty("zombieCentre").vector3Value = ArenaBuilder.ZombieCentre;
                so.FindProperty("zombieExtent").floatValue = zombieExtent;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Top-down picture of the Zombie Survival town (ArenaBuilder.Zombie): an orthographic camera straight down, north (+z) up.
        static Texture2D BakeZombieMinimap(out float extent)
        {
            extent = ArenaBuilder.ZombieHalfSize + 4f;
            if (GameObject.Find("ZombieArena") == null) return null;
            const int size = 512;
            const string path = "Assets/Art/Generated/Minimap_Zombie.png";
            var go = new GameObject("ZombieMinimapCam");
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            Texture2D result = null;
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = extent; cam.aspect = 1f;
                cam.transform.SetPositionAndRotation(ArenaBuilder.ZombieCentre + Vector3.up * 150f, Quaternion.Euler(90f, 0f, 0f));
                cam.nearClipPlane = 1f; cam.farClipPlane = 400f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.16f, 0.24f, 0.14f);
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path);
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                imp.textureType = TextureImporterType.Default; imp.mipmapEnabled = false; imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = 512; imp.textureCompression = TextureImporterCompression.Compressed;
                imp.crunchedCompression = true; imp.compressionQuality = 85;
                imp.SaveAndReimport();
                result = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            catch (System.Exception e) { Debug.LogWarning("Zombie minimap bake failed: " + e.Message); }
            finally
            {
                Object.DestroyImmediate(go);
                rt.Release(); Object.DestroyImmediate(rt);
            }
            return result;
        }

        // Walkable surface for the bots (terrain, house floors, props as obstacles).
        static void BakeNavMesh()
        {
            var arena = GameObject.Find("Arena");
            if (arena == null) return;
            SetAgentType(0.3f, 1.8f);
            var surface = arena.GetComponent<NavMeshSurface>() ?? arena.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;   // only colliders block bots: foliage cards must not
            surface.BuildNavMesh();
            EditorUtility.SetDirty(surface);
        }

        // The default Humanoid agent (radius 0.5, height 2) erodes too much of the BattlefieldTown houses: the head of 2F_B's
        // stairs sits in a corner and its NavMesh did not join the upper floor, so zombies stopped at the top (2026-10-27).
        // Bake for the cats' own size instead (capsule radius 0.3, 1.75 m). Edits the project's agent type 0 (Humanoid).
        static void SetAgentType(float radius, float height)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            var list = so.FindProperty("m_Settings");
            if (list == null || list.arraySize == 0) return;
            var agent = list.GetArrayElementAtIndex(0);
            agent.FindPropertyRelative("agentRadius").floatValue = radius;
            agent.FindPropertyRelative("agentHeight").floatValue = height;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildNetworkManager(GameObject playerPrefab, CharacterCatalog catalog, MatchSetup.LootPrefabs loot, GameObject matchSystem, GameObject[] zombies)
        {
            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();
            transport.UseWebSockets = true;   // browser (WebGL) clients can only use WebSocket, so everyone does
            transport.SetConnectionData("127.0.0.1", 7777);

            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.PlayerPrefab = playerPrefab;
            nm.NetworkConfig.ConnectionApproval = true;       // NetworkBootstrap picks the spawn point
            nm.NetworkConfig.EnableSceneManagement = false;   // single arena scene for now
            nm.NetworkConfig.TickRate = 30;

            // Register every network prefab: all characters, the loot pickups and the match system.
            AssetDatabase.DeleteAsset(PrefabListPath);   // rebuilt from scratch so removed prefabs (old weapon loot) do not linger as null entries
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(list, PrefabListPath);
            void Register(GameObject prefab)
            {
                if (prefab != null && !list.Contains(prefab)) list.Add(new NetworkPrefab { Prefab = prefab });
            }
            foreach (var entry in catalog.entries) Register(entry.playerPrefab);
            foreach (var prefab in loot.All()) Register(prefab);
            foreach (var prefab in zombies) Register(prefab);
            Register(matchSystem);
            EditorUtility.SetDirty(list);
            if (!nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(list))
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);

            var bootstrap = go.AddComponent<NetworkBootstrap>();
            var so = new SerializedObject(bootstrap);
            so.FindProperty("catalog").objectReferenceValue = catalog;
            so.FindProperty("matchSystemPrefab").objectReferenceValue = matchSystem;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
    }
}
