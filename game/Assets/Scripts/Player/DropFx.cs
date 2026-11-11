using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Free-fall feel for the local player (driven by <see cref="PlayerDrop"/> every frame on the owner):
    /// wind streaks rushing up past the camera (many and fast in free fall, few and slow under the canopy), a wider FOV and a
    /// light continuous wind buffet in free fall. Visual only.
    /// </summary>
    public static class DropFx
    {
        static ParticleSystem streaks;
        static bool tried;
        static float rate;
        static AudioSource windFree, windChute;

        /// <param name="state">the owner's own drop state</param>
        /// <param name="fallSpeed">current downward speed (m/s, positive)</param>
        public static void Tick(DropState state, float fallSpeed)
        {
            bool free = state == DropState.Freefall, chute = state == DropState.Parachute;
            CameraFx.FovOffsetTarget = free ? 11f : chute ? 3f : 0f;
            if (free) CameraFx.Sustain(0.16f);
            // wind: a roar in free fall, a soft breeze with canopy flutter under the parachute
            if ((free || chute) && windFree == null)
            {
                windFree = GameAudio.Loop("wind_freefall_loop", null, 0f);
                windChute = GameAudio.Loop("wind_chute_loop", null, 0f);
            }
            if (windFree != null)
            {
                windFree.volume = Mathf.MoveTowards(windFree.volume, free ? 0.75f : 0f, Time.deltaTime * (free ? 1.5f : 2.5f));
                windChute.volume = Mathf.MoveTowards(windChute.volume, chute ? 0.5f : 0f, Time.deltaTime * 1.2f);
            }

            var cam = Camera.main;
            if (cam == null || !Ready()) return;
            float target = free ? 70f : chute ? 14f : 0f;
            rate = Mathf.MoveTowards(rate, target, Time.deltaTime * 140f);
            if (rate < 0.5f) return;

            // emit streaks in a ring around the camera, below it, rushing up (the cat falls through them)
            int n = Mathf.FloorToInt(rate * Time.deltaTime + Random.value);
            var cp = cam.transform.position;
            float speed = Mathf.Max(fallSpeed, chute ? 5f : 18f) + (free ? 22f : 4f);
            for (int i = 0; i < n; i++)
            {
                var d = Random.insideUnitCircle.normalized * Random.Range(1.6f, 6.5f);
                var p = cp + new Vector3(d.x, Random.Range(-14f, -4f), d.y) + cam.transform.forward * Random.Range(0f, 4f);
                streaks.Emit(new ParticleSystem.EmitParams
                {
                    position = p, velocity = Vector3.up * speed * Random.Range(0.85f, 1.15f),
                    startSize = Random.Range(0.025f, 0.05f), startLifetime = Random.Range(0.35f, 0.6f),
                    startColor = new Color(1f, 1f, 1f, free ? Random.Range(0.25f, 0.5f) : 0.18f), applyShapeToPosition = false,
                }, 1);
            }
        }

        /// <summary>Called when the drop is over (landed / reset): FOV back to normal.</summary>
        public static void Stop() { CameraFx.FovOffsetTarget = 0f; rate = 0f; }

        static bool Ready()
        {
            if (streaks != null) return true;
            if (tried) return false;
            tried = true;
            var spark = Resources.Load<Material>("FX/MuzzleSpark");
            if (spark == null) return false;
            var mat = new Material(spark); mat.SetColor("_BaseColor", new Color(1.3f, 1.35f, 1.45f));
            var go = new GameObject("FreefallStreaks");
            Object.DontDestroyOnLoad(go);
            streaks = go.AddComponent<ParticleSystem>();
            streaks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = streaks.main;
            main.loop = true; main.playOnAwake = false; main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.maxParticles = 400;
            var em = streaks.emission; em.enabled = false;
            var shape = streaks.shape; shape.enabled = false;
            var col = streaks.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat; r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.06f; r.lengthScale = 2f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            streaks.Play();
            return true;
        }
    }
}
