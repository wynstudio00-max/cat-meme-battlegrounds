using UnityEditor;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Import rules for the game's sounds (Assets/Resources/Audio, 2026-10-28 WebGL size pass): mono (3D sounds are positioned by the
    /// engine anyway), 32 kHz, Vorbis quality 0.5 (the WebGL build turns it into AAC at that quality). Loops stream-friendly
    /// (compressed in memory), short one-shots decompressed on load. Was: Unity's defaults (stereo, quality 100%).
    /// </summary>
    public class AudioImportRules : AssetPostprocessor
    {
        public override uint GetVersion() => 1;

        void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith("Assets/Resources/Audio/") || !(assetImporter is AudioImporter ai)) return;
            ai.forceToMono = true;
            ai.loadInBackground = false;
            bool loop = assetPath.Contains("_loop");
            var s = ai.defaultSampleSettings;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = loop ? 0.4f : 0.5f;
            s.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
            s.sampleRateOverride = 32000;
            s.loadType = loop ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
            s.preloadAudioData = true;
            ai.defaultSampleSettings = s;
        }
    }
}
