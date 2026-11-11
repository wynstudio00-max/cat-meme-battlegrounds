using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Dust kicked up by a cat (added by <see cref="PlayerAnimator"/>, every machine, never on a server): little puffs at every
    /// stride while running, a puff at take-off and a ring of dust when it lands (bigger after a long fall / parachute).
    /// Works from the transform's motion only, so it also runs for remote players and bots. Shared particle system
    /// (Resources/FX/MuzzleSmoke) for all cats.
    /// </summary>
    public class FootFx : MonoBehaviour
    {
        const float RunSpeed = 2.4f;          // above this a stride kicks dust (sprint is 4 m/s)
        const float StrideTime = 0.27f;

        static ParticleSystem dust;
        static bool tried;
        static readonly RaycastHit[] hits = new RaycastHit[8];

        Vector3 lastPos;
        float lastVy, airTime, strideTimer, stepTimer;
        bool grounded = true, side;
        PlayerDrop drop;
        Renderer anyRenderer;

        void Start()
        {
            lastPos = transform.position;
            drop = GetComponent<PlayerDrop>();
            anyRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var pos = transform.position;
            var v = (pos - lastPos) / dt;
            lastPos = pos;
            if (v.sqrMagnitude > 900f) { lastVy = 0f; return; }   // teleport / respawn
            if (anyRenderer != null && !anyRenderer.enabled) return;   // hidden in the drop plane

            bool flying = drop != null && drop.State != DropState.Ground;
            bool nowGrounded = !flying && OnGround(pos);
            float hSpeed = new Vector2(v.x, v.z).magnitude;

            if (nowGrounded && !grounded)
            {
                // landing: the faster it came down, the bigger the ring
                float impact = Mathf.Max(-lastVy, airTime > 0.35f ? 3f : 0f);
                if (impact > 2.5f) Ring(pos, Mathf.Clamp01((impact - 2.5f) / 8f), v);
                if (impact > 2f) Sound("land", pos, Mathf.Clamp(0.35f + impact / 14f, 0.35f, 1f));
                stepTimer = 0.25f;
            }
            else if (!nowGrounded && grounded && v.y > 2f)   // take-off
            {
                Puff(pos, 4, 0.35f, Vector3.zero);
                Sound("jump", pos, 0.5f);
            }

            if (nowGrounded)
            {
                airTime = 0f;
                // footsteps: faster steps when running, quieter when walking / crouching
                if (hSpeed > 0.5f)
                {
                    stepTimer -= dt;
                    if (stepTimer <= 0f)
                    {
                        stepTimer = Mathf.Clamp(0.62f - hSpeed * 0.09f, 0.26f, 0.56f);
                        Sound("step", pos, Mathf.Clamp(0.18f + hSpeed * 0.09f, 0.18f, 0.55f));
                        if (hSpeed > 0.8f && hSpeed <= RunSpeed) Puff(pos - new Vector3(v.x, 0f, v.z).normalized * 0.08f, 1, 0f, -v * 0.08f);
                    }
                }
                else stepTimer = 0.12f;
                if (hSpeed > RunSpeed)
                {
                    strideTimer -= dt * Mathf.Clamp(hSpeed / 4f, 0.7f, 1.3f);
                    if (strideTimer <= 0f)
                    {
                        strideTimer = StrideTime;
                        side = !side;
                        var right = transform.right * (side ? 0.09f : -0.09f);
                        float k = Mathf.InverseLerp(RunSpeed, 4.5f, hSpeed);
                        var back = new Vector3(v.x, 0f, v.z).normalized;
                        Puff(pos + right - back * 0.12f, 3, 0.5f + k * 0.5f, -v * 0.15f);
                        Trail(pos - back * 0.3f, v);
                    }
                }
                else strideTimer = 0.05f;
            }
            else airTime += dt;

            grounded = nowGrounded;
            lastVy = v.y;
        }

        void Sound(string name, Vector3 pos, float volume)
        {
            bool own = CameraFx.IsLocalView(this);
            GameAudio.Play(name, pos, volume * (own ? 0.8f : 1f), 1.5f, 28f, 1f, 0.1f, own ? 0.5f : 1f);
        }

        bool OnGround(Vector3 pos)
        {
            int n = Physics.RaycastNonAlloc(pos + Vector3.up * 0.3f, Vector3.down, hits, 0.65f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!hits[i].transform.IsChildOf(transform)) return true;
            return false;
        }

        // ------------------------------------------------------------------ particles
        static bool Ready()
        {
            if (dust != null) return true;
            if (tried) return false;
            tried = true;
            var mat = Resources.Load<Material>("FX/MuzzleSmoke");
            if (mat == null) return false;
            var go = new GameObject("FootDust");
            dust = go.AddComponent<ParticleSystem>();
            dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = dust.main;
            main.loop = true; main.playOnAwake = false; main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.maxParticles = 600; main.gravityModifier = -0.02f;
            var em = dust.emission; em.enabled = false;
            var shape = dust.shape; shape.enabled = false;
            var size = dust.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f, 0f, 4f), new Keyframe(1f, 2.2f, 0f, 0f)));
            var col = dust.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var limit = dust.limitVelocityOverLifetime; limit.enabled = true; limit.drag = 3f; limit.limit = 100f;
            limit.multiplyDragByParticleSize = false; limit.multiplyDragByParticleVelocity = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat; r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            dust.Play();
            return true;
        }

        static readonly Color DustColor = new Color(0.78f, 0.7f, 0.56f);

        static void Emit(Vector3 p, Vector3 vel, float size, float life, float alpha)
        {
            var c = DustColor * Random.Range(0.92f, 1.05f); c.a = alpha;
            dust.Emit(new ParticleSystem.EmitParams
            {
                position = p, velocity = vel, startSize = size, startLifetime = life, startColor = c,
                rotation = Random.Range(0f, 360f), applyShapeToPosition = false,
            }, 1);
        }

        // strength 0 = a walking step, 1 = full sprint
        static void Puff(Vector3 feet, int count, float strength, Vector3 push)
        {
            if (!Ready()) return;
            for (int i = 0; i < count; i++)
            {
                var d = Random.insideUnitSphere; d.y = Mathf.Abs(d.y) * 0.6f;
                Emit(feet + Vector3.up * 0.06f + d * 0.1f, push + d * 0.6f + Vector3.up * Random.Range(0.25f, 0.5f),
                     Random.Range(0.32f, 0.5f) * (0.75f + strength * 0.7f), Random.Range(0.8f, 1.3f) * (0.8f + strength * 0.4f), 0.3f + 0.25f * strength);
            }
        }

        // a big, slow, lingering cloud left behind a sprinting cat
        static void Trail(Vector3 feet, Vector3 move)
        {
            if (!Ready()) return;
            Emit(feet + Vector3.up * 0.15f + Random.insideUnitSphere * 0.1f, -move * 0.05f + Vector3.up * 0.15f,
                 Random.Range(0.6f, 0.85f), Random.Range(1.4f, 2f), 0.28f);
        }

        static void Ring(Vector3 feet, float strength, Vector3 move)
        {
            if (!Ready()) return;
            int count = Mathf.RoundToInt(Mathf.Lerp(8f, 20f, strength));
            for (int i = 0; i < count; i++)
            {
                float a = (i + Random.value * 0.5f) / count * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Emit(feet + dir * 0.15f + Vector3.up * 0.06f, dir * Mathf.Lerp(1.8f, 4.2f, strength) + Vector3.up * 0.35f + move * 0.1f,
                     Random.Range(0.45f, 0.7f) * (1f + strength), Random.Range(0.9f, 1.4f), 0.45f + 0.2f * strength);
            }
        }
    }
}
