using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Gun-fire effects in the style of the reference clip (BunnyOps): a small but very bright muzzle flash that blooms, a big warm
    /// light that flickers over the ground, a short yellow tracer streak that travels to the target, sparks and a dust puff where
    /// it lands, glowing shell casings thrown to the side and soft grey smoke that builds up while firing and drifts away.
    /// Built lazily per shooter from the materials in Resources/FX (editor script FxAssets); everything is emitted in world space.
    /// Visual only, never on a server. Bloom comes from <see cref="PostFxSetup"/>.
    /// </summary>
    public class MuzzleFlash : MonoBehaviour
    {
        const float TracerSpeed = 140f;   // m/s: fast, but slow enough to see the streak travel

        static Material flashMat, sparkMat, smokeMat;
        static bool loaded;

        ParticleSystem flash, sparks, smoke, shells, tracer, impact, dust, chunks;
        Light glow;
        float glowPeak, glowStart = -10f;
        bool ready;

        public static MuzzleFlash For(GameObject owner)
        {
            if (Application.isBatchMode) return null;
            var fx = owner.GetComponentInChildren<MuzzleFlash>();
            if (fx == null)
            {
                var go = new GameObject("MuzzleFX");
                go.transform.SetParent(owner.transform, false);
                fx = go.AddComponent<MuzzleFlash>();
            }
            return fx.Build() ? fx : null;
        }

        /// <param name="size">1 = rifle; bigger for shotguns / snipers, smaller for SMG / pistol.</param>
        public void Play(Vector3 position, Vector3 direction, float size, Vector3 right)
        {
            direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : transform.forward;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(Vector3.up, direction).normalized;
            var up = Vector3.Cross(direction, right).normalized;

            // flash: a hot core and a slightly larger, redder halo; one or two frames only
            flash.Emit(P(position, Vector3.zero, size * Random.Range(0.16f, 0.22f), Random.Range(0.03f, 0.045f), new Color(1f, 0.92f, 0.7f, 1f)), 1);
            flash.Emit(P(position + direction * 0.06f, direction * 1.5f, size * Random.Range(0.3f, 0.4f), 0.05f, new Color(1f, 0.55f, 0.18f, 0.85f)), 1);

            // a few sparks flying forward
            int n = Random.Range(2, 5);
            for (int i = 0; i < n; i++)
            {
                var s = Random.insideUnitCircle * 0.25f;
                var v = (direction + right * s.x + up * s.y).normalized * Random.Range(6f, 12f);
                sparks.Emit(P(position, v, Random.Range(0.015f, 0.03f), Random.Range(0.05f, 0.12f), new Color(1f, 0.8f, 0.35f, 1f)), 1);
            }

            // shell casing: thrown right and up, falls, glows briefly
            var shellV = right * Random.Range(1.6f, 2.4f) + Vector3.up * Random.Range(1.4f, 2.2f) - direction * 0.4f;
            shells.Emit(P(position - direction * 0.35f, shellV, 0.035f, Random.Range(0.45f, 0.7f), new Color(1f, 0.62f, 0.25f, 1f)), 1);

            // thick smoke that lingers and builds up during sustained fire: two puffs per shot
            for (int i = 0; i < 2; i++)
            {
                var drift = direction * Random.Range(0.2f, 0.55f) + Vector3.up * Random.Range(0.15f, 0.4f) + right * Random.Range(-0.25f, 0.25f);
                var sp = P(position + direction * (0.1f + 0.15f * i) + Random.insideUnitSphere * 0.08f, drift,
                           size * Random.Range(0.6f, 0.85f), Random.Range(2f, 3f), new Color(0.7f, 0.7f, 0.7f, Random.Range(0.28f, 0.38f)));
                sp.rotation = Random.Range(0f, 360f); sp.angularVelocity = Random.Range(-20f, 20f);
                smoke.Emit(sp, 1);
            }

            // the big warm light that washes over the ground (rises instantly, falls off over ~70 ms)
            glow.transform.position = position + direction * 0.2f - Vector3.up * 0.15f;
            glow.range = 7f * size;
            glowPeak = Random.Range(3.5f, 5f) * size;
            glowStart = Time.time;
            glow.intensity = glowPeak; glow.enabled = true;
        }

        /// <summary>A short streak that travels from the muzzle to the target; sparks and dust where it lands when it hit something.</summary>
        // What a bullet hit, for the impact effect. Sent over the network as a byte.
        public const int Dirt = 0, Stone = 1, Wood = 2, Flesh = 3;

        /// <summary>Surface type from the arena's object names (ArenaBuilder): terrain / sandbags = dirt, rocks / ruins / barrels =
        /// stone (sparks), trees and wooden buildings = wood, anything with Health = flesh (no hole).</summary>
        public static int SurfaceOf(Collider c)
        {
            if (c == null) return Stone;
            if (c.GetComponentInParent<Health>() != null) return Flesh;
            for (var t = c.transform; t != null; t = t.parent)
            {
                string n = t.name.ToLowerInvariant();
                if (n == "terrain" || n.StartsWith("sandbag")) return Dirt;
                if (n.Contains("rock") || n.Contains("pebble") || n.StartsWith("ruin") || n.StartsWith("sm_house") || n.StartsWith("sm_wall") || n == "steppingstones" || n.StartsWith("barrel") || n.StartsWith("flagpole")) return Stone;
                if (n.Contains("tree") || n.StartsWith("house") || n.StartsWith("barracks") || n.StartsWith("crate") || n.StartsWith("fence")
                    || n.StartsWith("watchtower") || n.StartsWith("tent")) return Wood;
            }
            return Stone;
        }

        /// <summary>A short streak that travels from the muzzle to the target; the surface impact shows when it arrives.</summary>
        public void Tracer(Vector3 from, Vector3 to, Color tint, bool hit, Vector3 normal, int surface)
        {
            var d = to - from; float dist = d.magnitude;
            if (dist < 0.5f) return;
            var dir = d / dist;
            var c = Color.Lerp(tint, new Color(1f, 0.9f, 0.45f), 0.65f); c.a = 1f;
            tracer.Emit(P(from + dir * 0.3f, dir * TracerSpeed, 0.05f, Mathf.Max(0.02f, (dist - 0.3f) / TracerSpeed), c), 1);
            if (!hit) return;
            if (normal.sqrMagnitude < 1e-4f) normal = -dir;
            StartCoroutineSafe(Impact(to, normal.normalized, dir, surface, dist / TracerSpeed));
        }

        System.Collections.IEnumerator Impact(Vector3 point, Vector3 normal, Vector3 dir, int surface, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            // reflect a bit off the surface: debris flies out along the normal, biased away from the shooter and upwards
            var outDir = (normal * 1.2f - dir * 0.4f + Vector3.up * 0.3f).normalized;
            var right = Vector3.Cross(Vector3.up, normal); if (right.sqrMagnitude < 1e-4f) right = Vector3.right; right.Normalize();
            var up = Vector3.Cross(normal, right);

            Color dustC, chunkC; int sparkCount, chunkCount, puffs; float dustSize, chunkSpeed;
            switch (surface)
            {
                // 2026-10-16: bigger dust clouds and more debris on ground / objects (user request)
                case Dirt:  dustC = new Color(0.56f, 0.44f, 0.3f, 0.7f);  chunkC = new Color(0.32f, 0.24f, 0.16f, 0.95f); sparkCount = 0; chunkCount = 12; puffs = 6; dustSize = 0.75f; chunkSpeed = 3.8f; break;
                case Wood:  dustC = new Color(0.62f, 0.52f, 0.38f, 0.6f); chunkC = new Color(0.55f, 0.4f, 0.24f, 0.95f);  sparkCount = 3; chunkCount = 10; puffs = 4; dustSize = 0.55f; chunkSpeed = 4.2f; break;
                case Flesh: dustC = new Color(0.85f, 0.82f, 0.78f, 0.35f); chunkC = dustC;                                 sparkCount = 0; chunkCount = 0; puffs = 1; dustSize = 0.22f; chunkSpeed = 0f;   break;
                default:    dustC = new Color(0.62f, 0.62f, 0.6f, 0.62f);  chunkC = new Color(0.4f, 0.4f, 0.4f, 0.95f);    sparkCount = 11; chunkCount = 8; puffs = 4; dustSize = 0.55f; chunkSpeed = 4.8f; break;
            }

            for (int i = 0; i < sparkCount; i++)
            {
                var s = Random.insideUnitCircle;
                var v = (outDir + right * s.x * 0.8f + up * s.y * 0.8f).normalized * Random.Range(4f, 9f);
                impact.Emit(P(point, v, Random.Range(0.015f, 0.03f), Random.Range(0.08f, 0.22f), new Color(1f, 0.85f, 0.45f, 1f)), 1);
            }
            for (int i = 0; i < chunkCount; i++)
            {
                var s = Random.insideUnitCircle;
                var v = (outDir + right * s.x * 0.7f + up * s.y * 0.7f + Vector3.up * 0.5f).normalized * Random.Range(chunkSpeed * 0.5f, chunkSpeed);
                var cp = P(point + normal * 0.02f, v, Random.Range(0.035f, 0.075f), Random.Range(0.5f, 0.9f), chunkC);
                cp.rotation = Random.Range(0f, 360f); cp.angularVelocity = Random.Range(-400f, 400f);
                chunks.Emit(cp, 1);
            }
            for (int i = 0; i < puffs; i++)
            {
                var v = outDir * Random.Range(0.6f, 1.8f) + Vector3.up * Random.Range(0.25f, 0.8f) + Random.insideUnitSphere * 0.35f;
                var dp = P(point + normal * 0.08f, v, dustSize * Random.Range(0.75f, 1.25f), Random.Range(1.1f, 1.9f), dustC);
                dp.rotation = Random.Range(0f, 360f); dp.angularVelocity = Random.Range(-30f, 30f);
                dust.Emit(dp, 1);
            }
            if (surface != Flesh) BulletHoles.Add(point, normal, surface == Dirt ? 0.17f : 0.12f);
            GameAudio.Play(GameAudio.ImpactSound(surface), point, surface == Flesh ? 0.8f : 0.6f, 2f, 45f, 1f, 0.1f);
        }

        void StartCoroutineSafe(System.Collections.IEnumerator routine)
        {
            if (isActiveAndEnabled) StartCoroutine(routine);
        }

        void Update()
        {
            if (glow == null || !glow.enabled) return;
            float t = (Time.time - glowStart) / 0.07f;
            if (t >= 1f) { glow.enabled = false; return; }
            glow.intensity = glowPeak * (1f - t) * (1f - t);
        }

        static ParticleSystem.EmitParams P(Vector3 pos, Vector3 vel, float size, float life, Color color) => new ParticleSystem.EmitParams
        {
            position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color, applyShapeToPosition = false,
        };

        bool Build()
        {
            if (ready) return true;
            if (!loaded)
            {
                loaded = true;
                flashMat = Resources.Load<Material>("FX/MuzzleFlash");
                sparkMat = Resources.Load<Material>("FX/MuzzleSpark");
                smokeMat = Resources.Load<Material>("FX/MuzzleSmoke");
            }
            if (flashMat == null || smokeMat == null) return false;   // assets not generated yet (open the editor once)
            var spark = sparkMat != null ? sparkMat : flashMat;

            // HDR tints so the bright parts bloom (the particle colours themselves are capped at 1)
            flash = MakeSystem("Flash", Tinted(flashMat, new Color(4f, 2.6f, 1.3f)), ParticleSystemRenderMode.Billboard, 0f);
            sparks = MakeSystem("Sparks", Tinted(spark, new Color(3f, 2f, 0.9f)), ParticleSystemRenderMode.Stretch, 0f);
            Stretch(sparks, 0.025f, 1.5f);
            shells = MakeSystem("Shells", Tinted(spark, new Color(2.2f, 1.4f, 0.6f)), ParticleSystemRenderMode.Billboard, 1f);
            tracer = MakeSystem("Tracer", Tinted(spark, new Color(3.5f, 2.8f, 1.2f)), ParticleSystemRenderMode.Stretch, 0f);
            Stretch(tracer, 0.018f, 1f);
            impact = MakeSystem("Impact", Tinted(spark, new Color(3f, 2.2f, 1f)), ParticleSystemRenderMode.Stretch, 1.2f);
            Stretch(impact, 0.02f, 1.5f);
            smoke = MakeSystem("Smoke", smokeMat, ParticleSystemRenderMode.Billboard, -0.03f);
            dust = MakeSystem("Dust", smokeMat, ParticleSystemRenderMode.Billboard, 0f);
            chunks = MakeSystem("Chunks", smokeMat, ParticleSystemRenderMode.Billboard, 1.6f);   // dirt / stone chips / wood splinters

            Grow(smoke, 0.8f, 3.8f); Grow(dust, 0.5f, 2.6f); Grow(flash, 1f, 1.35f);
            Fade(flash, 1f, 0.9f); Fade(sparks, 1f, 0.8f); Fade(shells, 1f, 0.6f); Fade(tracer, 1f, 1f); Fade(impact, 1f, 0.7f);
            Fade(smoke, 0.95f, 0.75f, 0.1f); Fade(dust, 1f, 0.6f); Fade(chunks, 1f, 0.9f);
            Drag(smoke, 0.6f); Drag(dust, 0.8f);

            var lg = new GameObject("Glow");
            lg.transform.SetParent(transform, false);
            glow = lg.AddComponent<Light>();
            glow.type = LightType.Point; glow.color = new Color(1f, 0.62f, 0.3f); glow.shadows = LightShadows.None; glow.enabled = false;
            glow.renderMode = LightRenderMode.ForcePixel;
            ready = true;
            return true;
        }

        static Material Tinted(Material source, Color hdr)
        {
            var m = new Material(source);
            m.SetColor("_BaseColor", hdr);
            return m;
        }

        ParticleSystem MakeSystem(string name, Material mat, ParticleSystemRenderMode mode, float gravity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = true; main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.maxParticles = 600; main.gravityModifier = gravity;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat; r.renderMode = mode;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            ps.Play();   // keeps simulating the emitted particles
            return ps;
        }

        static void Stretch(ParticleSystem ps, float velocityScale, float lengthScale)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>(); r.velocityScale = velocityScale; r.lengthScale = lengthScale;
        }

        static void Grow(ParticleSystem ps, float from, float to)
        {
            var s = ps.sizeOverLifetime; s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, from, 0f, (to - from) * 2f), new Keyframe(1f, to, 0f, 0f)));
        }

        // alpha: full at the start (or after fadeIn), a mid value at 40%, gone at the end - a smooth ease out
        static void Fade(ParticleSystem ps, float start, float mid, float fadeIn = 0f)
        {
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            var alphas = fadeIn > 0f
                ? new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(start, fadeIn), new GradientAlphaKey(mid, 0.45f), new GradientAlphaKey(0f, 1f) }
                : new[] { new GradientAlphaKey(start, 0f), new GradientAlphaKey(mid, 0.4f), new GradientAlphaKey(0f, 1f) };
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, alphas);
            col.color = g;
        }

        static void Drag(ParticleSystem ps, float dampen)
        {
            var l = ps.limitVelocityOverLifetime; l.enabled = true; l.limit = 0.05f; l.dampen = dampen * 0.1f;
        }
    }
}
