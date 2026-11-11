using UnityEditor;

namespace CatBattleground
{
    /// <summary>
    /// Import rules for the lobby art in Assets/Resources/UI (copied from "CMBG FINAL UI/Parts" by Tools/UI/prepare_lobby_assets.py):
    /// single sprites, no mipmaps, crunch-compressed (quality 75), clamped.
    /// </summary>
    public class LobbyAssetImporter : AssetPostprocessor
    {
        public override uint GetVersion() => 2;   // bumped when the rules change: Unity re-imports the UI textures
        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/Resources/UI/") || !(assetImporter is TextureImporter ti)) return;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            ti.filterMode = UnityEngine.FilterMode.Bilinear;
            ti.maxTextureSize = 2048;
            // 2026-10-28 (WebGL size): was Uncompressed (~52 MB of RGBA for 122 sprites). Block-compressed + crunched now; the sprites
            // are padded to multiples of 4 by Tools/UI/pad_to_multiple_of_4.py, otherwise WebGL leaves them uncompressed.
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.crunchedCompression = true;
            ti.compressionQuality = 75;
        }
    }
}
