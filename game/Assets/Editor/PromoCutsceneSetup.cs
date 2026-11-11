using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CatBattleground
{
    // Menu: CatBattleground > Cinematic
    // Builds Assets/Scenes/PromoCutscene.unity from the Phase 2 arena (copy of the scene without the network / match objects)
    // with a PromoCutscene director that plays and records the promo film. Make the video afterwards with
    //   python Tools/Cinematic/make_promo.py <recording folder>   (titles, letterbox, music + SFX, MP4 through Blender).
    public static class PromoCutsceneSetup
    {
        const string SourceScene = "Assets/Scenes/Phase2_Network.unity";
        const string ScenePath = "Assets/Scenes/PromoCutscene.unity";

        // catalog id -> character asset name (Orange is hidden from the game, so it is not in the film either)
        static readonly (string id, string character)[] Cast =
        {
            ("army", "Cat_Army"), ("knight", "Cat_Knight"), ("ninja", "Cat_Ninja"), ("black", "Cat_Black"), ("gold", "Cat_Gold"),
            ("soldier", "Cat_Soldier"), ("white", "Cat_White"), ("baby", "Cat_Baby"), ("nobi", "Cat_Nobi"),
        };

        [MenuItem("CatBattleground/Cinematic/Build Promo Cutscene Scene")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null)
            {
                EditorUtility.DisplayDialog("Promo cutscene", "Run CatBattleground > Build Phase 2 Multiplayer Scene first (the film is shot in that arena).", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AssetDatabase.DeleteAsset(ScenePath);
            AssetDatabase.CopyAsset(SourceScene, ScenePath);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // no networking, HUD or match in the film
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

            var director = new GameObject("PromoCutscene").AddComponent<PromoCutscene>();
            var members = new List<PromoCutscene.CastMember>();
            foreach (var (id, character) in Cast)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Characters/{character}.prefab");
                if (model == null) { Debug.LogWarning("[Promo] missing character prefab " + character); continue; }
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>($"Assets/Animations/CatPlayer_{character}.overrideController")
                                 ?? AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorBuilder.ControllerPath);
                members.Add(new PromoCutscene.CastMember { id = id, model = model, controller = controller });
            }
            director.cast = members.ToArray();
            EditorUtility.SetDirty(director);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Promo] scene ready ({members.Count} cats): {ScenePath}. Menu CatBattleground > Cinematic > Record Promo (or press Play with 'record' off to watch).");
        }

        [MenuItem("CatBattleground/Cinematic/Record Promo (1920x1080, 30 fps)")]
        public static void Record() => Play(true);

        [MenuItem("CatBattleground/Cinematic/Preview Promo (no recording)")]
        public static void Preview() => Play(false);

        static void Play(bool record)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) Build();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = Object.FindAnyObjectByType<PromoCutscene>();
            if (director == null) { Debug.LogError("[Promo] no PromoCutscene in the scene - rebuild it"); return; }
            director.record = record;
            EditorUtility.SetDirty(director);
            EditorSceneManager.SaveScene(scene);
            EditorApplication.isPlaying = true;
        }
    }
}
