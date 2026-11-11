using System.IO;
using System.Linq;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CatBattleground
{
    // Menu: CatBattleground > Build Phase 1 Test Scene
    // Builds the animator controller, the player prefab and a small shooting-range scene.
    public static class Phase1Setup
    {
        const string ScenePath = "Assets/Scenes/Phase1_Test.unity";
        const string SourceCharacter = "Cat_Army";

        [MenuItem("CatBattleground/Build Phase 1 Test Scene")]
        public static void Build()
        {
            var armyPlayer = BuildAllPrefabs();
            BuildScene(armyPlayer);
            Debug.Log("CatBattleground: Phase 1 scene ready. Open Assets/Scenes/Phase1_Test.unity and press Play.");
        }

        /// <summary>Character prefabs, animator + per-character overrides and the five networked Player_* prefabs. Returns Player_Army.</summary>
        public static GameObject BuildAllPrefabs()
        {
            CharacterImportSetup.Run();   // humanoid rig, clip names/loops, materials, character prefabs

            var controller = AnimatorBuilder.Build();
            var controllers = AnimatorBuilder.BuildOverrides(controller);
            GameObject armyPlayer = null;
            foreach (var kv in controllers)
            {
                var prefab = BuildPlayerPrefab(kv.Key, kv.Value);
                if (kv.Key == SourceCharacter) armyPlayer = prefab;
            }
            return armyPlayer;
        }

        static GameObject BuildPlayerPrefab(string character, RuntimeAnimatorController controller)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Characters/{character}.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.name = "Player_" + character.Replace("Cat_", "");

            var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            var cc = instance.AddComponent<CharacterController>();
            cc.height = 1.75f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.875f + cc.skinWidth, 0f);   // feet on the ground: the controller hovers skinWidth
            cc.stepOffset = 0.4f;   // the BattlefieldTown stairs are 0.34-0.37 m at ArenaBuilder.TownScale 1.7

            instance.AddComponent<Health>();
            instance.AddComponent<PlayerAnimator>();
            instance.AddComponent<PlayerController>();
            instance.AddComponent<PlayerShooter>();
            instance.AddComponent<BombThrower>();   // G: bomb (2026-10-27)
            instance.AddComponent<PlayerAnimationTester>();
            instance.AddComponent<SimpleHud>();
            instance.AddComponent<SpectateController>();

            // Networking. Offline (Phase 1 scene) the NetworkObject simply stays unspawned and everything runs locally.
            instance.AddComponent<NetworkObject>();
            var nt = instance.AddComponent<NetworkTransform>();
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;   // owner moves the cat, others interpolate
            nt.SyncPositionX = nt.SyncPositionY = nt.SyncPositionZ = true;
            nt.SyncRotAngleX = false; nt.SyncRotAngleY = true; nt.SyncRotAngleZ = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            nt.Interpolate = true;
            var na = instance.AddComponent<NetworkAnimator>();
            na.Animator = animator;
            na.AuthorityMode = NetworkAnimator.AuthorityModes.Owner;
            instance.AddComponent<NetworkPlayer>();
            instance.AddComponent<NetworkPlayerHealth>();
            instance.AddComponent<PlayerDrop>();
            // Trigger capsule the server raycasts against (the CharacterController is off on remote copies).
            var hitbox = instance.AddComponent<CapsuleCollider>();
            hitbox.isTrigger = true;
            hitbox.height = 1.75f; hitbox.radius = 0.3f; hitbox.center = new Vector3(0f, 0.875f, 0f);

            EnsureFolder("Assets/Prefabs");
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, $"Assets/Prefabs/{instance.name}.prefab");
            Object.DestroyImmediate(instance);
            return prefab;
        }

        static void BuildScene(GameObject playerPrefab)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(10f, 1f, 10f);
            ground.GetComponent<Renderer>().sharedMaterial = ColorMaterial("Ground", new Color(0.45f, 0.6f, 0.4f));

            var cover = ColorMaterial("Cover", new Color(0.55f, 0.5f, 0.45f));
            CreateBox("Wall_A", new Vector3(0f, 1.5f, 8f), new Vector3(8f, 3f, 0.5f), cover);
            CreateBox("Wall_B", new Vector3(-9f, 1f, 4f), new Vector3(0.5f, 2f, 6f), cover);
            CreateBox("Crate_1", new Vector3(5f, 0.5f, 3f), Vector3.one, cover);
            CreateBox("Crate_2", new Vector3(6.2f, 0.5f, 3f), Vector3.one, cover);
            CreateBox("Crate_3", new Vector3(5.6f, 1.5f, 3f), Vector3.one, cover);
            var ramp = CreateBox("Ramp", new Vector3(-4f, 0.6f, -6f), new Vector3(3f, 0.2f, 6f), cover);
            ramp.transform.rotation = Quaternion.Euler(-12f, 0f, 0f);

            var dummyMat = ColorMaterial("Dummy", new Color(0.85f, 0.3f, 0.25f));
            for (int i = 0; i < 5; i++)
                CreateDummy($"Dummy_{i + 1}", new Vector3(-8f + i * 4f, 1f, 18f), dummyMat);
            CreateDummy("Dummy_Far", new Vector3(0f, 1f, 45f), dummyMat);

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            player.transform.position = new Vector3(0f, 0.1f, -4f);

            var cam = Camera.main != null ? Camera.main.gameObject : new GameObject("Main Camera", typeof(Camera));
            cam.tag = "MainCamera";
            var follow = cam.GetComponent<ThirdPersonCamera>() ?? cam.AddComponent<ThirdPersonCamera>();
            follow.SetTarget(player.GetComponent<PlayerController>());

            // Wire HUD references (serialized private fields).
            var hud = player.GetComponent<SimpleHud>();
            var so = new SerializedObject(hud);
            so.FindProperty("health").objectReferenceValue = player.GetComponent<Health>();
            so.FindProperty("shooter").objectReferenceValue = player.GetComponent<PlayerShooter>();
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
        }

        static GameObject CreateBox(string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static void CreateDummy(string name, Vector3 pos, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.transform.position = pos;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.AddComponent<Health>();
            go.AddComponent<TargetDummy>();
        }

        static Material ColorMaterial(string name, Color color)
        {
            EnsureFolder("Assets/Art/Materials");
            var path = $"Assets/Art/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(mat);
            return mat;
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
