using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Low-cost cartoon explosion for the bomb (2026-10-27), played on every machine by <see cref="BombThrower"/>: a white-hot flash,
    /// orange fireball puffs that swell and turn to dark smoke, a ground shockwave ring, sparks, dirt chunks thrown up, a short warm
    /// light, the boom (bomb_explode_*) and a camera shake that fades with distance. One shared set of world-space particle systems
    /// (Resources/FX materials from FxAssets: MuzzleFlash, MuzzleSpark, MuzzleSmoke, Ring).
    /// </summary>
    public class BombFx : MonoBehaviour
    {
        static BombFx instance;
        ParticleSystem flash, fire, smoke, ring, sparks, chunks;
        Light glow;
        float glowStart = -10f;
        bool ok;

        public static void Explode(Vector3 pos, float radius)
        {
            if (Application.isBatchMode) return;
            if (instance == null) instance = new GameObject("BombFx").AddComponent<BombFx>();
            instance.Burst(pos, radius);
        }

        void Awake()
        {
            var sparkMat = Resources.Load<Material>("FX/MuzzleSpark");
            var smokeMat = Resources.Load<Material>("FX/MuzzleSmoke");
            var flashMat = Resources.Load<Material>("FX/MuzzleFlash");
            var ringMat = Resources.Load<Material>("FX/Ring");
            if (sparkMat == null || smokeMat == null) return;
            if (flashMat == null) flashMat = sparkMat;
            if (ringMat == null) ringMat = sparkMat;

            flash = Make("Flash", Tinted(flashMat, new Color(6f, 4.6f, 3f)), ParticleSystemRenderMode.Billboard, 0f);
            Grow(flash, 0.5f, 1.2f); Fade(flash, 0.2f);
            fire = Make("Fire", Tinted(smokeMat, new Color(4f, 1.7f, 0.45f)), ParticleSystemRenderMode.Billboard, -0.15f);
            Grow(fire, 0.4f, 1.5f); Fade(fire, 0.35f); Drag(fire, 4f);
            smoke = Make("Smoke", smokeMat, ParticleSystemRenderMode.Billboard, -0.06f);
            Grow(smoke, 0.5f, 1.9f); Fade(smoke, 0.5f); Drag(smoke, 2.5f);
            ring = Make("Ring", Tinted(ringMat, new Color(2.6f, 1.9f, 1.2f)), ParticleSystemRenderMode.HorizontalBillboard, 0f);
            Grow(ring, 0.1f, 1f); Fade(ring, 0.2f);
            sparks = Make("Sparks", Tinted(sparkMat, new Color(4f, 2.4f, 0.8f)), ParticleSystemRenderMode.Stretch, 1f);
            var sr = sparks.GetComponent<ParticleSystemRenderer>(); sr.velocityScale = 0.04f; sr.lengthScale = 1.5f;
            Fade(sparks, 0.6f);
            chunks = Make("Chunks", Tinted(smokeMat, new Color(0.35f, 0.27f, 0.2f)), ParticleSystemRenderMode.Billboard, 2.2f);
            Fade(chunks, 0.75f);

            var lg = new GameObject("Glow"); lg.transform.SetParent(transform, false);
            glow = lg.AddComponent<Light>();
            glow.type = LightType.Point; glow.color = new Color(1f, 0.62f, 0.3f); glow.range = 14f; glow.shadows = LightShadows.None;
            glow.renderMode = LightRenderMode.ForcePixel; glow.enabled = false;
            ok = true;
        }

        void Burst(Vector3 p, float radius)
        {
            if (!ok) return;
            float s = Mathf.Clamp(radius / 5.5f, 0.6f, 1.6f);
            flash.Emit(P(p + Vector3.up * 0.4f, Vector3.zero, 4.5f * s, 0.14f, Color.white), 1);
            ring.Emit(P(p + Vector3.up * 0.08f, Vector3.zero, radius * 2.3f, 0.45f, Color.white), 1);
            for (int i = 0; i < 14; i++)   // fireball: puffs pushed out, rising
            {
                var d = Random.onUnitSphere; d.y = Mathf.Abs(d.y) * 0.8f + 0.2f;
                fire.Emit(P(p + d * 0.3f, d * Random.Range(3f, 6.5f) * s + Vector3.up * 1.2f, Random.Range(1.3f, 2.2f) * s,
                            Random.Range(0.35f, 0.6f), new Color(1f, Random.Range(0.75f, 1f), Random.Range(0.6f, 0.9f), 1f)), 1);
            }
            for (int i = 0; i < 12; i++)   // smoke left behind, slower and darker
            {
                var d = Random.insideUnitSphere; d.y = Mathf.Abs(d.y) * 0.6f + 0.2f;
                float shade = Random.Range(0.28f, 0.45f);
                smoke.Emit(P(p + d * 0.8f + Vector3.up * 0.6f, d * Random.Range(1f, 2.5f) * s + Vector3.up * 0.9f, Random.Range(2f, 3.2f) * s,
                             Random.Range(1.8f, 2.8f), new Color(shade, shade * 0.95f, shade * 0.9f, Random.Range(0.65f, 0.85f))), 1);
            }
            for (int i = 0; i < 40; i++)
                sparks.Emit(P(p + Vector3.up * 0.3f, Random.onUnitSphere * Random.Range(6f, 15f) + Vector3.up * 4f,
                              Random.Range(0.04f, 0.08f), Random.Range(0.35f, 0.8f), Color.white), 1);
            for (int i = 0; i < 18; i++)
            {
                var d = Random.onUnitSphere; d.y = Mathf.Abs(d.y) + 0.8f;
                chunks.Emit(P(p, d.normalized * Random.Range(4f, 9f), Random.Range(0.08f, 0.18f), Random.Range(0.9f, 1.5f), Color.white), 1);
            }
            GameAudio.Play("bomb_explode", p, 1f, 8f, 160f, 1f, 0.06f);
            glow.transform.position = p + Vector3.up * 0.8f;
            glowStart = Time.time;
            glow.enabled = true;

            // shake: strong up close, gone beyond ~35 m
            var cam = Camera.main;
            if (cam != null)
            {
                float dist = Vector3.Distance(cam.transform.position, p);
                float k = Mathf.Clamp01(1f - dist / 35f);
                if (k > 0f) CameraFx.Shake(0.15f + 0.75f * k * k);
            }
        }

        void Update()
        {
            if (glow == null || !glow.enabled) return;
            float t = (Time.time - glowStart) / 0.35f;
            if (t >= 1f) { glow.enabled = false; return; }
            glow.intensity = 14f * (1f - t) * (1f - t);
        }

        // ------------------------------------------------------------------ helpers (same style as KillFx)
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
            main.startSpeed = 0f; main.maxParticles = 300; main.gravityModifier = gravity;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat; r.renderMode = mode;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            r.maxParticleSize = 4f;
            ps.Play();
            return ps;
        }

        static void Grow(ParticleSystem ps, float from, float to)
        {
            var s = ps.sizeOverLifetime; s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, from, 0f, 4f), new Keyframe(1f, to, 0f, 0f)));
        }

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
