using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Import rules + URP materials for the stylised nature pack in Assets/Art/Forest (trees, bushes, grass, flowers, mushrooms, rocks).
    /// The FBX files reference their materials by name; those names are mapped here to URP Lit materials built from the pack's textures.
    /// Leaf textures come as an alpha mask (white) and a coloured version (_C); extra tinted variants are made from the masks.
    /// </summary>
    public class ForestAssetPostprocessor : AssetPostprocessor
    {
        public const string Root = "Assets/Art/Forest/";
        public const string TexDir = Root + "Textures/";
        public const string ModelDir = Root + "Models/";
        public const string MatDir = Root + "Materials/";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(TexDir) || !(assetImporter is TextureImporter ti)) return;
            var name = Path.GetFileNameWithoutExtension(assetPath);

            if (name.EndsWith("_Normal"))
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.maxTextureSize = 512;
                ti.crunchedCompression = false;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Repeat;
                return;
            }

            bool alpha = ForestAssets.HasAlpha(name);
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaIsTransparency = alpha;
            ti.mipmapEnabled = true;
            ti.mipMapsPreserveCoverage = alpha;
            ti.alphaTestReferenceValue = 0.5f;
            ti.wrapMode = name.StartsWith("Bark") || name.StartsWith("Rocks") || name.StartsWith("PathRocks") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = name.StartsWith("Bark") ? 1024 : name == "Grass" ? 128 : name == "Leaves" ? 1024 : 512;
            ti.crunchedCompression = true;
            ti.compressionQuality = 55;
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(ModelDir) || !(assetImporter is ModelImporter mi)) return;
            var name = Path.GetFileNameWithoutExtension(assetPath);
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importBlendShapes = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.isReadable = name.StartsWith("Rock_");   // convex MeshColliders are made from the rocks
            mi.meshCompression = ModelImporterMeshCompression.Off;
        }

        Material OnAssignMaterialModel(Material material, Renderer renderer)
        {
            if (!assetPath.StartsWith(ModelDir)) return null;
            return AssetDatabase.LoadAssetAtPath<Material>(MatDir + material.name + ".mat");
        }
    }

    public static class ForestAssets
    {
        static readonly string[] AlphaTextures =
        {
            "Leaves_NormalTree", "Leaves_NormalTree_C", "Leaves_TwistedTree", "Leaves_TwistedTree_C", "Leaf_Pine", "Leaf_Pine_C", "Leaves", "Flowers",
        };

        public static bool HasAlpha(string textureName) => System.Array.IndexOf(AlphaTextures, textureName) >= 0;

        struct Def
        {
            public string name, baseTexture, normalTexture;
            public bool alpha;
            public Color tint;
            public float wind;   // > 0: CatBattleground/Foliage shader (sways in the wind), value = sway amount
            public Def(string name, string baseTexture, string normalTexture = null, bool alpha = false, Color? tint = null, float wind = 0f)
            {
                this.name = name; this.baseTexture = baseTexture; this.normalTexture = normalTexture; this.alpha = alpha; this.tint = tint ?? Color.white;
                this.wind = wind;
            }
        }

        static readonly Def[] Defs =
        {
            new Def("Bark_NormalTree", "Bark_NormalTree", "Bark_NormalTree_Normal"),
            new Def("Bark_TwistedTree", "Bark_TwistedTree", "Bark_TwistedTree_Normal"),
            new Def("Bark_DeadTree", "Bark_DeadTree", "Bark_DeadTree_Normal"),
            new Def("Material", "Bark_NormalTree"),                                               // twigs on the bushes

            new Def("Leaves_NormalTree", "Leaves_NormalTree_C", alpha: true, wind: 1.6f),
            new Def("Leaves_NormalTree_Lime", "Leaves_NormalTree", alpha: true, tint: new Color(0.62f, 0.82f, 0.22f), wind: 1.6f),
            new Def("Leaves_NormalTree_Autumn", "Leaves_NormalTree", alpha: true, tint: new Color(0.96f, 0.52f, 0.14f), wind: 1.6f),
            new Def("Leaves_TwistedTree", "Leaves_TwistedTree_C", alpha: true, wind: 1.3f),                  // red
            new Def("Leaves_TwistedTree_Green", "Leaves_TwistedTree", alpha: true, tint: new Color(0.30f, 0.62f, 0.18f), wind: 1.3f),
            new Def("Leaves_Pine", "Leaf_Pine_C", alpha: true, wind: 1.1f),
            new Def("Leaves_Pine_Light", "Leaf_Pine", alpha: true, tint: new Color(0.40f, 0.68f, 0.30f), wind: 1.1f),

            new Def("Leaves", "Leaves", alpha: true, wind: 0.7f),          // bushes, ferns, plants
            new Def("Flowers", "Flowers", alpha: true, wind: 0.9f),
            new Def("Grass", "Grass", wind: 1.0f),
            new Def("Mushrooms", "Mushrooms"),
            new Def("PathRocks", "PathRocks_Diffuse"),
            new Def("Rocks", "Rocks_Diffuse"),
        };

        /// <summary>Refresh imports, build the materials, then re-import the models so they pick the materials up by name.</summary>
        public static void Prepare()
        {
            AssetDatabase.Refresh();
            BuildMaterials();
            foreach (var path in Directory.GetFiles("Assets/Art/Forest/Models", "*.fbx"))
                AssetDatabase.ImportAsset(path.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void BuildMaterials()
        {
            EnsureFolder("Assets/Art/Forest/Materials");
            foreach (var def in Defs)
            {
                var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ForestAssetPostprocessor.TexDir}{def.baseTexture}.png");
                if (baseTex == null) { Debug.LogWarning("ForestAssets: missing texture " + def.baseTexture); continue; }

                var path = ForestAssetPostprocessor.MatDir + def.name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                var foliage = def.wind > 0f ? Shader.Find("CatBattleground/Foliage") : null;
                var shader = foliage != null ? foliage : Shader.Find("Universal Render Pipeline/Lit");
                if (mat == null)
                {
                    mat = new Material(shader);
                    AssetDatabase.CreateAsset(mat, path);
                }
                else if (mat.shader != shader) mat.shader = shader;
                if (foliage != null)
                {
                    mat.SetFloat("_WindAmount", def.wind);
                    mat.SetFloat("_Translucency", def.name == "Grass" ? 0.2f : 0.4f);
                }
                mat.SetTexture("_BaseMap", baseTex);
                mat.SetColor("_BaseColor", def.tint);
                mat.SetFloat("_Smoothness", def.name.StartsWith("Bark") ? 0.08f : 0.02f);
                mat.SetFloat("_Metallic", 0f);

                if (def.normalTexture != null)
                {
                    var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ForestAssetPostprocessor.TexDir}{def.normalTexture}.png");
                    mat.SetTexture("_BumpMap", normal);
                    mat.SetFloat("_BumpScale", 1f);
                    mat.EnableKeyword("_NORMALMAP");
                }

                bool twoSided = def.alpha || def.name == "Grass";
                mat.SetFloat("_AlphaClip", def.alpha ? 1f : 0f);
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_Cull", twoSided ? 0f : 2f);
                if (def.alpha) mat.EnableKeyword("_ALPHATEST_ON"); else mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetOverrideTag("RenderType", def.alpha ? "TransparentCutout" : "Opaque");
                mat.renderQueue = def.alpha ? (int)UnityEngine.Rendering.RenderQueue.AlphaTest : -1;
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
        }

        public static GameObject Model(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(ForestAssetPostprocessor.ModelDir + name + ".fbx");
        public static Material Material(string name) => AssetDatabase.LoadAssetAtPath<Material>(ForestAssetPostprocessor.MatDir + name + ".mat");
    }
}
