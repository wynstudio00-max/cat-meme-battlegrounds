using System.Collections;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Cartoon elimination burst, played on every machine when a cat dies (<see cref="HitReaction"/>): a white-hot flash with a
    /// warm light, a shockwave ring, golden stars spinning out, colourful confetti raining down, sparks and a big puff of smoke.
    /// One shared set of world-space particle systems (Resources/FX materials from FxAssets: Star, Ring, MuzzleSpark, MuzzleSmoke).
    /// </summary>
    public class KillFx : MonoBehaviour
    {
        static KillFx instance;
        ParticleSystem stars, ring, confetti, sparks, smoke, flash;
        Light glow;
        float glowStart = -10f;
        bool ok;

        static readonly Color[] ConfettiColors =
        {
            new Color(1f, 0.35f, 0.3f), new Color(1f, 0.8f, 0.2f), new Color(0.35f, 0.85f, 0.4f),
            new Color(0.3f, 0.65f, 1f), new Color(0.9f, 0.45f, 1f), new Color(1f, 0.6f, 0.2f),
        };

        public static void Play(Vector3 chest)
        {
            if (Application.isBatchMode) return;
            if (instance == null) instance = new GameObject("KillFx").AddComponent<KillFx>();
            instance.Burst(chest);
        }

        void Awake()
        {
            var starMat = Resources.Load<Material>("FX/Star");
            var ringMat = Resources.Load<Material>("FX/Ring");
            var sparkMat = Resources.Load<Material>("FX/MuzzleSpark");
            var smokeMat = Resources.Load<Material>("FX/MuzzleSmoke");
            var flashMat = Resources.Load<Material>("FX/MuzzleFlash");
            if (sparkMat == null || smokeMat == null) return;
            if (starMat == null) starMat = sparkMat;
            if (ringMat == null) ringMat = sparkMat;

            stars = Make("Stars", Tinted(starMat, new Color(3.2f, 2.5f, 0.9f)), ParticleSystemRenderMode.Billboard, 0.55f);
            Spin(stars, 360f); Shrink(stars); Fade(stars, 0.7f);
            ring = Make("Ring", Tinted(ringMat, new Color(2.4f, 2.1f, 1.6f)), ParticleSystemRenderMode.Billboard, 0f);
            GrowCurve(ring, 0.15f, 1f); Fade(ring, 0.25f);
            confetti = Make("Confetti", Tinted(sparkMat, new Color(1.6f, 1.6f, 1.6f)), ParticleSystemRenderMode.Billboard, 0.35f);
            Spin(confetti, 540f); Fade(confetti, 0.85f); Drag(confetti, 2.2f);
            sparks = Make("Sparks", Tinted(sparkMat, new Color(3f, 2.2f, 1f)), ParticleSystemRenderMode.Stretch, 1f);
            var sr = sparks.GetComponent<ParticleSystemRenderer>(); sr.velocityScale = 0.03f; sr.lengthScale = 1.5f;
            Fade(sparks, 0.6f);
            smoke = Make("Smoke", smokeMat, ParticleSystemRenderMode.Billboard, -0.05f);
            GrowCurve(smoke, 0.5f, 1f); Fade(smoke, 0.55f); Drag(smoke, 1.5f);
            flash = Make("Flash", Tinted(flashMat != null ? flashMat : sparkMat, new Color(5f, 4f, 3f)), ParticleSystemRenderMode.Billboard, 0f);
            GrowCurve(flash, 0.6f, 1f); Fade(flash, 0.3f);

            var lg = new GameObject("Glow"); lg.transform.SetParent(transform, false);
            glow = lg.AddComponent<Light>();
            glow.type = LightType.Point; glow.color = new Color(1f, 0.75f, 0.45f); glow.range = 9f; glow.shadows = LightShadows.None;
            glow.renderMode = LightRenderMode.ForcePixel; glow.enabled = false;
            ok = true;
        }

        void Burst(Vector3 p)
        {
            if (!ok) return;
            flash.Emit(P(p, Vector3.zero, 2.2f, 0.16f, Color.white), 1);
            ring.Emit(P(p, Vector3.zero, 4.5f, 0.38f, Color.white), 1);
            for (int i = 0; i < 14; i++)
            {
                var d = Random.onUnitSphere; d.y = Mathf.Abs(d.y) * 0.9f + 0.25f;
                stars.Emit(P(p, d.normalized * Random.Range(3.2f, 6.5f), Random.Range(0.22f, 0.42f), Random.Range(0.75f, 1.1f), Color.white), 1);
            }
            for (int i = 0; i < 46; i++)
            {
                var d = Random.onUnitSphere; d.y = Mathf.Abs(d.y) + 0.6f;
                var c = ConfettiColors[Random.Range(0, ConfettiColors.Length)];
                confetti.Emit(P(p, d.normalized * Random.Range(3f, 7.5f), Random.Range(0.07f, 0.12f), Random.Range(1.6f, 2.6f), c), 1);
            }
            for (int i = 0; i < 26; i++)
                sparks.Emit(P(p, Random.onUnitSphere * Random.Range(4f, 10f) + Vector3.up * 2f, Random.Range(0.03f, 0.06f), Random.Range(0.25f, 0.5f), Color.white), 1);
            for (int i = 0; i < 9; i++)
            {
                var d = Random.insideUnitSphere; d.y = Mathf.Abs(d.y) * 0.5f;
                float shade = Random.Range(0.82f, 1f);
                smoke.Emit(P(p + d * 0.3f, d * Random.Range(1.2f, 2.6f) + Vector3.up * 0.6f, Random.Range(1.1f, 1.9f), Random.Range(1.1f, 1.7f),
                             new Color(shade, shade, shade, Random.Range(0.55f, 0.75f))), 1);
            }
            GameAudio.Play("kill_poof", p, 1f, 4f, 70f);
            glow.transform.position = p + Vector3.up * 0.3f;
            glowStart = Time.time;
            glow.enabled = true;
        }

        void Update()
        {
            if (glow == null || !glow.enabled) return;
            float t = (Time.time - glowStart) / 0.22f;
            if (t >= 1f) { glow.enabled = false; return; }
            glow.intensity = 9f * (1f - t) * (1f - t);
        }

        // ------------------------------------------------------------------ helpers
        static ParticleSystem.EmitParams P(Vector3 pos, Vector3 vel, float size, float life, Color color) => new ParticleSystem.EmitParams
        {
            position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color, applyShapeToPosition = false,
            rotation = Random.Range(0f, 360f),
        };

        static Material Tinted(Material source, Color hdr)
        {
            var m = new Material(source);
            m.SetColor("_BaseColor", hdr);
            return m;
        }

        ParticleSystem Make(string name, Material mat, ParticleSystemRenderMode mode, float gravity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = true; main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.maxParticles = 400; main.gravityModifier = gravity;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat; r.renderMode = mode;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            r.maxParticleSize = 3f;
            ps.Play();
            return ps;
        }

        static void Spin(ParticleSystem ps, float degPerSec)
        {
            var rot = ps.rotationOverLifetime; rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-degPerSec * Mathf.Deg2Rad, degPerSec * Mathf.Deg2Rad);
        }

        static void Shrink(ParticleSystem ps)
        {
            var s = ps.sizeOverLifetime; s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.12f, 1.25f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0f)));
        }

        static void GrowCurve(ParticleSystem ps, float from, float to)
        {
            var s = ps.sizeOverLifetime; s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, from, 0f, 4f), new Keyframe(1f, to, 0f, 0f)));
        }

        // alpha: full, then fades from 'holdUntil' (0..1 of the life) to zero
        static void Fade(ParticleSystem ps, float holdUntil)
        {
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, holdUntil), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        static void Drag(ParticleSystem ps, float drag)
        {
            var l = ps.limitVelocityOverLifetime; l.enabled = true; l.drag = drag; l.multiplyDragByParticleSize = false; l.multiplyDragByParticleVelocity = false;
            l.limit = 100f;
        }
    }
}
