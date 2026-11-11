using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Menu: CatBattleground > Setup Characters
// Configures the Cat_*.fbx models exported from Blender (Humanoid rig, 25 animation clips with loop flags),
// creates URP materials from Art/Textures and builds one prefab per character in Assets/Prefabs/Characters.
public static class CharacterImportSetup
{
    const string ModelDir = "Assets/Art/Characters";
    const string TextureDir = "Assets/Art/Textures";
    const string MaterialDir = "Assets/Art/Materials";
    const string PrefabDir = "Assets/Prefabs/Characters";
    const float TargetHeight = 1.75f;

    // Bind-pose (T-pose) body heights in metres, measured in Blender (export_mixamo.py prints them). The 2026-10-15 models
    // (NewAnimationCharacter) are all normalised to 1 m; each model is scaled to TargetHeight.
    static readonly Dictionary<string, float> ModelHeights = new()
    {
        { "Cat_Army", 1.0f }, { "Cat_Orange", 1.0f }, { "Cat_Black", 1.0f }, { "Cat_White", 1.0f }, { "Cat_Gold", 1.0f },
        { "Cat_Knight", 1.0f }, { "Cat_Ninja", 1.0f }, { "Cat_Baby", 1.0f }, { "Cat_Soldier", 1.0f }, { "Cat_Nobi", 0.974f },
    };

    static readonly string[] Suffixes = { "ARMY", "ORANGE", "BLACK", "WHITE", "GOLD", "KNIGHT", "NINJA", "BABY", "SOLDIER", "NOBI" };

    // Every clip authored in Blender. Names match the Blender action names without the character suffix.
    static readonly string[] AllClips =
    {
        "Idle_Rifle", "Crouch_Idle",
        "Walk_Fwd", "Walk_Back", "Walk_Left", "Walk_Right",
        "Walk_FwdLeft", "Walk_FwdRight", "Walk_BackLeft", "Walk_BackRight",
        "Run_Fwd", "Run_Back", "Run_Left", "Run_Right",
        "Run_FwdLeft", "Run_FwdRight", "Run_BackLeft", "Run_BackRight",
        "Crouch_Walk", "Crouch_Walk_Back", "Crouch_Walk_Left", "Crouch_Walk_Right",
        "Crouch_Walk_FwdLeft", "Crouch_Walk_FwdRight", "Crouch_Walk_BackLeft", "Crouch_Walk_BackRight",
        "Jump", "Fall", "Plane_Exit", "Skydive", "Parachute", "Parachute_Land", "Land", "Fire", "Reload", "Hit", "Melee", "Throw_Grenade",
        "Death_Front", "Death_Back", "Knocked_Down", "Knocked_Idle", "Get_Up", "Interact",
        "Lobby_Idle",   // hand-made lobby pose loop (only some cats; AnimatorBuilder falls back to Idle_Rifle)
        "Victory",      // winner celebration, 90 frames, holds the last pose (make_victory.py, 2026-11-07)
    };

    static readonly HashSet<string> LoopClips = new()
    {
        "Idle_Rifle", "Crouch_Idle",
        "Walk_Fwd", "Walk_Back", "Walk_Left", "Walk_Right",
        "Walk_FwdLeft", "Walk_FwdRight", "Walk_BackLeft", "Walk_BackRight",
        "Run_Fwd", "Run_Back", "Run_Left", "Run_Right",
        "Run_FwdLeft", "Run_FwdRight", "Run_BackLeft", "Run_BackRight",
        "Crouch_Walk", "Crouch_Walk_Back", "Crouch_Walk_Left", "Crouch_Walk_Right",
        "Crouch_Walk_FwdLeft", "Crouch_Walk_FwdRight", "Crouch_Walk_BackLeft", "Crouch_Walk_BackRight",
        "Fall", "Skydive", "Parachute", "Knocked_Idle", "Lobby_Idle",
    };

    // Frames (30 fps) cut from the start of a clip, measured on the Mixamo packs (same for every cat).
    static readonly Dictionary<string, int> TrimStartFrames = new() { { "Jump", 11 }, { "Land", 9 } };

    [MenuItem("CatBattleground/Setup Characters")]
    public static void Run()
    {
        EnsureFolder(MaterialDir);
        EnsureFolder("Assets/Prefabs");
        EnsureFolder(PrefabDir);

        ConfigureTextures();
        var rifleMat = CreateRifleMaterial();
        // own weapon models (2026-10-19, CHARACTER/weapons_work.blend -> install_weapons.py): Knight's shotgun, Ninja's sniper
        var weaponMats = new Dictionary<string, Material>
        {
            { "Cat_Knight", CreateWeaponMaterial("Shotgun", 0.31f, 0.63f) },
            { "Cat_Ninja", CreateWeaponMaterial("Sniper", 0f, 0.5f) },
            // 2026-11-07 (user): Cash Cat carries a copy of Ninja's sniper (install_sniper_white.py), same textures.
            // The painted marksman rifle (make_marksman_rifle.py, Weapon_Marksman) is kept in CHARACTER/weapons but unused.
            // Legendary look: Weapon_SniperGold_* = Ninja's textures with the camo turned to gold (make_sniper_gold.py).
            { "Cat_White", CreateWeaponMaterial("SniperGold", 0f, 0.5f) },
        };

        foreach (var guid in AssetDatabase.FindAssets("t:Model Cat_", new[] { ModelDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var name = Path.GetFileNameWithoutExtension(path);
            ConfigureModel(path, name);

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/{name}_BaseColor.jpg");
            var bodyMat = GetOrCreateMaterial(name, texture, Color.white);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var gunMat = weaponMats.TryGetValue(name, out var own) && own != null ? own : rifleMat;
            foreach (var r in instance.GetComponentsInChildren<Renderer>())
                { var n = r.name.ToLower(); r.sharedMaterial = n.Contains("rifl") || n.Contains("magazine") ? gunMat : bodyMat; }   // Magazine_<SUF> = split off the rifle
            PrefabUtility.SaveAsPrefabAsset(instance, $"{PrefabDir}/{name}.prefab");
            Object.DestroyImmediate(instance);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("CatBattleground: character setup complete.");
    }

    // "Armature|Armature|Run_Fwd_ARMY" -> "Run_Fwd"
    static string CleanClipName(string takeName)
    {
        var n = takeName.Split('|').Last();
        foreach (var s in Suffixes)
            if (n.EndsWith("_" + s)) { n = n.Substring(0, n.Length - s.Length - 1); break; }
        return n;
    }

    // GENERIC rig: the bones play exactly what was authored in Blender. The humanoid conversion (muscle space) distorted the
    // Mixamo clips on these chibi proportions. Every cat's FBX has the same root ("Armature") and the same mixamorig bone names,
    // so the shared animator, the per-cat override controllers and the transform-based upper-body mask all line up.
    // Loops are made in place by export_mixamo.py, and Animator.applyRootMotion is off.
    static void ConfigureModel(string path, string name)
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        if (ModelHeights.TryGetValue(name, out var height)) importer.globalScale = TargetHeight / height;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.humanDescription = new HumanDescription();   // drop the old humanoid mapping
        importer.motionNodeName = "";                          // no root motion node
        // the FBX are baked at every frame (11-13 MB each); let Unity drop the keys it does not need (WebGL size, 2026-10-28)
        importer.animationCompression = ModelImporterAnimationCompression.Optimal;
        // first WebGL build: the 9 cats' clips were still ~21 MB of the data file at 0.5 -> 1.0 (still invisible on chibi cats)
        // 2026-11-07: rotation 1.0 -> 0.25 deg. The gun hangs at the end of the spine/arm/hand/finger chain, so the per-bone error
        // added up to several cm at the paws (gun off the paws in Unity while the FBX itself matches Blender exactly).
        importer.animationRotationError = 0.25f;
        importer.animationPositionError = 0.5f;
        importer.animationScaleError = 1.0f;
        importer.meshCompression = ModelImporterMeshCompression.Low;

        var clips = new List<ModelImporterClipAnimation>();
        foreach (var c in importer.defaultClipAnimations)
        {
            var clean = CleanClipName(c.takeName);
            if (!AllClips.Contains(clean)) continue;   // skip default "Take 001" etc.
            c.name = clean;
            c.loopTime = LoopClips.Contains(clean);
            c.loopPose = false;                        // loops are closed in Blender (last frame == first frame)
            // Mixamo's jump clips include ground time the physics does not have: "jump up" crouches ~0.4 s before the feet leave
            // the ground (the game jumps at once), "jump down" still hangs ~0.27 s before the feet touch. Start them at take-off / touch-down.
            if (TrimStartFrames.TryGetValue(clean, out var trim)) c.firstFrame = Mathf.Min(c.firstFrame + trim, c.lastFrame - 2);
            clips.Add(c);
        }
        importer.clipAnimations = clips.ToArray();
        importer.SaveAndReimport();
    }

    // Web demo: keep downloads small. Cat skins 2048 -> 1024 with crunch, rifle maps 512; normal map type for the rifle.
    static void ConfigureTextures()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TextureDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
            var file = Path.GetFileNameWithoutExtension(path);
            bool changed = false;
            int maxSize = file.StartsWith("Rifle_") ? 512 : 1024;
            if (ti.maxTextureSize != maxSize) { ti.maxTextureSize = maxSize; changed = true; }
            if (!ti.crunchedCompression) { ti.crunchedCompression = true; ti.compressionQuality = 50; changed = true; }
            if (file.EndsWith("_Normal") && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; }
            if (file.EndsWith("_MetallicSmoothness") && ti.sRGBTexture) { ti.sRGBTexture = false; changed = true; }
            if (changed) ti.SaveAndReimport();
        }
    }

    static Material CreateRifleMaterial()
    {
        var mat = GetOrCreateMaterial("Rifle", AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/Rifle_BaseColor.png"), Color.white);
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/Rifle_Normal.png");
        var emissive = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/Rifle_Emissive.png");
        if (normal != null) { mat.SetTexture("_BumpMap", normal); mat.EnableKeyword("_NORMALMAP"); }
        if (emissive != null)
        {
            mat.SetTexture("_EmissionMap", emissive);
            mat.SetColor("_EmissionColor", Color.white);
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
        }
        mat.SetFloat("_Metallic", 0.6f);
        mat.SetFloat("_Smoothness", 0.5f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Weapon_<kind>_BaseColor / _Normal / _MetallicSmoothness (R metal, A smoothness) written by install_weapons.py.
    static Material CreateWeaponMaterial(string kind, float metallic, float smoothness)
    {
        var baseMap = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/Weapon_{kind}_BaseColor.png");
        if (baseMap == null) return null;
        var mat = GetOrCreateMaterial("Weapon_" + kind, baseMap, Color.white);
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/Weapon_{kind}_Normal.png");
        var metal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/Weapon_{kind}_MetallicSmoothness.png");
        if (normal != null) { mat.SetTexture("_BumpMap", normal); mat.EnableKeyword("_NORMALMAP"); }
        if (metal != null)
        {
            mat.SetTexture("_MetallicGlossMap", metal); mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.SetFloat("_Smoothness", 1f);               // scales the map's alpha
        }
        else
        {
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material GetOrCreateMaterial(string name, Texture2D baseMap, Color tint)
    {
        var path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", tint);
        if (baseMap != null) mat.SetTexture("_BaseMap", baseMap);
        mat.SetFloat("_Smoothness", 0.1f);
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
