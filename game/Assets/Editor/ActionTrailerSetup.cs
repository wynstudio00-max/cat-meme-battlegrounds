using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CatBattleground
{
    // Menu: CatBattleground > Cinematic > ... Action Trailer (2026-10-31)
    // Builds Assets/Scenes/ActionTrailer.unity from the Phase 2 arena (the BR island with its towns + the zombie town north of it)
    // with an ActionTrailer director. Record it, then make the video with
    //   python Tools/Cinematic/make_trailer.py <recording folder>   (titles, flashes, action score + SFX, MP4 through Blender).
    public static class ActionTrailerSetup
    {
        const string SourceScene = "Assets/Scenes/Phase2_Network.unity";
        const string ScenePath = "Assets/Scenes/ActionTrailer.unity";
        const string ZombieModel = "Assets/Art/Zombies/ZombieCat.fbx";
        const string ZombieController = "Assets/Animations/Zombie.controller";

        static readonly (string id, string character)[] Cast =
        {
            ("army", "Cat_Army"), ("knight", "Cat_Knight"), ("ninja", "Cat_Ninja"), ("soldier", "Cat_Soldier"),
            ("black", "Cat_Black"), ("gold", "Cat_Gold"),
        };

        [MenuItem("CatBattleground/Cinematic/Build Action Trailer Scene")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null)
            {
                EditorUtility.DisplayDialog("Action trailer", "Run CatBattleground > Build Phase 2 Multiplayer Scene first (the trailer is shot in that arena).", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AssetDatabase.DeleteAsset(ScenePath);
            AssetDatabase.CopyAsset(SourceScene, ScenePath);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.GetComponentInChildren<NetworkManager>(true) != null || go.GetComponentInChildren<NetworkObject>(true) != null
                    || go.GetComponentInChildren<MinimapHud>(true) != null || go.GetComponentInChildren<NetworkBootstrap>(true) != null)
                    Object.DestroyImmediate(go);
            }
            var cam = Camera.main;
            if (cam != null)
            {
                var tpc = cam.GetComponent<ThirdPersonCamera>();
                if (tpc != null) Object.DestroyImmediate(tpc);
            }

            var director = new GameObject("ActionTrailer").AddComponent<ActionTrailer>();
            var members = new List<ActionTrailer.CastMember>();
            foreach (var (id, character) in Cast)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Characters/{character}.prefab");
                if (model == null) { Debug.LogWarning("[Trailer] missing character prefab " + character); continue; }
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>($"Assets/Animations/CatPlayer_{character}.overrideController")
                                 ?? AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorBuilder.ControllerPath);
                members.Add(new ActionTrailer.CastMember { id = id, model = model, controller = controller });
            }
            director.cast = members.ToArray();
            director.zombieModel = AssetDatabase.LoadAssetAtPath<GameObject>(ZombieModel);
            director.zombieController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ZombieController);
            if (director.zombieModel == null || director.zombieController == null)
                Debug.LogWarning("[Trailer] zombie model / controller missing - run Build Phase 2 Multiplayer Scene (ZombieSetup) first");
            EditorUtility.SetDirty(director);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Trailer] scene ready ({members.Count} cats): {ScenePath}. Menu CatBattleground > Cinematic > Record Action Trailer.");
        }

        [MenuItem("CatBattleground/Cinematic/Record Action Trailer (1920x1080, 30 fps)")]
        public static void Record() => Play(true);

        [MenuItem("CatBattleground/Cinematic/Preview Action Trailer (no recording)")]
        public static void Preview() => Play(false);

        static void Play(bool record)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) Build();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = Object.FindAnyObjectByType<ActionTrailer>();
            if (director == null) { Debug.LogError("[Trailer] no ActionTrailer in the scene - rebuild it"); return; }
            director.record = record;
            EditorUtility.SetDirty(director);
            EditorSceneManager.SaveScene(scene);
            EditorApplication.isPlaying = true;
        }
    }
}
