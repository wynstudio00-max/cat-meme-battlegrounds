using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// How the storm feels for the local player (client only, auto-created in scenes with a NetworkBootstrap):
    ///  * outside the safe zone the screen turns purple and desaturated with chromatic aberration and a dark purple vignette
    ///    (a runtime post-processing Volume whose weight follows "how long have I been outside");
    ///  * an electric hum that grows near the wall and inside the storm, a zap + small shake on every storm damage tick;
    ///  * electric sparks crawling up the wall near the player.
    /// </summary>
    public class ZoneFx : MonoBehaviour
    {
        static ZoneFx instance;
        Volume volume;
        AudioSource hum;
        ParticleSystem sparks;
        Health watched;
        float outsideWeight;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            instance = new GameObject("ZoneFx").AddComponent<ZoneFx>();
        }

        void Awake()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var color = profile.Add<ColorAdjustments>(true);
            color.colorFilter.Override(new Color(0.82f, 0.68f, 1.15f));
            color.saturation.Override(-45f);
            color.postExposure.Override(-0.15f);
            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.55f);
            var vig = profile.Add<Vignette>(true);
            vig.color.Override(new Color(0.25f, 0.05f, 0.4f));
            vig.intensity.Override(0.45f);
            vig.smoothness.Override(0.6f);
            volume = gameObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 20f;
            volume.weight = 0f;
            volume.sharedProfile = profile;

            var spark = Resources.Load<Material>("FX/MuzzleSpark");
            if (spark != null)
            {
                var mat = new Material(spark); mat.SetColor("_BaseColor", new Color(2.2f, 1.4f, 4f));
                var go = new GameObject("StormSparks"); go.transform.SetParent(transform, false);
                sparks = go.AddComponent<ParticleSystem>();
                sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = sparks.main;
                main.loop = true; main.playOnAwake = false; main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startSpeed = 0f; main.maxParticles = 300;
                var em = sparks.emission; em.enabled = false;
                var shape = sparks.shape; shape.enabled = false;
                var col = sparks.colorOverLifetime; col.enabled = true;
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                          new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = mat; r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.04f; r.lengthScale = 2f;
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                sparks.Play();
            }
        }

        void OnDestroy()
        {
            if (watched != null) watched.Damaged -= OnDamaged;
            if (hum != null) Destroy(hum.gameObject);
            if (instance == this) instance = null;
        }

        void Update()
        {
            var match = MatchManager.Instance;
            var zone = match != null ? match.Zone : null;
            var nm = NetworkManager.Singleton;
            var me = nm != null && nm.IsClient && nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject : null;
            bool on = zone != null && zone.IsSpawned && zone.Active && me != null;
            var health = me != null ? me.GetComponent<Health>() : null;
            if (health != watched)
            {
                if (watched != null) watched.Damaged -= OnDamaged;
                watched = health;
                if (watched != null) watched.Damaged += OnDamaged;
            }

            bool alive = health != null && !health.IsDead;
            bool outside = on && alive && zone.IsOutside(me.transform.position);
            outsideWeight = Mathf.MoveTowards(outsideWeight, outside ? 1f : 0f, Time.deltaTime * (outside ? 1.8f : 1.2f));
            volume.weight = outsideWeight;

            // distance to the wall (positive = inside the safe zone)
            float toWall = 999f;
            Vector3 wallPoint = Vector3.zero, outward = Vector3.forward;
            if (on)
            {
                var flat = me.transform.position - zone.ShownCenter; flat.y = 0f;
                float dist = flat.magnitude;
                outward = dist > 0.01f ? flat / dist : Vector3.forward;
                toWall = zone.ShownRadius - dist;
                wallPoint = zone.ShownCenter + outward * zone.ShownRadius;
            }

            // electric hum: near the wall and while inside the storm
            float near = on && alive ? Mathf.Clamp01(1f - Mathf.Abs(toWall) / 18f) : 0f;
            float humVol = Mathf.Max(outsideWeight * 0.55f, near * 0.35f);
            if (humVol > 0.01f && hum == null) hum = GameAudio.Loop("zone_loop", null, 0f);
            if (hum != null) hum.volume = Mathf.MoveTowards(hum.volume, humVol, Time.deltaTime * 0.8f);

            // sparks crawling up the wall around the closest wall point
            if (sparks != null && on && Mathf.Abs(toWall) < 30f)
            {
                int n = Mathf.FloorToInt(Time.deltaTime * 26f + Random.value);
                var side = Vector3.Cross(Vector3.up, outward);
                for (int i = 0; i < n; i++)
                {
                    float along = Random.Range(-16f, 16f);
                    // stay on the circle: rotate the outward direction by the arc offset
                    float ang = along / Mathf.Max(zone.ShownRadius, 1f);
                    var dir = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, Vector3.up) * outward;
                    var p = zone.ShownCenter + dir * zone.ShownRadius;
                    p.y = me.transform.position.y + Random.Range(-1f, 10f);
                    sparks.Emit(new ParticleSystem.EmitParams
                    {
                        position = p, velocity = Vector3.up * Random.Range(3f, 8f) + side * Random.Range(-1.5f, 1.5f),
                        startSize = Random.Range(0.04f, 0.09f), startLifetime = Random.Range(0.25f, 0.6f), startColor = Color.white, applyShapeToPosition = false,
                    }, 1);
                }
            }
        }

        void OnDamaged(float amount, GameObject attacker)
        {
            if (attacker != null || outsideWeight < 0.05f) return;   // only storm ticks (no attacker)
            GameAudio.Play2D("zone_zap", 0.6f, 1f, 0.08f);
            CameraFx.Shake(0.22f);
        }
    }
}
