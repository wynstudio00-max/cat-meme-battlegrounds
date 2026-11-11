using System.IO;
using UnityEditor;

namespace CatBattleground
{
    /// <summary>
    /// 2026-11-10 size pass (user: keep the browser game light): a WebGL-only size cap on the big art textures. Runs after the
    /// other import rules (they set the default / PC settings) and only adds a WebGL platform override, so the editor, PC
    /// recordings and the cinematic films keep the full textures.
    ///   - cat / zombie / plane / forest colour maps: 2048 -> 1024
    ///   - normal, metallic, emissive maps: 512 (small on screen, cost as much as a colour map)
    ///   - weapon colour maps: 1024 (they fill the lobby preview)
    ///   - baked ground textures (Art/Generated: island terrain, zombie town ground) and all UI: untouched
    /// Bump GetVersion when the rules change: Unity then re-imports the affected textures.
    /// </summary>
    public class WebGLTextureRules : AssetPostprocessor
    {
        public override uint GetVersion() => 1;
        public override int GetPostprocessOrder() => 1000;   // after ArenaAssets / ForestAssets / LootAssets / TownAssets / CharacterImportSetup

        void OnPreprocessTexture()
        {
            if (!(assetImporter is TextureImporter ti)) return;
            string p = assetPath.Replace('\\', '/');
            if (!p.StartsWith("Assets/Art/")) return;
            if (p.StartsWith("Assets/Art/Generated/")) return;   // terrain / zombie ground / minimaps: one texture for the whole map

            int cap = Cap(Path.GetFileNameWithoutExtension(p));
            if (cap <= 0) return;

            var s = ti.GetPlatformTextureSettings("WebGL");
            int size = System.Math.Min(ti.maxTextureSize, cap);
            if (s.overridden && s.maxTextureSize == size && s.crunchedCompression) return;
            s.overridden = true;
            s.maxTextureSize = size;
            s.format = TextureImporterFormat.Automatic;
            s.textureCompression = TextureImporterCompression.Compressed;
            s.crunchedCompression = true;
            s.compressionQuality = 50;
            ti.SetPlatformTextureSettings(s);
        }

        static int Cap(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.EndsWith("_normal") || n.Contains("metallic") || n.Contains("emissive") || n.Contains("roughness")) return 512;
            return 1024;
        }
    }
}
