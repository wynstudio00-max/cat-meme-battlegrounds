using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Paints the terrain albedo (2048 x 2048 for the 300 m island): painterly grass, cracked dirt paths, gravel pads under the
    /// buildings, dirt clearings, sand on the beach. One texture, one material, no tiling or blending shader needed.
    /// </summary>
    public static class TerrainBaker
    {
        public const int Size = 2048;
        public const float Extent = 75f * ArenaBuilder.MapScale;
        const string GrassTile = "Assets/Art/Arena/Textures/grass_tile.png";
        const string DirtTile = "Assets/Art/Arena/Textures/dirt_tile.png";
        const string OutPath = "Assets/Art/Generated/Terrain_Albedo.png";

        public static Texture2D Bake()
        {
            var grass = Load(GrassTile);
            var dirt = Load(DirtTile);
            var pixels = new Color32[Size * Size];
            float step = Extent * 2f / Size;
            var sand = new Color(0.86f, 0.78f, 0.56f);

            for (int j = 0; j < Size; j++)
            {
                for (int i = 0; i < Size; i++)
                {
                    float x = -Extent + (i + 0.5f) * step, z = -Extent + (j + 0.5f) * step;
                    float h = ArenaBuilder.Height(x, z);

                    Color g = grass.GetPixelBilinear(x / 9f, z / 9f);
                    Color d = dirt.GetPixelBilinear(x / 6f, z / 6f);

                    // dirt: paths, clearings, gravel pads
                    float pd = ArenaBuilder.PathDistance(x, z);
                    float wd = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.1f, 2.5f, pd));
                    float patch = Mathf.PerlinNoise(x * 0.06f + 31.7f, z * 0.06f + 12.3f);
                    wd = Mathf.Max(wd, Mathf.SmoothStep(0.70f, 0.82f, patch) * 0.85f);
                    wd = Mathf.Max(wd, ArenaBuilder.PadWeight(x, z) * 0.9f);

                    Color c = Color.Lerp(g, d, wd);

                    // darker, mossier ground in the forest, lighter on the meadows
                    float forest = ArenaBuilder.Forest(x, z);
                    c *= Mathf.Lerp(1.05f, 0.82f, forest * (1f - wd));

                    // beach and wet sand
                    float beach = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.75f, -0.15f, h));
                    c = Color.Lerp(c, sand * Mathf.Lerp(0.75f, 1.05f, d.grayscale), beach);

                    // big, soft brightness variation so the meadows are not flat
                    c *= 0.90f + 0.20f * Mathf.PerlinNoise(x * 0.13f + 4.2f, z * 0.13f + 9.9f);
                    c.a = 1f;
                    pixels[j * Size + i] = c;
                }
            }

            Object.DestroyImmediate(grass);
            Object.DestroyImmediate(dirt);

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(OutPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(OutPath);

            var importer = (TextureImporter)AssetImporter.GetAtPath(OutPath);
            importer.textureType = TextureImporterType.Default;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 2048;
            importer.crunchedCompression = true;
            importer.compressionQuality = 60;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(OutPath);
        }

        /// <summary>
        /// Ground of the Zombie Survival town (ArenaBuilder.Zombie, 2026-10-28, user: not all grass): grass blended with dirt -
        /// worn dirt streets between the 21 m blocks, the town square, bare yards around every house / wall (<paramref name="pads"/>:
        /// local x, z, radius), patchy dirt in the grass, and a darker needle-strewn forest floor past the walls.
        /// One texture over the whole <paramref name="half"/> x 2 square (uv 0..1).
        /// </summary>
        public static Texture2D BakeZombie(float half, float block, float wallHalf, System.Collections.Generic.List<Vector3> pads)
        {
            const int size = 2048;
            const string outPath = "Assets/Art/Generated/ZombieGround_Albedo.png";
            var grass = Load(GrassTile);
            var dirt = Load(DirtTile);
            var pixels = new Color32[size * size];
            float step = half * 2f / size;
            var needles = new Color(0.42f, 0.33f, 0.22f);
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    float x = -half + (i + 0.5f) * step, z = -half + (j + 0.5f) * step;
                    Color g = grass.GetPixelBilinear(x / 9f, z / 9f);
                    Color d = dirt.GetPixelBilinear(x / 6f, z / 6f);
                    float wobble = (Mathf.PerlinNoise(x * 0.35f + 3.1f, z * 0.35f + 8.7f) - 0.5f) * 1.6f;   // ragged edges

                    // streets halfway between the blocks (x or z near (k + 0.5) * block)
                    float sx = Mathf.Abs(Mathf.Repeat(x / block + 0.5f, 1f) - 0.5f) * block;   // distance to the nearest block centre line
                    float sz = Mathf.Abs(Mathf.Repeat(z / block + 0.5f, 1f) - 0.5f) * block;
                    float street = Mathf.Max(sx, sz) - block * 0.5f + 1.4f + wobble;              // > 0 within ~1.4 m of a street line
                    float wd = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.6f, 0.9f, street)) * 0.8f;
                    // the square in the middle
                    float sq = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) - 9.5f + wobble;
                    wd = Mathf.Max(wd, 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.5f, 1.5f, sq)));
                    // bare yards around the houses and walls
                    foreach (var p in pads)
                    {
                        if (Mathf.Abs(x - p.x) > p.z + 3.5f || Mathf.Abs(z - p.y) > p.z + 3.5f) continue;   // far away: skip the sqrt
                        float dd = Mathf.Sqrt((x - p.x) * (x - p.x) + (z - p.y) * (z - p.y)) - p.z + wobble;
                        if (dd < 2f) wd = Mathf.Max(wd, (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 2f, dd))) * 0.75f);
                    }
                    // dirt patches in the grass, smaller specks
                    float patch = Mathf.PerlinNoise(x * 0.07f + 31.7f, z * 0.07f + 12.3f);
                    wd = Mathf.Max(wd, Mathf.SmoothStep(0.62f, 0.76f, patch) * 0.7f);
                    float speck = Mathf.PerlinNoise(x * 0.4f + 71f, z * 0.4f + 5f);
                    wd = Mathf.Max(wd, Mathf.SmoothStep(0.74f, 0.82f, speck) * 0.5f);

                    Color c = Color.Lerp(g, d, wd);
                    // forest floor past the walls: darker, brown needles mixed in
                    float forest = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(wallHalf - 2f, wallHalf + 8f, Mathf.Max(Mathf.Abs(x), Mathf.Abs(z))));
                    c = Color.Lerp(c, Color.Lerp(g, needles * Mathf.Lerp(0.8f, 1.1f, d.grayscale), 0.55f) * 0.78f, forest);
                    c *= 0.88f + 0.22f * Mathf.PerlinNoise(x * 0.13f + 4.2f, z * 0.13f + 9.9f);
                    c.a = 1f;
                    pixels[j * size + i] = c;
                }
            Object.DestroyImmediate(grass);
            Object.DestroyImmediate(dirt);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels); tex.Apply();
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(outPath);
            importer.textureType = TextureImporterType.Default;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true; importer.anisoLevel = 4;
            importer.maxTextureSize = 2048; importer.crunchedCompression = true; importer.compressionQuality = 60;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        // The tiles are imported as compressed textures, so read the source files directly.
        static Texture2D Load(string assetPath)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
            tex.LoadImage(File.ReadAllBytes(assetPath));
            return tex;
        }
    }
}
