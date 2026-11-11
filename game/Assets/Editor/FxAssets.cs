using System.IO;
using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Creates the weapon effect assets once (runs by itself after scripts compile): procedural flash / spark / smoke textures
    /// and URP particle materials in Assets/Resources/FX, so <see cref="MuzzleFlash"/> can load them at runtime and the
    /// shaders end up in every build (WebGL included). Delete the folder to regenerate.
    /// </summary>
    static class FxAssets
    {
        const string Dir = "Assets/Resources/FX";

        [InitializeOnLoadMethod]
        static void Init() => EditorApplication.delayCall += Ensure;

        [MenuItem("CatBattleground/Rebuild Weapon FX Assets")]
        static void Rebuild()
        {
            AssetDatabase.DeleteAsset(Dir);
            Ensure();
        }

        static void Ensure()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/MuzzleFlash.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/MuzzleSmoke.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/BulletHole.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Cloud.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Star.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Ring.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Leaf.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Wing.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Beam.mat") != null &&
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/ZoneWall.mat") != null) return;
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/Resources", "FX");

            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/MuzzleFlash.mat") == null)
            {
                var flash = SaveTexture("Flash", FlashPixels(128));
                var smoke = SaveTexture("Smoke", SoftPixels(64, 0.45f));
                var spark = SaveTexture("Spark", SoftPixels(32, 0.9f));
                MakeMaterial("MuzzleFlash", flash, additive: true);
                MakeMaterial("MuzzleSpark", spark, additive: true);
                MakeMaterial("MuzzleSmoke", smoke, additive: false);
            }
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/BulletHole.mat") == null)
                MakeMaterial("BulletHole", SaveTexture("BulletHole", HolePixels(64)), additive: false);
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Cloud.mat") == null)
                MakeMaterial("Cloud", SaveTexture("Cloud", CloudPixels(256), 256), additive: false);
            // elimination burst (KillFx): cartoon stars and a shockwave ring
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Star.mat") == null)
                MakeMaterial("Star", SaveTexture("Star", StarPixels(128)), additive: true);
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Ring.mat") == null)
                MakeMaterial("Ring", SaveTexture("Ring", RingPixels(128)), additive: true);
            // ambient life (AmbientLife): falling leaves and butterfly wings, white so the particle / instance colour tints them
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Leaf.mat") == null)
                MakeMaterial("Leaf", SaveTexture("Leaf", LeafPixels(64)), additive: false);
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Wing.mat") == null)
                MakeMaterial("Wing", SaveTexture("Wing", WingPixels(128)), additive: false);
            // loot light beams (LootVisual) and the animated storm wall (ZoneController, shader CatBattleground/ZoneWall)
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Beam.mat") == null)
                MakeMaterial("Beam", SaveTexture("Beam", BeamPixels(64)), additive: true);
            if (AssetDatabase.LoadAssetAtPath<Material>(Dir + "/ZoneWall.mat") == null)
            {
                var zs = Shader.Find("CatBattleground/ZoneWall");
                if (zs != null) AssetDatabase.CreateAsset(new Material(zs) { name = "ZoneWall" }, Dir + "/ZoneWall.mat");
            }
            AssetDatabase.SaveAssets();
            Debug.Log("CatBattleground: weapon FX assets created in " + Dir);
        }

        static Texture2D SaveTexture(string name, Color[] pixels, int maxSize = 128)
        {
            int size = (int)Mathf.Sqrt(pixels.Length);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels); tex.Apply();
            string path = $"{Dir}/{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = true; importer.wrapMode = TextureWrapMode.Clamp; importer.mipmapEnabled = false;
            importer.maxTextureSize = maxSize;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // A hot core with 6 uneven spikes: reads as a gun flash from any side.
        static Color[] FlashPixels(int n)
        {
            var px = new Color[n * n];
            float[] spike = { 1f, 0.55f, 0.85f, 0.5f, 0.95f, 0.6f };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f) * 2f;
                    float r = p.magnitude, a = Mathf.Atan2(p.y, p.x);
                    float sector = (a / (Mathf.PI * 2f) + 1f) * 6f;
                    int i = Mathf.FloorToInt(sector) % 6; float f = sector - Mathf.Floor(sector);
                    float reach = Mathf.Lerp(spike[i], spike[(i + 1) % 6], f) * Mathf.Pow(Mathf.Abs(Mathf.Cos(f * Mathf.PI)), 6f);
                    float rays = Mathf.Clamp01(1f - r / Mathf.Max(0.05f, reach)) * 0.9f;
                    float core = Mathf.Clamp01(1f - r / 0.35f);
                    float v = Mathf.Clamp01(rays + core * core * 1.2f);
                    px[y * n + x] = new Color(1f, 1f, 1f, v);
                }
            return px;
        }

        // Dark scorched hole: a black centre, a dark ring and a soft, slightly ragged edge.
        // Cumulus: a row of soft round lumps over a flatter base, white on top, a little grey underneath.
        static Color[] CloudPixels(int n)
        {
            var px = new Color[n * n];
            var rnd = new System.Random(7);
            var blobs = new System.Collections.Generic.List<Vector3>();   // x, y, radius in 0..1 space
            for (int i = 0; i < 9; i++)
            {
                float x = 0.18f + 0.64f * i / 8f + (float)(rnd.NextDouble() - 0.5) * 0.08f;
                float r = 0.12f + (float)rnd.NextDouble() * 0.12f * (1f - Mathf.Abs(x - 0.5f) * 1.4f);
                blobs.Add(new Vector3(x, 0.42f + r * 0.55f, r));
            }
            for (int i = 0; i < 5; i++) blobs.Add(new Vector3(0.3f + 0.4f * (float)rnd.NextDouble(), 0.62f + 0.1f * (float)rnd.NextDouble(), 0.11f + 0.06f * (float)rnd.NextDouble()));
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n, d = 0f;
                    foreach (var b in blobs)
                    {
                        float dx = (u - b.x) / b.z, dy = (v - b.y) / b.z;
                        d = Mathf.Max(d, 1f - (dx * dx + dy * dy));
                    }
                    float baseCut = Mathf.SmoothStep(0.3f, 0.42f, v);           // flat-ish bottom
                    float a = Mathf.Clamp01(d * 2.2f) * baseCut;
                    a *= Mathf.Clamp01(1f - Mathf.Abs(u - 0.5f) * 2f) * 1.6f;   // fade the ends
                    float shade = Mathf.Lerp(0.8f, 1f, Mathf.SmoothStep(0.35f, 0.7f, v));
                    px[y * n + x] = new Color(shade, shade, Mathf.Min(1f, shade * 1.03f), Mathf.Clamp01(a));
                }
            return px;
        }

        // Five-point cartoon star with a soft glow around it.
        static Color[] StarPixels(int n)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f) * 2f;
                    float r = p.magnitude, a = Mathf.Atan2(p.x, p.y);              // 0 = pointing up
                    float k = Mathf.Abs(Mathf.Repeat(a / (Mathf.PI * 2f / 5f), 1f) - 0.5f) * 2f;   // 1 at a tip, 0 between
                    float edge = Mathf.Lerp(0.42f, 0.9f, Mathf.Pow(k, 1.6f));     // star outline radius
                    float body = Mathf.Clamp01((edge - r) / 0.04f);
                    float glow = Mathf.Clamp01(1f - r) * 0.35f;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(body + glow * glow));
                }
            return px;
        }

        // Thin bright ring with soft edges (shockwave).
        static Color[] RingPixels(int n)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = new Vector2((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f).magnitude * 2f;
                    float v = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) / 0.12f);
                    px[y * n + x] = new Color(1f, 1f, 1f, v * v * (3f - 2f * v));
                }
            return px;
        }

        // Vertical light beam: bright soft core across, fading out towards the top (v = 1).
        static Color[] BeamPixels(int n)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = Mathf.Abs((x + 0.5f) / n * 2f - 1f), v = (y + 0.5f) / n;
                    float across = Mathf.Pow(Mathf.Clamp01(1f - u), 2.2f) + Mathf.Pow(Mathf.Clamp01(1f - u / 0.25f), 2f) * 0.6f;
                    float along = Mathf.Pow(1f - v, 1.6f) * Mathf.Clamp01(v / 0.04f);
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(across * along));
                }
            return px;
        }

        // A pointed leaf with a darker midrib, pointing up.
        static Color[] LeafPixels(int n)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n;              // u -1..1 across, v 0..1 along
                    float halfWidth = 0.62f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(v * 1.05f)) * (1f - 0.25f * v);
                    float edge = Mathf.Clamp01((halfWidth - Mathf.Abs(u)) / 0.08f);
                    float stem = v < 0.12f ? Mathf.Clamp01((0.06f - Mathf.Abs(u)) / 0.03f) : 0f;
                    float a = Mathf.Max(edge, stem);
                    float vein = Mathf.Clamp01(1f - Mathf.Abs(u) / 0.05f) * 0.35f + Mathf.Clamp01(1f - Mathf.Abs(Mathf.Abs(u) - (v - 0.2f) * 0.5f) / 0.03f) * 0.12f;
                    float shade = Mathf.Lerp(0.78f, 1f, 1f - Mathf.Abs(u)) - vein;
                    px[y * n + x] = new Color(shade, shade, shade, a);
                }
            return px;
        }

        // One butterfly wing (fore + hind lobe) seen from above, hinge on the left edge: white with a dark border and spots.
        static Color[] WingPixels(int n)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n;                          // u 0 hinge .. 1 tip, v 0 back .. 1 front
                    float fore = Ellipse(u, v, 0.48f, 0.66f, 0.5f, 0.3f, -0.5f);
                    float hind = Ellipse(u, v, 0.34f, 0.3f, 0.34f, 0.26f, 0.4f);
                    float d = Mathf.Max(fore, hind);                                       // > 0 inside
                    float a = Mathf.Clamp01(d / 0.06f);
                    float border = 1f - Mathf.Clamp01((d - 0.05f) / 0.1f);
                    float spot = Mathf.Clamp01(1f - (new Vector2(u - 0.62f, v - 0.72f).magnitude) / 0.07f) + Mathf.Clamp01(1f - (new Vector2(u - 0.4f, v - 0.25f).magnitude) / 0.05f);
                    float c = Mathf.Lerp(1f, 0.08f, Mathf.Clamp01(border * 0.9f + spot));
                    float vein = Mathf.Clamp01(1f - Mathf.Abs(Mathf.Sin(Mathf.Atan2(v - 0.5f, u) * 9f)) / 0.12f) * 0.25f * (1f - border);
                    c -= vein;
                    px[y * n + x] = new Color(c, c, c, a);
                }
            return px;
        }

        // signed-ish distance inside a rotated ellipse (positive inside)
        static float Ellipse(float u, float v, float cx, float cy, float rx, float ry, float angle)
        {
            float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);
            float dx = u - cx, dy = v - cy;
            float px = dx * ca + dy * sa, py = -dx * sa + dy * ca;
            float k = (px * px) / (rx * rx) + (py * py) / (ry * ry);
            return (1f - k) * Mathf.Min(rx, ry);
        }

        static Color[] HolePixels(int n)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f) * 2f;
                    float r = p.magnitude + 0.06f * Mathf.Sin(Mathf.Atan2(p.y, p.x) * 7f);
                    float alpha = Mathf.Clamp01((1f - r) / 0.45f);
                    float shade = Mathf.Lerp(0.02f, 0.22f, Mathf.Clamp01((r - 0.18f) / 0.6f));
                    px[y * n + x] = new Color(shade, shade * 0.95f, shade * 0.9f, alpha * alpha);
                }
            return px;
        }

        static Color[] SoftPixels(int n, float hardness)
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = new Vector2((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f).magnitude * 2f;
                    float v = Mathf.Clamp01(1f - r);
                    v = Mathf.Pow(v, Mathf.Lerp(2.5f, 0.6f, hardness));
                    px[y * n + x] = new Color(1f, 1f, 1f, v);
                }
            return px;
        }

        static void MakeMaterial(string name, Texture2D tex, bool additive)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            var mat = new Material(shader) { name = name };
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Surface", 1f);                       // transparent
            mat.SetFloat("_Blend", additive ? 2f : 0f);          // additive / alpha
            mat.SetFloat("_SrcBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.SrcAlpha));
            mat.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            AssetDatabase.CreateAsset(mat, $"{Dir}/{name}.mat");
        }
    }
}
