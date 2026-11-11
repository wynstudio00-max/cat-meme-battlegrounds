using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

namespace CatBattleground
{
    /// <summary>
    /// Builds the networked zombie prefabs for Zombie Survival (called by Build Phase 2 Multiplayer Scene, after the
    /// character prefabs exist). Since 2026-10-25: Zombie_Cat from the user's Mixamo zombie cat (Assets/Art/Zombies/ZombieCat.fbx,
    /// Tools/BlenderAnim/build_zombie.py) with its own Zombie.controller + ZombieAnimator. Fallback when that FBX is missing:
    /// a few cat bodies without their weapon, every material tinted a sickly green, using the cat's own animator.
    /// </summary>
    public static class ZombieSetup
    {
        const string PrefabDir = "Assets/Prefabs/Zombies";
        const string MaterialDir = "Assets/Art/Materials/Zombie";
        static readonly string[] Bodies = { "Army", "Black", "Soldier" };
        static readonly Color Tint = new Color(0.58f, 0.82f, 0.46f);

        public static GameObject[] BuildZombies()
        {
            EnsureFolder("Assets/Prefabs", "Zombies");
            EnsureFolder("Assets/Art/Materials", "Zombie");

            // the user's zombie cat (2026-10-25) replaces the tinted cats when its FBX exists
            var zombieCat = BuildZombieCat();
            if (zombieCat != null) { AssetDatabase.SaveAssets(); return new[] { zombieCat }; }

            var result = new List<GameObject>();
            foreach (var body in Bodies)
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Characters/Cat_{body}.prefab");
                var player = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/Player_{body}.prefab");
                if (source == null || player == null) { Debug.LogWarning($"ZombieSetup: Cat_{body} / Player_{body} missing, skipped."); continue; }
                var prefab = BuildZombie(body, source, player.GetComponent<Animator>().runtimeAnimatorController);
                if (prefab != null) result.Add(prefab);
            }
            AssetDatabase.SaveAssets();
            if (result.Count == 0) Debug.LogWarning("ZombieSetup: no zombie prefabs built - Zombie Survival will not spawn anything.");
            return result.ToArray();
        }

        // ------------------------------------------------------------------ zombie cat (Mixamo zombie clips)
        const string ZombieModel = "Assets/Art/Zombies/ZombieCat.fbx";
        const string ZombieTex = "Assets/Art/Zombies/ZombieCat_";
        const string ZombieController = "Assets/Animations/Zombie.controller";
        const float ZombieModelHeight = 0.8692f;   // build_zombie.py HEIGHT (Blender metres); scaled to 1.75 m like the cats
        static readonly string[] ZombieClips = { "Idle", "Walk", "Run", "Crawl", "CrawlRun", "Attack", "Bite", "BiteLong", "NeckBite", "Death", "Dying", "Scream" };
        static readonly HashSet<string> ZombieLoops = new HashSet<string> { "Idle", "Walk", "Run", "Crawl", "CrawlRun", "Bite", "BiteLong" };

        static GameObject BuildZombieCat()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ZombieModel) == null) return null;
            ConfigureZombieModel();
            var mat = ZombieMaterial();
            var controller = BuildZombieController();
            if (controller == null) { Debug.LogWarning("ZombieSetup: ZombieCat.fbx has no clips - falling back to tinted cats."); return null; }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ZombieModel));
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "Zombie_Cat";
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true)) r.sharedMaterial = mat;

            var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;   // many zombies in a browser: skip bones off screen

            instance.AddComponent<Health>();
            AddZombieBody(instance, animator);
            instance.AddComponent<ZombieAnimator>();
            instance.AddComponent<Zombie>();

            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, $"{PrefabDir}/{instance.name}.prefab");
            Object.DestroyImmediate(instance);
            Debug.Log("ZombieSetup: built Zombie_Cat from the Mixamo zombie pack.");
            return prefab;
        }

        // GENERIC rig like the cats; clips "Armature|Zombie_Walk" -> "Walk"; loops closed in Blender (in place, last == first frame).
        static void ConfigureZombieModel()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(ZombieModel);
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.globalScale = 1.75f / ZombieModelHeight;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.motionNodeName = "";
            importer.animationCompression = ModelImporterAnimationCompression.Optimal;   // WebGL size
            importer.animationRotationError = 0.5f; importer.animationPositionError = 0.5f; importer.animationScaleError = 0.5f;
            var clips = new List<ModelImporterClipAnimation>();
            foreach (var c in importer.defaultClipAnimations)
            {
                var n = c.takeName.Split('|');
                var clean = n[n.Length - 1];
                if (clean.StartsWith("Zombie_")) clean = clean.Substring(7);
                if (System.Array.IndexOf(ZombieClips, clean) < 0) continue;
                c.name = clean;
                c.loopTime = ZombieLoops.Contains(clean);
                c.loopPose = false;
                clips.Add(c);
            }
            importer.clipAnimations = clips.ToArray();
            importer.SaveAndReimport();

            foreach (var (suffix, normal, linear) in new[] { ("BaseColor", false, false), ("Normal", true, false), ("MetallicSmoothness", false, true) })
            {
                if (!(AssetImporter.GetAtPath(ZombieTex + suffix + ".png") is TextureImporter ti)) continue;
                ti.maxTextureSize = 1024; ti.crunchedCompression = true; ti.compressionQuality = 50;
                if (normal) ti.textureType = TextureImporterType.NormalMap;
                if (linear) ti.sRGBTexture = false;
                ti.SaveAndReimport();
            }
        }

        static Material ZombieMaterial()
        {
            var path = $"{MaterialDir}/ZombieCat.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
            mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(ZombieTex + "BaseColor.png"));
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(ZombieTex + "Normal.png");
            if (normal != null) { mat.SetTexture("_BumpMap", normal); mat.EnableKeyword("_NORMALMAP"); }
            var metal = AssetDatabase.LoadAssetAtPath<Texture2D>(ZombieTex + "MetallicSmoothness.png");
            if (metal != null) { mat.SetTexture("_MetallicGlossMap", metal); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            mat.SetFloat("_Smoothness", 0.6f);   // the map's alpha averages 0.59: a little less sheen for fur
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // Locomotion = 1D tree on Gait (0 idle, 1 walk, 2 run, 3 running crawl), state speed MoveRate (ZombieAnimator matches it to the
        // real speed); Attack / NeckBite (speed AttackSpeed) and Scream return to it; Dead -> Death or Dying (DeathIndex) from any state.
        static AnimatorController BuildZombieController()
        {
            var clips = new Dictionary<string, AnimationClip>();
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(ZombieModel))
                if (a is AnimationClip c && !c.name.StartsWith("__preview")) clips[c.name] = c;
            if (!clips.ContainsKey("Idle") || !clips.ContainsKey("Walk")) return null;
            AnimationClip C(string n) => clips.TryGetValue(n, out var c) ? c : clips["Idle"];

            EnsureFolder("Assets", "Animations");
            AssetDatabase.DeleteAsset(ZombieController);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ZombieController);
            ctrl.AddParameter("Gait", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("MoveRate", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("AttackIndex", AnimatorControllerParameterType.Int);
            ctrl.AddParameter("AttackSpeed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Scream", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Dead", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("DeathIndex", AnimatorControllerParameterType.Int);
            var ps = ctrl.parameters;
            foreach (var p in ps) if (p.name == "MoveRate" || p.name == "AttackSpeed") p.defaultFloat = 1f;
            ctrl.parameters = ps;

            var sm = ctrl.layers[0].stateMachine;
            var loco = ctrl.CreateBlendTreeInController("Locomotion", out var tree);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Gait";
            tree.useAutomaticThresholds = false;
            tree.AddChild(C("Idle"), 0f);
            tree.AddChild(C("Walk"), 1f);
            tree.AddChild(C("Run"), 2f);
            tree.AddChild(C("CrawlRun"), 3f);
            loco.speedParameterActive = true; loco.speedParameter = "MoveRate";
            sm.defaultState = loco;

            AnimatorState Action(string name, string clip, float exit, string speedParam, float speed, int index)
            {
                var s = sm.AddState(name);
                s.motion = C(clip);
                if (speedParam != null) { s.speedParameterActive = true; s.speedParameter = speedParam; } else s.speed = speed;
                var t = loco.AddTransition(s);
                t.hasExitTime = false; t.hasFixedDuration = true; t.duration = 0.1f;
                t.AddCondition(AnimatorConditionMode.If, 0f, name == "Scream" ? "Scream" : "Attack");
                if (index >= 0) t.AddCondition(AnimatorConditionMode.Equals, index, "AttackIndex");
                var back = s.AddTransition(loco);
                back.hasExitTime = true; back.exitTime = exit; back.hasFixedDuration = true; back.duration = 0.25f;
                return s;
            }
            Action("Attack", "Attack", 0.62f, "AttackSpeed", 1f, 0);   // claw lands at frame 33 of 80
            Action("NeckBite", "NeckBite", 0.55f, "AttackSpeed", 1f, 1);   // bite at frame 42 of 125
            Action("Scream", "Scream", 0.92f, null, 1.3f, -1);

            for (int i = 0; i < 2; i++)
            {
                var death = sm.AddState(i == 0 ? "Death" : "Dying");
                death.motion = C(i == 0 ? "Death" : "Dying");
                var t = sm.AddAnyStateTransition(death);
                t.canTransitionToSelf = false; t.hasExitTime = false; t.hasFixedDuration = true; t.duration = 0.15f;
                t.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
                t.AddCondition(AnimatorConditionMode.Equals, i, "DeathIndex");
            }
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            return ctrl;
        }

        // NavMesh agent, solid capsule and the server-authority network components (shared by both zombie bodies).
        static void AddZombieBody(GameObject instance, Animator animator)
        {
            var agent = instance.AddComponent<NavMeshAgent>();
            agent.radius = 0.35f;
            agent.height = 1.75f;
            agent.acceleration = 10f;
            agent.angularSpeed = 0f;
            agent.updateRotation = false;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

            // solid body: blocks the player and takes the bullets
            var col = instance.AddComponent<CapsuleCollider>();
            col.height = 1.75f; col.radius = 0.32f; col.center = new Vector3(0f, 0.875f, 0f);

            instance.AddComponent<NetworkObject>();
            var nt = instance.AddComponent<NetworkTransform>();
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Server;
            nt.SyncPositionX = nt.SyncPositionY = nt.SyncPositionZ = true;
            nt.SyncRotAngleX = false; nt.SyncRotAngleY = true; nt.SyncRotAngleZ = false;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            nt.Interpolate = true;
            var na = instance.AddComponent<NetworkAnimator>();
            na.Animator = animator;
            na.AuthorityMode = NetworkAnimator.AuthorityModes.Server;
        }

        // ------------------------------------------------------------------ fallback: tinted cat bodies
        static GameObject BuildZombie(string body, GameObject source, RuntimeAnimatorController controller)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "Zombie_" + body;

            // no weapon in the paws
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var n = r.name.ToLowerInvariant();
                if (n.Contains("rifl") || n.Contains("magazine")) Object.DestroyImmediate(r.gameObject);
            }
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = Tinted(mats[i]);
                r.sharedMaterials = mats;
            }

            var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;   // many zombies in a browser: skip bones off screen

            instance.AddComponent<Health>();
            instance.AddComponent<PlayerAnimator>();
            AddZombieBody(instance, animator);
            instance.AddComponent<Zombie>();

            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, $"{PrefabDir}/{instance.name}.prefab");
            Object.DestroyImmediate(instance);
            return prefab;
        }

        // A green, slightly darker copy of a cat material (kept in sync with the source on every build).
        static Material Tinted(Material source)
        {
            if (source == null) return null;
            var path = $"{MaterialDir}/{source.name}_Zombie.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(source);
                AssetDatabase.CreateAsset(mat, path);
            }
            else mat.CopyPropertiesFromMaterial(source);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", source.GetColor("_BaseColor") * Tint);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}")) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
