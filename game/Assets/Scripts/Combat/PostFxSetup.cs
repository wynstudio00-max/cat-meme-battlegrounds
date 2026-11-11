using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// Bloom for the gun effects: a global Volume made at runtime (no scene change needed) and post-processing switched on
    /// for the main camera. The threshold is above normal scene brightness, so mostly the HDR muzzle flash, tracers and sparks
    /// (MuzzleFlash tints them above 1) glow, like the reference clip.
    /// </summary>
    public static class PostFxSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || GameObject.Find("PostFX (runtime)") != null) return;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.15f);
            bloom.intensity.Override(0.9f);
            bloom.scatter.Override(0.68f);
            bloom.tint.Override(new Color(1f, 0.85f, 0.7f));
            bloom.highQualityFiltering.Override(false);   // cheaper for WebGL
            if (MobileProfile.Light) bloom.active = false;   // phones: the bloom passes are the most expensive part of post FX

            // cinematic grade (2026-10-16): soft filmic tonemap, a touch more contrast/colour, light vignette
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.1f);
            color.contrast.Override(12f);
            color.saturation.Override(12f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.2f);
            vignette.smoothness.Override(0.45f);

            var go = new GameObject("PostFX (runtime)");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.sharedProfile = profile;

            var cam = Camera.main;
            if (cam != null) cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        }
    }
}
