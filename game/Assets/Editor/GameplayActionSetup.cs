using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CatBattleground
{
    // Menu: CatBattleground > Cinematic > ... Gameplay Action (2026-11-01)
    // Builds Assets/Scenes/GameplayAction.unity from the Phase 2 arena (its NavMesh comes along) with a GameplayAction director.
    // Record it, then make the video with
    //   python Tools/Cinematic/make_gameplay_action.py <recording folder>   (action words, music + SFX, MP4 through Blender).
    public static class GameplayActionSetup
    {
        const string SourceScene = "Assets/Scenes/Phase2_Network.unity";
        const string ScenePath = "Assets/Scenes/GameplayAction.unity";

        static readonly (string id, string character)[] Cast =
        {
            ("army", "Cat_Army"), ("soldier", "Cat_Soldier"), ("knight", "Cat_Knight"), ("black", "Cat_Black"),
            ("gold", "Cat_Gold"), ("ninja", "Cat_Ninja"), ("white", "Cat_White"), ("baby", "Cat_Baby"),
        };

        [MenuItem("CatBattleground/Cinematic/Build Gameplay Action Scene")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null)
            {
                EditorUtility.DisplayDialog("Gameplay action", "Run CatBattleground > Build Phase 2 Multiplayer Scene first (the film is shot in that arena).", "OK");
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

            var director = new GameObject("GameplayAction").AddComponent<GameplayAction>();
            var members = new List<GameplayAction.CastMember>();
            foreach (var (id, character) in Cast)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Characters/{character}.prefab");
                if (model == null) { Debug.LogWarning("[Gameplay] missing character prefab " + character); continue; }
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>($"Assets/Animations/CatPlayer_{character}.overrideController")
                                 ?? AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorBuilder.ControllerPath);
                members.Add(new GameplayAction.CastMember { id = id, model = model, controller = controller });
            }
            director.cast = members.ToArray();
            EditorUtility.SetDirty(director);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Gameplay] scene ready ({members.Count} cats): {ScenePath}. Menu CatBattleground > Cinematic > Record Gameplay Action.");
        }

        [MenuItem("CatBattleground/Cinematic/Record Gameplay Action (1920x1080, 30 fps)")]
        public static void Record() => Play(true);

        [MenuItem("CatBattleground/Cinematic/Preview Gameplay Action (no recording)")]
        public static void Preview() => Play(false);

        // Command-line recorder (Unity must be closed):
        //   Unity.exe -batchmode -quit -projectPath CatBattleground -buildTarget Win64 -executeMethod CatBattleground.GameplayActionSetup.BuildRecorder
        //   Build/GameplayRecorder/GameplayRecorder.exe -gameplayOut <folder>      (writes frames + cues.json, quits by itself)
        public const string RecorderExe = "../Build/GameplayRecorder/GameplayRecorder.exe";

        public static void BuildRecorder()
        {
            Build();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = Object.FindAnyObjectByType<GameplayAction>();
            if (director != null) { director.record = true; EditorUtility.SetDirty(director); EditorSceneManager.SaveScene(scene); }
            var report = UnityEditor.BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = RecorderExe,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log("[Gameplay] recorder build: " + report.summary.result + " -> " + RecorderExe);
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        static void Play(bool record)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) Build();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = Object.FindAnyObjectByType<GameplayAction>();
            if (director == null) { Debug.LogError("[Gameplay] no GameplayAction in the scene - rebuild it"); return; }
            director.record = record;
            EditorUtility.SetDirty(director);
            EditorSceneManager.SaveScene(scene);
            EditorApplication.isPlaying = true;
        }
    }
}
