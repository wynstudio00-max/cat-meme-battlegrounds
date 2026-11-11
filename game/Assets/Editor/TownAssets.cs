using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Import rules for the user's BattlefieldTown pack (Assets/Art/BattlefieldTown, 2026-10-27): plaster houses (1 and 2 floors,
    /// enterable), a ruined house and walls, exported from Blender. Meshes are readable (MeshColliders), Blender's axes are baked
    /// into the meshes, and the FBX materials are replaced by URP Lit materials made from town_materials.json
    /// (Tools/ArenaKit/town_materials.py: colour, roughness, plaster texture) in Assets/Art/BattlefieldTown/Materials.
    /// </summary>
    public class TownAssetPostprocessor : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(TownAssets.ModelDir) || !(assetImporter is ModelImporter mi)) return;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.bakeAxisConversion = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.isReadable = true;   // MeshColliders
            mi.meshCompression = ModelImporterMeshCompression.Medium;   // WebGL size pass (houses were 0.3-0.7 MB each)
        }

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(TownAssets.Root + "/Textures/") || !(assetImporter is TextureImporter ti)) return;
            ti.maxTextureSize = 1024;
            ti.crunchedCompression = true;
            ti.compressionQuality = 55;
        }

        Material OnAssignMaterialModel(Material material, Renderer renderer)
        {
            if (!assetPath.StartsWith(TownAssets.ModelDir)) return null;
            return AssetDatabase.LoadAssetAtPath<Material>(TownAssets.MatDir + material.name + ".mat");
        }
    }

    public static class TownAssets
    {
        public const string Root = "Assets/Art/BattlefieldTown";
        public const string ModelDir = Root + "/Models/";
        public const string MatDir = Root + "/Materials/";

        [System.Serializable] class MatDef { public string name, texture; public float[] color; public float roughness, metallic; }
        [System.Serializable] class MatList { public MatDef[] materials; }

        public static bool Available => AssetDatabase.IsValidFolder(Root + "/Models/Buildings");

        /// <summary>Build the materials, then re-import the models so they pick them up by name.</summary>
        public static void Prepare()
        {
            if (!Available) return;
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/town_materials.json");
            if (json == null) { Debug.LogWarning("TownAssets: town_materials.json missing (run Tools/ArenaKit/town_materials.py)."); return; }
            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");
            foreach (var d in JsonUtility.FromJson<MatList>(json.text).materials)
            {
                var path = MatDir + d.name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
                var c = d.color != null && d.color.Length >= 3 ? new Color(d.color[0], d.color[1], d.color[2]) : Color.white;
                mat.SetColor("_BaseColor", c);
                var tex = string.IsNullOrEmpty(d.texture) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>($"{Root}/Textures/{d.texture}.png");
                mat.SetTexture("_BaseMap", tex);
                mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - d.roughness) * 0.6f);
                mat.SetFloat("_Metallic", d.metallic);
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            foreach (var path in Directory.GetFiles(Root + "/Models", "*.fbx", SearchOption.AllDirectories))
                AssetDatabase.ImportAsset(path.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
        }

        public static GameObject Building(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + "Buildings/" + name + ".fbx");    }
}
