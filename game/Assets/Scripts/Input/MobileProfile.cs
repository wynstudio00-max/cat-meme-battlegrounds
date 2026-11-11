using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// Lighter graphics on phones (GameInput.TouchMode, 2026-10-28; lighter again 2026-10-31): 60 fps cap, render scale 0.7 (page caps the pixel density at 2),
    /// one 512 px shadow cascade out to 25 m, no HDR / MSAA, half-resolution textures, no bloom (PostFxSetup checks <see cref="Light"/>).
    /// Only in builds (in the editor it would change the URP asset on disk). ZombieWaveManager / SceneAtmosphere check
    /// <see cref="GameInput.TouchMode"/> themselves (fewer zombies alive, no ambient leaves/butterflies).
    /// </summary>
    public static class MobileProfile
    {
        public const int ZombieAliveCap = 22;

        /// <summary>The phone profile is on (touch device, real build).</summary>
        public static bool Light => GameInput.TouchMode && !Application.isEditor;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Apply()
        {
            // WebGL cannot run FSR (picked by "Auto" when render scale < 1): it switches every post-processing pass off.
            // Mobile_RPAsset already says Bilinear; this makes sure even if the asset is changed back (2026-10-29).
            if (Application.platform == RuntimePlatform.WebGLPlayer && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset web)
                web.upscalingFilter = UpscalingFilterSelection.Linear;
            if (!GameInput.TouchMode || Application.isEditor) return;
            Application.targetFrameRate = 60;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = 0.7f;      // of a canvas already capped at 2x pixel density by the page (index.html)
                urp.shadowDistance = Mathf.Min(urp.shadowDistance, 25f);
                urp.shadowCascadeCount = 1;
                urp.mainLightShadowmapResolution = 512;
                urp.supportsHDR = false;      // smaller render targets; bloom is off on phones anyway
                urp.msaaSampleCount = 1;
            }
            QualitySettings.globalTextureMipmapLimit = 1;   // half-size textures on the GPU: less memory, less heat
            QualitySettings.lodBias = Mathf.Min(QualitySettings.lodBias, 0.6f);
        }
    }
}
