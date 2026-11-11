using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Renders the island into a 256 x 256 top-down texture (sea, sand, grass with forest shading, dirt paths and the
    /// building footprints) from the same data used to build the arena. No runtime camera is needed, which keeps the WebGL
    /// build light. Texture x = world x, texture y = world z (north up).
    /// </summary>
    public static class MinimapBaker
    {
        public const float Extent = TerrainBaker.Extent;   // map covers the whole terrain in x and z
        const int Size = 384;
        const string Path = "Assets/Art/Generated/Minimap.png";

        public static Texture2D Bake()
        {
            var pixels = new Color[Size * Size];
            float step = Extent * 2f / Size;

            for (int j = 0; j < Size; j++)
            {
                for (int i = 0; i < Size; i++)
                {
                    float x = -Extent + (i + 0.5f) * step, z = -Extent + (j + 0.5f) * step;
                    float h = ArenaBuilder.Height(x, z);
                    Color c;
                    if (h < -0.95f)
                        c = Color.Lerp(new Color(0.32f, 0.62f, 0.80f), new Color(0.14f, 0.34f, 0.57f), Mathf.Clamp01((-0.95f - h) / 3f));
                    else if (h < -0.3f)
                        c = new Color(0.88f, 0.82f, 0.62f);   // beach
                    else
                    {
                        float forest = ArenaBuilder.Forest(x, z);
                        c = Color.Lerp(new Color(0.55f, 0.76f, 0.38f), new Color(0.30f, 0.52f, 0.28f), forest);
                        float dx = ArenaBuilder.Height(x + 1.5f, z) - ArenaBuilder.Height(x - 1.5f, z);
                        float dz = ArenaBuilder.Height(x, z + 1.5f) - ArenaBuilder.Height(x, z - 1.5f);
                        c *= Mathf.Clamp(1f + (-dx + dz) * 0.16f, 0.75f, 1.2f);
                        float path = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.9f, 1.9f, ArenaBuilder.PathDistance(x, z)));
                        c = Color.Lerp(c, new Color(0.80f, 0.64f, 0.42f), path);
                        c.a = 1f;
                    }
                    pixels[j * Size + i] = c;
                }
            }

            foreach (var s in ArenaBuilder.Sites)
            {
                switch (s.kind)
                {
                    case "house": Footprint(pixels, s, 6.6f, 5.6f, new Color(0.74f, 0.32f, 0.26f)); break;
                    case "ruin": Footprint(pixels, s, 6.6f, 5.6f, new Color(0.45f, 0.40f, 0.38f)); break;
                    case "town": Footprint(pixels, s, s.fx, s.fz, new Color(0.86f, 0.70f, 0.58f)); break;   // BattlefieldTown plaster
                    case "townwall": Footprint(pixels, s, s.fx, Mathf.Max(s.fz, 0.8f), new Color(0.62f, 0.52f, 0.44f)); break;
                    case "barracks": Footprint(pixels, s, 16.4f, 6.6f, new Color(0.33f, 0.40f, 0.24f)); break;
                    case "tower": Footprint(pixels, s, 4.2f, 4.2f, new Color(0.25f, 0.18f, 0.1f)); break;
                    case "tent": Footprint(pixels, s, 5f, 3.8f, new Color(0.38f, 0.45f, 0.26f)); break;
                }
            }

            // thin dark border so the edge of the map is clear
            for (int k = 0; k < Size; k++)
                pixels[k] = pixels[(Size - 1) * Size + k] = pixels[k * Size] = pixels[k * Size + Size - 1] = new Color(0.1f, 0.1f, 0.12f);

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply();
            File.WriteAllBytes(Path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(Path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(Path);
            importer.textureType = TextureImporterType.Default;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 512;
            importer.textureCompression = TextureImporterCompression.Compressed;   // was Uncompressed: 0.75 MB (WebGL size pass)
            importer.crunchedCompression = true; importer.compressionQuality = 85;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(Path);
        }

        // Rotated rectangle (width along the building's local x, depth along its local z).
        static void Footprint(Color[] pixels, ArenaBuilder.Site s, float width, float depth, Color color)
        {
            float step = Extent * 2f / Size;
            float rad = s.yaw * Mathf.Deg2Rad, cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            for (float u = -width / 2f; u <= width / 2f; u += step * 0.5f)
                for (float v = -depth / 2f; v <= depth / 2f; v += step * 0.5f)
                {
                    float x = s.pos.x + u * cos + v * sin, z = s.pos.z - u * sin + v * cos;
                    int ix = Mathf.RoundToInt((x + Extent) / step), iz = Mathf.RoundToInt((z + Extent) / step);
                    if (ix < 0 || iz < 0 || ix >= Size || iz >= Size) continue;
                    pixels[iz * Size + ix] = color;
                }
        }
    }
}
