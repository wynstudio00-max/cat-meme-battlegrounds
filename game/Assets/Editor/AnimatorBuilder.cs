using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Builds Assets/Animations/CatPlayer.controller from the clips in Cat_Army.fbx, plus one
    /// AnimatorOverrideController per character so each cat uses its own (per-proportion) clips.
    ///
    /// Base layer : Locomotion 2D blend tree, Crouch blend tree, Jump/Fall/Land, Knocked/Get_Up, Death.
    /// UpperBody  : Fire, Reload, Hit, Melee, Throw, Interact (avatar mask: spine, head, arms).
    /// </summary>
    public static class AnimatorBuilder
    {
        public const string ControllerPath = "Assets/Animations/CatPlayer.controller";
        const string MaskPath = "Assets/Animations/UpperBody.mask";
        const string SourceCharacter = "Cat_Army";

        // Blend tree speeds. Ground speeds in PlayerController: walk 1.4, sprint 4.0, crouch 0.9 m/s.
        // Walk/Run/Crouch clips are the Mixamo "Rifle 8-Way Locomotion Pack" made for each cat's own rig (NewAnimationCharacter).
        // Root speed in Unity metres per second at timeScale 1 (all cats are scaled to 1.75 m): walk 1.03-1.15, run 2.55-2.87 (Mixamo
        // "run", not "sprint" which is ~4 m/s), crouch walk 1.1-1.2. TimeScales = game speed / clip speed: walk 1.2, sprint 3.1,
        // crouch 0.9 (PlayerController).
        // 2026-10-16: Run_* are now Mixamo "sprint" (3.8-4.3 m/s) and the game runs faster: walk 1.4, sprint 4.0.
        const float WalkTimeScale = 1.28f;
        const float RunTimeScale = 1.0f;
        const float CrouchTimeScale = 0.8f;
        const float RunBlendRadius = 2.86f;     // sprint speed / walk speed
        const float CrouchBlendPoint = 0.64f;   // crouch speed / walk speed

        static string FbxPath(string character) => $"Assets/Art/Characters/{character}.fbx";

        static Dictionary<string, AnimationClip> LoadClips(string character) =>
            AssetDatabase.LoadAllAssetsAtPath(FbxPath(character)).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .GroupBy(c => c.name).ToDictionary(g => g.Key, g => g.First());

        public static AnimatorController Build()
        {
            var clips = LoadClips(SourceCharacter);
            AnimationClip C(string name)
            {
                if (clips.TryGetValue(name, out var clip)) return clip;
                Debug.LogWarning($"AnimatorBuilder: clip '{name}' not found in {SourceCharacter}.fbx (run CatBattleground > Setup Characters).");
                return null;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Animations")) AssetDatabase.CreateFolder("Assets", "Animations");
            AssetDatabase.DeleteAsset(ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

            foreach (var p in new[] { "MoveX", "MoveY", "MoveSpeed", "ReloadSpeed" }) controller.AddParameter(p, AnimatorControllerParameterType.Float);
            foreach (var p in new[] { "Grounded", "Crouching", "Skydive", "Parachute", "Dead", "DeathBack", "Knocked", "Lobby", "Victory" }) controller.AddParameter(p, AnimatorControllerParameterType.Bool);
            foreach (var p in new[] { "Jump", "Fire", "Reload", "Hit", "Melee", "Throw", "Interact" }) controller.AddParameter(p, AnimatorControllerParameterType.Trigger);
            var ps = controller.parameters;
            for (int i = 0; i < ps.Length; i++) { if (ps[i].name == "Grounded") ps[i].defaultBool = true; if (ps[i].name == "ReloadSpeed") ps[i].defaultFloat = 1f; }
            controller.parameters = ps;

            BuildBaseLayer(controller, C, clips.ContainsKey);
            BuildUpperBodyLayer(controller, C);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        // ------------------------------------------------------------------ base layer
        static void BuildBaseLayer(AnimatorController controller, System.Func<string, AnimationClip> C, System.Func<string, bool> clipsHave)
        {
            var sm = controller.layers[0].stateMachine;

            // Locomotion: 2D freeform. X = right, Y = forward, in walk-speed units.
            var loco = controller.CreateBlendTreeInController("Locomotion", out var locoTree);
            locoTree.blendType = BlendTreeType.FreeformCartesian2D;
            locoTree.blendParameter = "MoveX";
            locoTree.blendParameterY = "MoveY";
            locoTree.useAutomaticThresholds = false;
            locoTree.AddChild(C("Idle_Rifle"), new Vector2(0f, 0f));
            AddDirectional8(locoTree, 1f, WalkTimeScale, C, "Walk_");
            AddDirectional8(locoTree, RunBlendRadius, RunTimeScale, C, "Run_");
            // "Left" in the Blender clips is the cat's left; in Unity local space that is -X.

            var crouch = controller.CreateBlendTreeInController("Crouch", out var crouchTree);
            crouchTree.blendType = BlendTreeType.FreeformCartesian2D;
            crouchTree.blendParameter = "MoveX";
            crouchTree.blendParameterY = "MoveY";
            crouchTree.useAutomaticThresholds = false;
            crouchTree.AddChild(C("Crouch_Idle"), new Vector2(0f, 0f));
            AddDirectional8(crouchTree, CrouchBlendPoint, CrouchTimeScale, C, "Crouch_Walk", crouch: true);

            // Mixamo "jump up" is 0.53 s (the physical jump is ~0.57 s), "jump loop" is the airborne pose, "jump down" (0.67 s) is played faster.
            // Skydive / Parachute are procedural poses (generated per cat from its own idle).
            var jump = AddState(sm, "Jump", C("Jump"), 1f);
            var fall = AddState(sm, "Fall", C("Fall"), 1f);
            var skydive = AddState(sm, "Skydive", C("Skydive"), 1f);
            // Leaping off the plane ramp (make_plane_exit.py, 1 s): crouch, push off, head-first dive, ends in the Skydive pose.
            var planeExit = AddState(sm, "Plane_Exit", C("Plane_Exit"), 1f);
            var parachute = AddState(sm, "Parachute", C("Parachute"), 1f);
            // Hand-made in Blender (parachute_landing_work.blend): starts floating ~0.75 m up, touches down at frame 10 of 30.
            var chuteLand = AddState(sm, "Parachute_Land", C("Parachute_Land"), 1f);
            var land = AddState(sm, "Land", C("Land"), 1.3f);
            var knockDown = AddState(sm, "Knocked_Down", C("Knocked_Down"), 1f);
            var knockIdle = AddState(sm, "Knocked_Idle", C("Knocked_Idle"), 1f);
            var getUp = AddState(sm, "Get_Up", C("Get_Up"), 1f);
            var deathFront = AddState(sm, "Death_Front", C("Death_Front"), 1f);
            var deathBack = AddState(sm, "Death_Back", C("Death_Back"), 1f);

            // Lobby character preview (LobbyCharacterPreview sets "Lobby"): the user's hand-made Lobby_Idle loop, or Idle_Rifle for a
            // cat that has none yet (BuildOverrides maps those to their own Idle_Rifle).
            var lobbyClip = clipsHave("Lobby_Idle") ? C("Lobby_Idle") : C("Idle_Rifle");
            var lobby = AddState(sm, "Lobby_Idle", lobbyClip, 1f);

            // Winner celebration (VictorySequence sets "Victory" on the winner): rifle up, fist pump, holds the last pose.
            var victory = AddState(sm, "Victory", clipsHave("Victory") ? C("Victory") : C("Idle_Rifle"), 1f);

            sm.defaultState = loco;
            foreach (var from in new[] { loco, crouch, land })
            { var tv = from.AddTransition(victory); Setup(tv, false, 0f, 0.15f); tv.AddCondition(AnimatorConditionMode.If, 0, "Victory"); }
            { var tv = victory.AddTransition(loco); Setup(tv, false, 0f, 0.25f); tv.AddCondition(AnimatorConditionMode.IfNot, 0, "Victory"); }
            { var tl = loco.AddTransition(lobby); Setup(tl, false, 0f, 0.2f); tl.AddCondition(AnimatorConditionMode.If, 0, "Lobby");
              tl = lobby.AddTransition(loco); Setup(tl, false, 0f, 0.2f); tl.AddCondition(AnimatorConditionMode.IfNot, 0, "Lobby"); }

            // Locomotion <-> Crouch
            var t = loco.AddTransition(crouch); Setup(t, false, 0f, 0.15f); t.AddCondition(AnimatorConditionMode.If, 0, "Crouching");
            t = crouch.AddTransition(loco); Setup(t, false, 0f, 0.15f); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Crouching");

            // Jump
            foreach (var from in new[] { loco, crouch })
            {
                t = from.AddTransition(jump); Setup(t, false, 0f, 0.05f); t.AddCondition(AnimatorConditionMode.If, 0, "Jump");
            }
            // The Jump clip is trimmed to the take-off (0.17 s of a ~0.69 s jump): at its end the cat is normally still in the air
            // and goes on into Fall, which lands with Land on touch-down. Jump -> Land only for very short hops that are already
            // down again (the condition must stay: without it Land played in mid-air and then again on touch-down = legs twice).
            t = jump.AddTransition(land); Setup(t, true, 0.9f, 0.06f); t.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            t = jump.AddTransition(fall); Setup(t, true, 0.9f, 0.08f); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");

            // Jumping out of the drop plane: the leap (Plane_Exit), then the belly-down skydive loop
            foreach (var from in new[] { loco, crouch })
            {
                t = from.AddTransition(planeExit); Setup(t, false, 0f, 0.05f); t.AddCondition(AnimatorConditionMode.If, 0, "Skydive");
            }
            t = planeExit.AddTransition(parachute); Setup(t, false, 0f, 0.25f); t.AddCondition(AnimatorConditionMode.If, 0, "Parachute");
            t = planeExit.AddTransition(land); Setup(t, false, 0f, 0.08f); t.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            t = planeExit.AddTransition(skydive); Setup(t, true, 0.95f, 0.08f);   // last frame == Skydive frame 1 (the user's flying pose)

            // Falling off ledges / after the jump clip ends while still airborne
            foreach (var from in new[] { loco, crouch })
            {
                t = from.AddTransition(fall); Setup(t, false, 0f, 0.1f); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");
            }
            // Drop-in poses: Skydive (free fall) -> Parachute (hanging under the canopy) -> Land. Bools "Skydive" / "Parachute" come from PlayerDrop.
            t = fall.AddTransition(skydive); Setup(t, false, 0f, 0.15f); t.AddCondition(AnimatorConditionMode.If, 0, "Skydive");
            t = skydive.AddTransition(parachute); Setup(t, false, 0f, 0.25f); t.AddCondition(AnimatorConditionMode.If, 0, "Parachute");
            t = skydive.AddTransition(land); Setup(t, false, 0f, 0.08f); t.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            t = skydive.AddTransition(fall); Setup(t, false, 0f, 0.15f); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Skydive");
            // Hanging under the canopy: Fall <-> Parachute follows the "Parachute" bool; touching down leaves for Land from either.
            t = fall.AddTransition(parachute); Setup(t, false, 0f, 0.25f); t.AddCondition(AnimatorConditionMode.If, 0, "Parachute");
            t = parachute.AddTransition(fall); Setup(t, false, 0f, 0.15f); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Parachute");
            t = parachute.AddTransition(chuteLand); Setup(t, false, 0f, 0.1f); t.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            // after the touch-down (frame 10 of 30) moving cancels the rest of the landing; otherwise it plays to the idle pose
            t = chuteLand.AddTransition(loco); Setup(t, true, 0.45f, 0.15f); t.AddCondition(AnimatorConditionMode.Greater, 0.15f, "MoveSpeed");
            t = chuteLand.AddTransition(loco); Setup(t, true, 0.95f, 0.15f);
            t = fall.AddTransition(land); Setup(t, false, 0f, 0.08f); t.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            // Landing while moving: back to walking/running right away, otherwise the in-place landing clip slides over the ground.
            t = land.AddTransition(loco); Setup(t, false, 0f, 0.12f); t.AddCondition(AnimatorConditionMode.Greater, 0.15f, "MoveSpeed");
            t = land.AddTransition(loco); Setup(t, true, 0.9f, 0.1f);

            // Knocked down (duo/squad revive)
            t = sm.AddAnyStateTransition(knockDown); Setup(t, false, 0f, 0.1f); t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0, "Knocked"); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Dead");
            t = knockDown.AddTransition(knockIdle); Setup(t, true, 0.95f, 0.05f);
            t = knockIdle.AddTransition(getUp); Setup(t, false, 0f, 0.1f); t.AddCondition(AnimatorConditionMode.IfNot, 0, "Knocked");
            t = getUp.AddTransition(loco); Setup(t, true, 0.95f, 0.1f);

            // Death (no way out except Animator.Rebind on revive)
            t = sm.AddAnyStateTransition(deathFront); Setup(t, false, 0f, 0.1f); t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0, "Dead"); t.AddCondition(AnimatorConditionMode.IfNot, 0, "DeathBack");
            t = sm.AddAnyStateTransition(deathBack); Setup(t, false, 0f, 0.1f); t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0, "Dead"); t.AddCondition(AnimatorConditionMode.If, 0, "DeathBack");
        }

        // Eight clips on a ring of the given radius (walk-speed units). Clip names: <prefix>Fwd/Back/Left/Right/FwdLeft/FwdRight/BackLeft/BackRight;
        // for the crouch set the forward clip is just "Crouch_Walk" and the others are "Crouch_Walk_<direction>".
        // "Left" in the clips is the cat's left; in Unity local space that is -X.
        static void AddDirectional8(BlendTree tree, float radius, float timeScale, System.Func<string, AnimationClip> C, string prefix, bool crouch = false)
        {
            string Name(string dir) => crouch ? (dir == "Fwd" ? "Crouch_Walk" : prefix + "_" + dir) : prefix + dir;
            const float d = 0.70710678f;
            var dirs = new (string name, Vector2 pos)[]
            {
                ("Fwd", new Vector2(0f, 1f)), ("Back", new Vector2(0f, -1f)), ("Left", new Vector2(-1f, 0f)), ("Right", new Vector2(1f, 0f)),
                ("FwdLeft", new Vector2(-d, d)), ("FwdRight", new Vector2(d, d)), ("BackLeft", new Vector2(-d, -d)), ("BackRight", new Vector2(d, -d)),
            };
            int start = tree.children.Length;
            for (int i = 0; i < dirs.Length; i++)
            {
                tree.AddChild(C(Name(dirs[i].name)), dirs[i].pos * radius);
                SetTimeScale(tree, start + i, timeScale);
            }
        }

        static void SetTimeScale(BlendTree tree, int index, float timeScale)
        {
            var children = tree.children;
            children[index].timeScale = timeScale;
            tree.children = children;
        }

        // ------------------------------------------------------------------ upper-body layer
        static void BuildUpperBodyLayer(AnimatorController controller, System.Func<string, AnimationClip> C)
        {
            var mask = CreateUpperBodyMask();

            controller.AddLayer("UpperBody");
            var layers = controller.layers;
            var layer = layers[layers.Length - 1];
            layer.avatarMask = mask;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.defaultWeight = 1f;
            layers[layers.Length - 1] = layer;
            controller.layers = layers;

            var sm = controller.layers[1].stateMachine;
            var empty = sm.AddState("Empty");
            sm.defaultState = empty;

            // Fire clip is 0.33 s but the rifle shoots 9 rounds/s.
            AddUpperState(sm, empty, "Fire", "Fire", C("Fire"), 2.5f, 0.9f);
            // Reload clip (2026-10-16, body + Magazine bone) is 1.8 s = the assault rifle; "ReloadSpeed" (PlayerAnimator.TriggerReload)
            // scales it to each weapon's reloadTime (1.3-3.4 s). Leaves at 0.92: the magazine is seated at 40/54 = 0.74.
            var reload = AddUpperState(sm, empty, "Reload", "Reload", C("Reload"), 1f, 0.92f);
            reload.speedParameter = "ReloadSpeed"; reload.speedParameterActive = true;
            AddUpperState(sm, empty, "Hit", "Hit", C("Hit"), 1.2f, 0.9f);
            AddUpperState(sm, empty, "Melee", "Melee", C("Melee"), 1.2f, 0.92f);
            // make_throw.py (2026-10-27): left-arm overhand throw, 1 s, the bomb leaves the paw at frame 14 (BombThrower.ReleaseDelay)
            AddUpperState(sm, empty, "Throw_Grenade", "Throw", C("Throw_Grenade"), 1f, 0.95f);
            AddUpperState(sm, empty, "Interact", "Interact", C("Interact"), 1f, 0.95f);
        }

        static AnimatorState AddUpperState(AnimatorStateMachine sm, AnimatorState empty, string stateName, string trigger, AnimationClip clip, float speed, float exitTime)
        {
            var state = AddState(sm, stateName, clip, speed);
            var t = sm.AddAnyStateTransition(state);
            Setup(t, false, 0f, 0.04f);
            t.canTransitionToSelf = true;   // re-trigger while still playing (automatic fire)
            t.AddCondition(AnimatorConditionMode.If, 0, trigger);
            var back = state.AddTransition(empty);
            Setup(back, true, exitTime, 0.08f);
            return state;
        }

        static AvatarMask CreateUpperBodyMask()
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            if (mask == null)
            {
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, MaskPath);
            }
            // Generic rig: a transform mask. Active = mixamorig:Spine and everything under it (torso, head, arms, hands, rifle);
            // inactive = root, Hips and legs, so fire / reload play on top of walking. All cats share these paths
            // (root "Armature", same bone names), so the Army model defines the mask for everyone.
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath(SourceCharacter));
            var transforms = model != null ? model.GetComponentsInChildren<Transform>(true) : new Transform[0];
            mask.transformCount = transforms.Length;
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                mask.SetTransformPath(i, AnimationUtility.CalculateTransformPath(t, model.transform));
                bool upper = false;
                for (var p = t; p != null; p = p.parent) if (p.name == "mixamorig:Spine") { upper = true; break; }
                mask.SetTransformActive(i, upper);
            }
            foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
                if (part != AvatarMaskBodyPart.LastBodyPart) mask.SetHumanoidBodyPartActive(part, false);
            EditorUtility.SetDirty(mask);
            return mask;
        }

        // ------------------------------------------------------------------ per-character overrides
        /// <summary>One override controller per Cat_*.fbx so each cat plays the clips authored for its own proportions.</summary>
        public static Dictionary<string, RuntimeAnimatorController> BuildOverrides(AnimatorController baseController)
        {
            var result = new Dictionary<string, RuntimeAnimatorController>();
            foreach (var guid in AssetDatabase.FindAssets("t:Model Cat_", new[] { "Assets/Art/Characters" }))
            {
                var character = System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid));
                if (character == SourceCharacter) { result[character] = baseController; continue; }

                var clips = LoadClips(character);
                var path = $"Assets/Animations/CatPlayer_{character}.overrideController";
                AssetDatabase.DeleteAsset(path);
                var oc = new AnimatorOverrideController(baseController);
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                oc.GetOverrides(pairs);
                for (int i = 0; i < pairs.Count; i++)
                {
                    var original = pairs[i].Key;
                    if (original == null) continue;
                    if (clips.TryGetValue(original.name, out var replacement))
                        pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(original, replacement);
                    else if (original.name == "Lobby_Idle" && clips.TryGetValue("Idle_Rifle", out var ownIdle))
                        pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(original, ownIdle);   // no lobby pose made for this cat yet
                }
                oc.ApplyOverrides(pairs);
                AssetDatabase.CreateAsset(oc, path);
                result[character] = oc;
            }
            return result;
        }

        // ------------------------------------------------------------------ helpers
        static AnimatorState AddState(AnimatorStateMachine sm, string name, AnimationClip clip, float speed)
        {
            var s = sm.AddState(name);
            s.motion = clip;
            s.speed = speed;
            return s;
        }

        static void Setup(AnimatorStateTransition t, bool hasExit, float exitTime, float duration)
        {
            t.hasExitTime = hasExit;
            t.exitTime = exitTime;
            t.duration = duration;
            t.hasFixedDuration = true;
        }
    }
}
