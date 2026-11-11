using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Import rules for the loot models from Blender (Assets/Art/Loot, 2026-10-28): Medkit (the user's), AmmoBox and ArmorBox
    /// (Tools/BlenderAnim/make_loot_crates.py). Blender's axes are baked into the meshes and the FBX materials are replaced by URP
    /// Lit materials made from loot_materials.json (colour in sRGB, roughness, metallic, plank texture) in Assets/Art/Loot/Materials.
    /// </summary>
    public class LootAssetPostprocessor : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(LootAssets.Root + "/") || !(assetImporter is ModelImporter mi)) return;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.bakeAxisConversion = true;
            mi.importCameras = false; mi.importLights = false;
            mi.importAnimation = false; mi.animationType = ModelImporterAnimationType.None;
        }

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(LootAssets.Root + "/") || !(assetImporter is TextureImporter ti)) return;
            ti.maxTextureSize = 512;
            ti.crunchedCompression = true; ti.compressionQuality = 60;
        }

        Material OnAssignMaterialModel(Material material, Renderer renderer)
        {
            if (!assetPath.StartsWith(LootAssets.Root + "/")) return null;
            return AssetDatabase.LoadAssetAtPath<Material>(LootAssets.MatDir + material.name + ".mat");
        }
    }

    public static class LootAssets
    {
        public const string Root = "Assets/Art/Loot";
        public const string MatDir = Root + "/Materials/";

        [System.Serializable] class MatDef { public string name, texture; public float[] color; public float roughness, metallic; }
        [System.Serializable] class MatList { public MatDef[] materials; }

        /// <summary>Build the materials, then re-import the models so they pick them up by name. False when the folder is missing.</summary>
        public static bool Prepare()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/loot_materials.json");
            if (json == null) return false;
            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");
            foreach (var d in JsonUtility.FromJson<MatList>(json.text).materials)
            {
                var path = MatDir + d.name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
                mat.SetColor("_BaseColor", d.color != null && d.color.Length >= 3 ? new Color(d.color[0], d.color[1], d.color[2]) : Color.white);
                mat.SetTexture("_BaseMap", string.IsNullOrEmpty(d.texture) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/{d.texture}.png"));
                mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - d.roughness) * 0.6f);
                mat.SetFloat("_Metallic", d.metallic);
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            foreach (var path in Directory.GetFiles(Root, "*.fbx"))
                AssetDatabase.ImportAsset(path.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
            return true;
        }

        public static GameObject Model(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/{name}.fbx");
    }
}
