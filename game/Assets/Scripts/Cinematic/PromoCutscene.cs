using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// 44-second cinematic promo, played and recorded in the arena (scene built by CatBattleground > Cinematic > Build Promo
    /// Cutscene Scene). Everything runs offline from code: cats are the plain character prefabs driven straight through the
    /// Animator, the drop plane / parachute / storm wall are the game's own models and materials, gun FX are MuzzleFlash / KillFx.
    ///
    /// Story: island reveal with the drop plane -> the Army cat jumps, free falls, opens the chute and lands -> reload close-up
    /// -> Knight (shotgun) and Ninja (sniper) attack -> firefight montage with a sniper bullet-time near miss -> slow-motion
    /// elimination of the Knight -> long-range kill on the Ninja -> the storm wall chases the hero -> lineup of all cats -> crane up
    /// for the title (titles, letterbox, music and SFX are added afterwards by Tools/Cinematic/make_promo.py).
    ///
    /// Recording: with <see cref="record"/> on, time is locked to <see cref="fps"/> (Time.captureFramerate) and every frame of
    /// the camera's render texture is written as JPG to &lt;project&gt;/Recordings/Promo_&lt;date&gt;/ together with cues.json (sound
    /// events with their film time). Play mode stops by itself at the end.
    /// Film time (cuts, camera) is unscaled; game time (cats, particles, animation) runs through Time.timeScale for slow motion.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class PromoCutscene : MonoBehaviour
    {
        [Serializable]
        public class CastMember
        {
            public string id;                              // catalog id: army, knight, ninja ...
            public GameObject model;                       // Assets/Prefabs/Characters/Cat_*.prefab
            public RuntimeAnimatorController controller;   // CatPlayer(_Cat_*) controller
        }

        [SerializeField] public CastMember[] cast = new CastMember[0];
        [Tooltip("Write every frame to <project>/Recordings. Off = just watch it in the Game view.")]
        [SerializeField] public bool record = true;
        [SerializeField] public int width = 1920, height = 1080, fps = 30;
        [Range(50, 100)] [SerializeField] public int jpgQuality = 95;
        [Tooltip("Start the film here (seconds) to preview a single shot; recordings always start at 0.")]
        [SerializeField] public float previewStart = 0f;

        public const float Duration = 44f;
        const float WalkSpeed = 1.4f;        // blend tree unit (PlayerController.walkSpeed)
        const float Sprint = 4f;
        const float PlaneHeight = 110f, PlaneSpeed = 42f, JumpAt = 5.8f;

        // ------------------------------------------------------------------ state
        Camera cam;
        RenderTexture rt;
        Texture2D readback;
        Volume volume;
        DepthOfField dof;
        string outDir;
        int frame;
        float film, game;
        bool finished;
        readonly StringBuilder cues = new StringBuilder();
        int cueCount;

        Vector3 S, F, R, O, planeDir;       // stage point, stage forward / right, outward direction, plane heading
        MeshCollider terrain;
        float stageY;

        Actor hero, knight, ninja;
        readonly List<Actor> lineup = new List<Actor>();
        readonly Dictionary<string, Actor> actors = new Dictionary<string, Actor>();

        Transform plane;
        readonly List<Transform> props = new List<Transform>();
        GameObject chute; Transform canopy; float chuteOpenedAt = -1f, chuteCollapseAt = -1f; Vector3 canopyScale = Vector3.one;
        GameObject zoneWall;
        ParticleSystem windStreaks;
        float focusDistance = 10f, focalLength = 50f, aperture = 8f;

        readonly List<(float t, Action a)> events = new List<(float, Action)>();
        int nextEvent;
        readonly List<(float g, Action a)> pending = new List<(float, Action)>();   // game-time callbacks (bullet arrival)

        // ================================================================== setup
        void Start()
        {
            cam = Camera.main;
            if (cam == null) { cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>(); cam.tag = "MainCamera"; }
            var tpc = cam.GetComponent<ThirdPersonCamera>(); if (tpc != null) tpc.enabled = false;
            cam.nearClipPlane = 0.08f; cam.farClipPlane = 900f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;

            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "PromoRT" };
            rt.Create();
            cam.targetTexture = rt;
            readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            BuildVolume();
            FindStage();
            BuildCast();
            if (!enabled) return;
            BuildPlane();
            BuildWind();
            BuildTimeline();

            if (record)
            {
                previewStart = 0f;
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
                var args = Environment.GetCommandLineArgs();   // standalone recorder: -promoOut <folder>
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-promoOut") root = args[i + 1];
                outDir = Path.Combine(root, "Promo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(outDir);
                Time.captureFramerate = fps;
                AudioListener.volume = 0f;   // the soundtrack is mixed afterwards from cues.json (real-time audio would drift)
                Debug.Log("[Promo] recording " + width + "x" + height + " @" + fps + " to " + outDir);
                StartCoroutine(Capture());
            }
            film = Mathf.Clamp(previewStart, 0f, Duration);
            // fast-forward the events that already happened when previewing from the middle
            while (nextEvent < events.Count && events[nextEvent].t <= film) { events[nextEvent].a(); nextEvent++; }
        }

        void OnDestroy()
        {
            Time.captureFramerate = 0;
            Time.timeScale = 1f;
            AudioListener.volume = 1f;
            if (cam != null) cam.targetTexture = null;
            if (rt != null) rt.Release();
        }

        // ------------------------------------------------------------------ stage: an open, flat meadow next to the village
        void FindStage()
        {
            var t = GameObject.Find("Arena/Terrain");
            if (t != null) terrain = t.GetComponent<MeshCollider>();

            var houses = new List<Vector3>();
            var buildings = new List<Vector3>();
            var b = GameObject.Find("Arena/Buildings");
            if (b != null)
                foreach (Transform c in b.transform)
                {
                    buildings.Add(c.position);
                    if (c.name.StartsWith("house")) houses.Add(c.position);
                }
            Vector3 village = Vector3.zero;
            if (houses.Count > 0) { foreach (var h in houses) village += h; village /= houses.Count; }
            village.y = 0f;

            float best = float.MaxValue; Vector3 bestP = village; Vector3 bestF = Vector3.forward;
            for (float r = 18f; r <= 48f; r += 3f)
                for (float a = 0f; a < 360f; a += 10f)
                {
                    var p = village + new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad)) * r;
                    if (new Vector2(p.x, p.z).magnitude > 95f) continue;
                    float h0 = Ground(p);
                    if (h0 < 1.2f) continue;                                   // beach / water
                    var fwd = (p - village); fwd.y = 0f; fwd.Normalize();      // the hero faces away from the village (houses behind him)
                    float lo = h0, hi = h0;
                    for (int k = 0; k < 12; k++)
                    {
                        float ka = k * 30f * Mathf.Deg2Rad;
                        var q = p + new Vector3(Mathf.Sin(ka), 0f, Mathf.Cos(ka)) * 14f;
                        float hq = Ground(q); lo = Mathf.Min(lo, hq); hi = Mathf.Max(hi, hq);
                        if (hq < 0.8f) { hi = 999f; break; }
                    }
                    for (int k = 1; k <= 3; k++)                              // the battle field in front (enemies at 12-28 m)
                    {
                        float hq = Ground(p + fwd * (k * 9f));
                        if (hq < 0.8f) { hi = 999f; break; }
                        lo = Mathf.Min(lo, hq); hi = Mathf.Max(hi, hq);
                    }
                    float nearestBuilding = 999f;
                    foreach (var bp in buildings) nearestBuilding = Mathf.Min(nearestBuilding, Vector2.Distance(new Vector2(bp.x, bp.z), new Vector2(p.x, p.z)));
                    float nearestAhead = 999f;
                    foreach (var bp in buildings)
                        for (int k = 1; k <= 3; k++)
                            nearestAhead = Mathf.Min(nearestAhead, Vector2.Distance(new Vector2(bp.x, bp.z), new Vector2(p.x + fwd.x * k * 9f, p.z + fwd.z * k * 9f)));
                    if (nearestBuilding < 15f || nearestAhead < 9f) continue;
                    float score = (hi - lo) * 3f + Mathf.Abs(r - 26f) * 0.15f + Mathf.Max(0f, nearestBuilding - 30f) * 0.2f;
                    if (score < best) { best = score; bestP = p; bestF = fwd; }
                }
            S = bestP; S.y = Ground(S); stageY = S.y;
            F = bestF; R = Vector3.Cross(Vector3.up, F).normalized;
            O = new Vector3(S.x, 0f, S.z); O = O.sqrMagnitude > 1f ? O.normalized : -F;
            planeDir = Vector3.Cross(Vector3.up, O).normalized;
            Debug.Log($"[Promo] stage {S} forward {F} (score {best:0.00})");

            // a clean meadow for the fight: trees, rocks and bushes near the action go away (grass tufts stay)
            ClearAround(S, 21f);
            ClearAround(W(-9f, 27f), 6f);
            ClearAround(W(0f, 13f), 10f);
        }

        void ClearAround(Vector3 c, float radius)
        {
            var arena = GameObject.Find("Arena");
            if (arena == null) return;
            foreach (Transform group in arena.transform)
            {
                string n = group.name;
                if (n == "Terrain" || n == "Sea" || n == "Buildings" || n == "Boundary" || n == "SpawnPoints" || n == "LootPoints") continue;
                foreach (var r in group.GetComponentsInChildren<Renderer>())
                {
                    var bc = r.bounds.center;
                    if (Vector2.Distance(new Vector2(bc.x, bc.z), new Vector2(c.x, c.z)) > radius) continue;
                    if (r.bounds.size.y < 0.55f) continue;   // grass, flowers
                    r.gameObject.SetActive(false);
                }
                foreach (var col in group.GetComponentsInChildren<Collider>())
                {
                    var bc = col.bounds.center;
                    if (Vector2.Distance(new Vector2(bc.x, bc.z), new Vector2(c.x, c.z)) <= radius) col.enabled = false;
                }
            }
        }

        float Ground(Vector3 p)
        {
            var ray = new Ray(new Vector3(p.x, 400f, p.z), Vector3.down);
            if (terrain != null && terrain.Raycast(ray, out var hit, 800f)) return hit.point.y;
            if (Physics.Raycast(ray, out hit, 800f, ~0, QueryTriggerInteraction.Ignore)) return hit.point.y;
            return 0f;
        }

        /// <summary>Stage space: x right, z forward (metres), h above the ground.</summary>
        Vector3 W(float x, float z, float h = 0f)
        {
            var p = S + R * x + F * z;
            p.y = Ground(p) + h;
            return p;
        }

        // ------------------------------------------------------------------ cast
        void BuildCast()
        {
            foreach (var m in cast)
            {
                if (m == null || m.model == null || string.IsNullOrEmpty(m.id)) continue;
                var a = new Actor(this, m);
                actors[m.id] = a;
            }
            actors.TryGetValue("army", out hero);
            actors.TryGetValue("knight", out knight);
            actors.TryGetValue("ninja", out ninja);
            if (hero == null) { foreach (var a in actors.Values) { hero = a; break; } }
            if (hero == null) { Debug.LogError("[Promo] no cast assigned (run CatBattleground > Cinematic > Build Promo Cutscene Scene)"); enabled = false; return; }
            if (knight == null) knight = hero;
            if (ninja == null) ninja = hero;
            hero.weapon = 0; knight.weapon = 5; ninja.weapon = 3;

            // lineup order: hero in the middle, the others fanned out behind him
            string[] order = { "army", "black", "gold", "knight", "ninja", "soldier", "white", "baby", "nobi" };
            foreach (var id in order) if (actors.TryGetValue(id, out var a) && !lineup.Contains(a)) lineup.Add(a);
            foreach (var a in actors.Values) if (!lineup.Contains(a)) lineup.Add(a);
            foreach (var a in actors.Values) a.Show(false);
        }

        // ------------------------------------------------------------------ props
        void BuildPlane()
        {
            var root = new GameObject("PromoPlane").transform;
            plane = root;
            var model = Resources.Load<GameObject>("Vehicles/DropPlane");
            if (model != null)
            {
                var p = Instantiate(model, root);
                foreach (var c in p.GetComponentsInChildren<Collider>()) Destroy(c);
                var b = BoundsOf(p);
                p.transform.localScale *= DropShipVisual.PlaneLength / Mathf.Max(0.01f, b.size.z);
                b = BoundsOf(p);
                p.transform.position -= b.center - root.position;
                foreach (var t in p.GetComponentsInChildren<Transform>())
                    if (t.name.ToUpperInvariant().StartsWith("BALING")) props.Add(t);
            }
            else
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.transform.SetParent(root, false);
                body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                body.transform.localScale = new Vector3(3.5f, 12f, 3.5f);
                Destroy(body.GetComponent<Collider>());
            }
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.one);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        Vector3 PlanePos(float f) => new Vector3(S.x, PlaneHeight, S.z) + planeDir * (PlaneSpeed * (f - JumpAt)) - planeDir * 6f;

        void UpdatePlane()
        {
            bool show = film < 8.5f;
            if (plane.gameObject.activeSelf != show) plane.gameObject.SetActive(show);
            if (!show) return;
            float t = film;
            plane.SetPositionAndRotation(PlanePos(film) + Vector3.up * Mathf.Sin(t * 0.9f) * 0.4f,
                Quaternion.LookRotation(planeDir, Vector3.up) * Quaternion.Euler(Mathf.Sin(t * 0.7f) * 1.2f, 0f, Mathf.Sin(t * 0.5f) * 2.5f));
            var fwd = plane.forward;
            foreach (var p in props) p.RotateAround(p.position, fwd, 1500f * Time.unscaledDeltaTime);
        }

        void BuildChute()
        {
            chute = new GameObject("PromoParachute");
            chute.transform.SetParent(hero.go.transform, false);
            var pivot = new GameObject("Canopy").transform;
            pivot.SetParent(chute.transform, false);
            pivot.localPosition = new Vector3(0f, 1.55f, 0f);
            var model = Resources.Load<GameObject>("Vehicles/Parachute_CMBG");
            if (model != null)
            {
                var m = Instantiate(model, pivot, false);
                foreach (var c in m.GetComponentsInChildren<Collider>()) Destroy(c);
                foreach (var r in m.GetComponentsInChildren<Renderer>()) foreach (var cm in r.materials) cm.SetFloat("_Cull", 0f);
                canopyScale = Vector3.one * 1.85f;
            }
            else
            {
                var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dome.transform.SetParent(pivot, false);
                dome.transform.localPosition = new Vector3(0f, 1.8f, 0f);
                dome.transform.localScale = new Vector3(3.2f, 1.5f, 3.2f);
                Destroy(dome.GetComponent<Collider>());
                canopyScale = Vector3.one;
            }
            pivot.localScale = canopyScale;
            canopy = pivot;
            chute.SetActive(false);
        }

        void UpdateChute()
        {
            if (chute == null || !chute.activeSelf) return;
            float t = game - chuteOpenedAt;
            float open = t < 0.14f ? Mathf.Lerp(0.15f, 1.18f, t / 0.14f) : 1f + 0.18f * Mathf.Exp(-(t - 0.14f) * 5.5f) * Mathf.Cos((t - 0.14f) * 17f);
            float collapse = 1f;
            if (chuteCollapseAt > 0f)
            {
                float c = Mathf.Clamp01((game - chuteCollapseAt) / 0.7f);
                collapse = 1f - c * c;
                chute.transform.localRotation = Quaternion.Euler(-70f * c, 0f, 0f);
                chute.transform.localPosition = -F * 0f + Vector3.down * (1.2f * c);
                if (c >= 1f) { chute.SetActive(false); return; }
            }
            chute.transform.localScale = new Vector3(open, Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(t / 0.25f)) * open * collapse, open);
            float b = Mathf.Sin(game * 2.6f) * 0.035f;
            canopy.localScale = Vector3.Scale(canopyScale, new Vector3(1f - b * 0.5f, 1f + b, 1f - b * 0.5f));
            canopy.localRotation = Quaternion.Euler(Mathf.Sin(game * 1.3f) * 3f, 0f, Mathf.Sin(game * 1.7f + 1f) * 4f);
        }

        void BuildZoneWall()
        {
            zoneWall = new GameObject("PromoZoneWall");
            const int seg = 128;
            var v = new Vector3[(seg + 1) * 2]; var tri = new int[seg * 6]; var uv = new Vector2[v.Length];
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.5f;
                v[i * 2] = d; v[i * 2 + 1] = d + Vector3.up;
                uv[i * 2] = new Vector2(i / (float)seg, 0f); uv[i * 2 + 1] = new Vector2(i / (float)seg, 1f);
            }
            for (int i = 0; i < seg; i++)
            {
                int k = i * 6, a = i * 2;
                tri[k] = a; tri[k + 1] = a + 1; tri[k + 2] = a + 2;
                tri[k + 3] = a + 2; tri[k + 4] = a + 1; tri[k + 5] = a + 3;
            }
            var mesh = new Mesh { name = "PromoZoneWall", vertices = v, triangles = tri, uv = uv };
            mesh.RecalculateBounds(); mesh.bounds = new Bounds(Vector3.up * 0.5f, new Vector3(1f, 1f, 1f));
            zoneWall.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = zoneWall.AddComponent<MeshRenderer>();
            var mat = Resources.Load<Material>("FX/ZoneWall");
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")); mat.color = new Color(0.7f, 0.3f, 1f, 0.5f); }
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        }

        void UpdateZoneWall()
        {
            if (zoneWall == null) return;
            // centre far ahead of the hero, wall behind him, closing in at 6 m/s
            var c = S + F * 70f;
            float radius = 88f - 6f * Mathf.Max(0f, film - 29.2f);
            zoneWall.transform.position = new Vector3(c.x, stageY - 12f, c.z);
            zoneWall.transform.localScale = new Vector3(radius * 2f, 70f, radius * 2f);
        }

        void BuildWind()
        {
            var src = Resources.Load<Material>("FX/MuzzleSpark");
            var go = new GameObject("PromoWindStreaks");
            windStreaks = go.AddComponent<ParticleSystem>();
            windStreaks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = windStreaks.main;
            main.playOnAwake = false; main.loop = true; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.maxParticles = 600; main.gravityModifier = 0f;
            var em = windStreaks.emission; em.enabled = false;
            var shape = windStreaks.shape; shape.enabled = false;
            var col = windStreaks.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            if (src != null) { var m = new Material(src); m.SetColor("_BaseColor", new Color(1.2f, 1.25f, 1.35f, 0.5f)); r.sharedMaterial = m; }
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.06f; r.lengthScale = 2f;
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            windStreaks.Play();
        }

        void EmitWind(float rate)
        {
            if (windStreaks == null || rate <= 0f) return;
            int n = Mathf.RoundToInt(rate * Time.unscaledDeltaTime + UnityEngine.Random.value);
            var c = cam.transform.position;
            for (int i = 0; i < n; i++)
            {
                var p = c + UnityEngine.Random.insideUnitSphere * 7f + Vector3.down * UnityEngine.Random.Range(4f, 16f);
                windStreaks.Emit(new ParticleSystem.EmitParams
                {
                    position = p, velocity = Vector3.up * UnityEngine.Random.Range(6f, 12f),
                    startSize = UnityEngine.Random.Range(0.012f, 0.03f), startLifetime = UnityEngine.Random.Range(0.35f, 0.7f),
                    startColor = Color.white, applyShapeToPosition = false,
                }, 1);
            }
        }

        // ------------------------------------------------------------------ post: depth of field, motion blur, grain
        void BuildVolume()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            dof = profile.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(10f);
            dof.focalLength.Override(50f);
            dof.aperture.Override(8f);
            var mb = profile.Add<MotionBlur>(true);
            mb.intensity.Override(0.35f);
            mb.quality.Override(MotionBlurQuality.High);
            var grain = profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(0.18f);
            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.12f);
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(0.3f);
            vig.smoothness.Override(0.5f);
            var go = new GameObject("PromoPostFX");
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;
            volume.sharedProfile = profile;
        }

        // ================================================================== timeline
        void At(float t, Action a) => events.Add((t, a));

        void BuildTimeline()
        {
            // --- ambience / vehicle loops for the soundtrack
            At(0f, () => Cue("plane_loop", 0.9f, 1f, 6.6f));
            At(6.6f, () => Cue("wind_freefall_loop", 1f, 1f, 2.6f));
            At(9.2f, () => Cue("wind_chute_loop", 0.8f, 1f, 2.4f));
            At(11.0f, () => Cue("ambient_loop", 0.6f, 1f, Duration - 11f));
            // --- B: the jump
            At(JumpAt, () =>
            {
                hero.Show(true);
                var exit = plane.TransformPoint(DropShipVisual.ExitOffset);
                hero.Teleport(exit, Quaternion.LookRotation(planeDir).eulerAngles.y);
                hero.airborne = true;
                hero.airVel = planeDir * (PlaneSpeed * 0.6f) + Vector3.down * 1.5f;
                hero.anim.SetBool("Grounded", false);
                hero.anim.SetBool("Skydive", true);
                Cue("banner_whoosh", 0.7f);
            });
            // --- C: parachute
            At(9.2f, () =>
            {
                BuildChute();
                chute.SetActive(true); chuteOpenedAt = game;
                hero.anim.SetBool("Parachute", true);
                CameraFx.Shake(0.5f);
                Cue("parachute_open", 1f);
            });
            At(11.0f, () =>
            {
                hero.Teleport(W(0f, 0f, 5.5f), Yaw(F));
                hero.airVel = Vector3.down * 4.2f;
            });
            // --- D: reload close-up
            At(13.65f, () => { hero.anim.SetFloat("ReloadSpeed", 1f); hero.anim.SetTrigger("Reload"); CueReload(1.8f); });
            // --- E: the Knight charges in
            At(14.0f, () =>
            {
                knight.Show(true);
                knight.Teleport(W(14f, 23f), Yaw(-F));
                knight.MoveTo(W(6.5f, 12.5f), Sprint);
            });
            At(14.8f, () => { ninja.Show(true); ninja.Teleport(W(-9f, 27f), Yaw(hero.Pos - W(-9f, 27f))); ninja.anim.SetBool("Crouching", true); ninja.face = hero; });
            At(15.6f, () => { hero.face = knight; });
            At(17.25f, () => { knight.face = hero; });
            // --- F1: hero opens fire, the Knight side-steps
            At(18.05f, () => { hero.StartFire(knight, 19.25f, 9f, 0f); });
            At(18.1f, () => { knight.MoveDir(-Right(knight), 2.6f, 19.25f); });
            // --- F2: shotgun blasts
            At(19.55f, () => { Shoot(knight, hero, false); CameraFx.Shake(0.35f); });
            At(20.15f, () => { Shoot(knight, hero, false); CameraFx.Shake(0.35f); });
            // --- F3: hero sprints sideways while firing
            At(20.4f, () => { hero.MoveDir(Right(hero), 3.6f, 21.45f); hero.StartFire(knight, 21.45f, 9f, 0.15f); });
            // --- F4: sniper bullet time
            At(21.82f, () =>
            {
                var head = hero.Pos + Vector3.up * 1.95f + Right(hero) * 0.25f;
                Shoot(ninja, head, false, true);
                CameraFx.Shake(0.25f);
            });
            // --- F5 + G: the Knight goes down in slow motion
            At(23.42f, () => { hero.face = knight; hero.StartFire(knight, 24.25f, 9f, 1f); });
            At(24.3f, () =>
            {
                Shoot(hero, knight, true);
                Delay(0.09f, () => { knight.Die(true); KillFx.Play(knight.Chest); CameraFx.Shake(0.5f); Cue("kill_poof", 1f); Cue("hitmarker_kill", 0.8f); });
            });
            // --- H: long-range shot on the Ninja
            At(27.0f, () => { hero.face = ninja; });
            At(27.8f, () => Shoot(hero, ninja, false));
            At(28.02f, () => Shoot(hero, ninja, true));
            At(28.24f, () =>
            {
                Shoot(hero, ninja, true);
                Delay(0.2f, () => { ninja.Die(true); KillFx.Play(ninja.Chest); Cue("kill_poof", 0.6f); Cue("hitmarker_kill", 0.8f); });
            });
            // --- I: the storm
            At(29.2f, () =>
            {
                knight.Show(false); ninja.Show(false);
                hero.face = null; hero.firing = false;
                hero.Teleport(W(0f, -4f), Yaw(F));
                hero.MoveDir(F, Sprint, 33.1f);
                BuildZoneWall();
                Cue("zone_loop", 0.9f, 1f, 3.8f);
                Cue("announce_alarm", 0.5f);
            });
            // --- J: lineup
            At(33.0f, () =>
            {
                if (zoneWall != null) Destroy(zoneWall);
                zoneWall = null;
                for (int i = 0; i < lineup.Count; i++)
                {
                    var a = lineup[i];
                    a.Show(true);
                    a.Revive();
                    a.firing = false; a.face = null; a.StopMoving();
                    a.anim.SetBool("Crouching", false);
                    var o = LineupOffset(i);
                    a.Teleport(W(o.x, 12f + o.y), Yaw(F));
                }
            });
            At(33.5f, () => TriggerIf("soldier", "Reload"));
            At(34.1f, () => TriggerIf("white", "Reload"));
            At(34.6f, () => { if (actors.TryGetValue("baby", out var b)) b.Hop(1.0f); });
            At(35.2f, () => TriggerIf("gold", "Interact"));
            events.Sort((a, b) => a.t.CompareTo(b.t));
        }

        Vector2 LineupOffset(int i)
        {
            if (i == 0) return Vector2.zero;
            int pair = (i + 1) / 2; float side = (i % 2 == 1) ? -1f : 1f;
            return new Vector2(side * 1.55f * pair, -0.85f * pair);
        }

        void TriggerIf(string id, string trigger)
        {
            if (!actors.TryGetValue(id, out var a)) return;
            if (trigger == "Reload") { a.anim.SetFloat("ReloadSpeed", 1f); CueReload(1.8f, 0.5f); }
            a.anim.SetTrigger(trigger);
        }

        void Delay(float gameSeconds, Action a) => pending.Add((game + gameSeconds, a));

        static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        static Vector3 Right(Actor a) => a.go.transform.right;

        // slow motion: sniper bullet time and the elimination
        float TimeScaleAt(float f)
        {
            float s = 1f;
            s = Mathf.Min(s, Ramp(f, 21.86f, 21.96f, 22.95f, 23.35f, 0.12f));
            s = Mathf.Min(s, Ramp(f, 24.3f, 24.38f, 25.9f, 26.45f, 0.17f));
            return s;
        }

        static float Ramp(float f, float a, float b, float c, float d, float low)
        {
            if (f <= a || f >= d) return 1f;
            if (f < b) return Mathf.Lerp(1f, low, Smooth((f - a) / (b - a)));
            if (f <= c) return low;
            return Mathf.Lerp(low, 1f, Smooth((f - c) / (d - c)));
        }

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        // ================================================================== frame loop
        void Update()
        {
            if (finished) return;
            if (record) film = frame / (float)fps;
            else film += Time.unscaledDeltaTime;
            if (!record && film >= Duration) film = Duration;

            Time.timeScale = TimeScaleAt(film);
            float dt = Time.deltaTime;
            game += dt;

            while (nextEvent < events.Count && events[nextEvent].t <= film) { events[nextEvent].a(); nextEvent++; }
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].g <= game) { var a = pending[i].a; pending.RemoveAt(i); a(); }

            // hero in the air: free fall, then the canopy
            if (hero.airborne)
            {
                bool chuteOpen = chute != null && chute.activeSelf && chuteCollapseAt < 0f;
                var v = hero.airVel;
                var flat = new Vector3(v.x, 0f, v.z);
                var toStage = new Vector3(S.x - hero.Pos.x, 0f, S.z - hero.Pos.z);
                var want = toStage.sqrMagnitude > 0.01f ? toStage.normalized * Mathf.Min(toStage.magnitude * 0.6f, chuteOpen ? 5f : 14f) : Vector3.zero;
                flat = Vector3.Lerp(flat, want, 1f - Mathf.Exp(-(chuteOpen ? 1.5f : 0.6f) * dt));
                float vy = chuteOpen ? Mathf.Lerp(v.y, -4.2f, 1f - Mathf.Exp(-4f * dt)) : Mathf.Max(v.y - 9.8f * dt, -30f);
                hero.airVel = new Vector3(flat.x, vy, flat.z);
                var p = hero.Pos + hero.airVel * dt;
                float gy = Ground(p);
                if (p.y <= gy && film > 11f)
                {
                    p.y = gy;
                    hero.airborne = false;
                    hero.anim.SetBool("Grounded", true);
                    chuteCollapseAt = game + 0.12f;
                    CameraFx.Shake(0.3f);
                    Cue("land", 1f);
                    Delay(0.3f, () => { hero.anim.SetBool("Parachute", false); hero.anim.SetBool("Skydive", false); });
                }
                hero.go.transform.position = p;
                var face = new Vector3(film < 10.5f ? flat.x : F.x, 0f, film < 10.5f ? flat.z : F.z);
                if (face.sqrMagnitude > 0.01f)
                    hero.go.transform.rotation = Quaternion.Slerp(hero.go.transform.rotation, Quaternion.LookRotation(face), 1f - Mathf.Exp(-2f * dt));
            }

            foreach (var a in actors.Values) a.Tick(dt);
            UpdatePlane();
            UpdateChute();
            UpdateZoneWall();
            if (film > 6.6f && film < 9.2f) { EmitWind(90f); CameraFx.Sustain(0.22f); }
            else if (film >= 9.2f && film < 11f) EmitWind(16f);
        }

        void LateUpdate()
        {
            if (finished) return;
            Shot(film);
            CameraFx.Apply(cam.transform, cam, record ? 1f / fps : Time.unscaledDeltaTime);
            dof.focusDistance.value = Mathf.Max(0.3f, focusDistance);
            dof.focalLength.value = focalLength;
            dof.aperture.value = aperture;
            if (!record && film >= Duration) Finish();
        }

        // ================================================================== camera
        void Cam(Vector3 pos, Vector3 look, float fov, float focal = 35f, float fstop = 11f, float roll = 0f)
        {
            cam.transform.position = pos;
            var dir = look - pos;
            cam.transform.rotation = Quaternion.LookRotation(dir.sqrMagnitude > 1e-6f ? dir : Vector3.forward, Vector3.up) * Quaternion.Euler(0f, 0f, roll);
            cam.fieldOfView = fov;
            focusDistance = dir.magnitude;
            focalLength = focal;
            aperture = fstop;
        }

        static float U(float f, float a, float b) => Mathf.Clamp01((f - a) / (b - a));
        static float Ease(float x) => Smooth(x);
        static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x) * (1f - x); }

        void Shot(float f)
        {
            Vector3 up = Vector3.up;
            if (f < 5f)          // A: island reveal, the drop plane crossing high above
            {
                float u = Ease(U(f, 0f, 5f));
                var from = new Vector3(S.x, 0f, S.z) + O * 240f; from.y = 38f;
                var to = new Vector3(S.x, 0f, S.z) + O * 150f; to.y = 58f;
                var look = Vector3.Lerp(S + up * 12f, PlanePos(f), Mathf.Lerp(0.15f, 0.4f, u));
                Cam(Vector3.Lerp(from, to, u), look, Mathf.Lerp(46f, 40f, u), 35f, 16f, Mathf.Lerp(-3f, 2f, u));
            }
            else if (f < 6.6f)   // B1: riding beside the tail, the cat leaps off the ramp
            {
                float u = U(f, 5f, 6.6f);
                var pos = plane.TransformPoint(new Vector3(-10f + 2f * u, -1.2f - 2.5f * u, -25f + 3f * u));
                var look = Vector3.Lerp(plane.TransformPoint(new Vector3(0f, -1.5f, -6f)), hero.go.activeSelf ? hero.Pos + up : plane.TransformPoint(DropShipVisual.ExitOffset), U(f, 5.9f, 6.6f) * 0.7f);
                Cam(pos, look, 54f, 28f, 16f, -6f);
            }
            else if (f < 9f)     // B2: chasing the free fall from above, the island rushing up
            {
                float u = U(f, 6.6f, 9f);
                var hv = new Vector3(hero.airVel.x, 0f, hero.airVel.z);
                var back = hv.sqrMagnitude > 0.5f ? -hv.normalized : -planeDir;
                var pos = hero.Pos + up * Mathf.Lerp(4.5f, 3.2f, u) + back * Mathf.Lerp(3.5f, 2.4f, u) + Vector3.Cross(up, back) * 0.8f;
                Cam(pos, hero.Pos + Vector3.down * 2.5f - back * 1.5f, 64f, 24f, 16f, Mathf.Sin(f * 1.7f) * 4f);
            }
            else if (f < 11f)    // C1: under the cat as the canopy bursts open against the sky
            {
                float u = U(f, 9f, 11f);
                float a = Mathf.Lerp(-40f, 25f, u) * Mathf.Deg2Rad;
                var around = (R * Mathf.Sin(a) + F * Mathf.Cos(a));
                var pos = hero.Pos + around * 5.2f + Vector3.down * 3.2f;
                Cam(pos, hero.Pos + up * 2.0f, 52f, 35f, 11f);
            }
            else if (f < 13.5f)  // C2: low on the meadow, the cat floats down and lands
            {
                float u = Ease(U(f, 11f, 13.5f));
                var pos = Vector3.Lerp(W(2.3f, 5.2f, 0.32f), W(1.5f, 3.6f, 0.55f), u);
                Cam(pos, hero.Pos + up * 1.15f, 42f, 50f, 4f);
            }
            else if (f < 15.6f)  // D: reload close-up, slow arc
            {
                float u = Ease(U(f, 13.5f, 15.6f));
                float a = Mathf.Lerp(-38f, 32f, u) * Mathf.Deg2Rad;
                var hf = hero.go.transform.forward; var hr = hero.go.transform.right;
                var pos = hero.Pos + (hf * Mathf.Cos(a) + hr * Mathf.Sin(a)) * 1.85f + up * Mathf.Lerp(0.85f, 1.05f, u);
                Cam(pos, hero.Pos + up * 1.12f, 34f, 70f, 2.8f);
            }
            else if (f < 18f)    // E: tracking the Knight's charge
            {
                var dir = (W(6.5f, 12.5f) - W(14f, 23f)); dir.y = 0f; dir.Normalize();
                var side = Vector3.Cross(up, dir);
                var pos = knight.Pos + side * 2.7f + dir * 0.9f + up * 0.85f;
                Cam(pos, knight.Pos + up * 1.0f + dir * 1.2f, 46f, 50f, 4f, -3f);
            }
            else if (f < 19.3f)  // F1: over the hero's shoulder
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.right * 0.75f + up * 1.62f - t.forward * 1.7f;
                Cam(pos, knight.Chest, 38f, 50f, 5.6f);
                focusDistance = Vector3.Distance(pos, knight.Chest);
            }
            else if (f < 20.4f)  // F2: in the Knight's face as the shotgun barks
            {
                var t = knight.go.transform;
                float u = U(f, 19.3f, 20.4f);
                var pos = knight.Pos + t.forward * Mathf.Lerp(2.6f, 2.1f, u) - t.right * 0.95f + up * 1.1f;
                Cam(pos, knight.Chest + t.forward * 0.4f, 40f, 50f, 4f, 4f);
            }
            else if (f < 21.5f)  // F3: running with the hero, gun blazing
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.forward * 3.3f + t.right * 1.7f + up * 0.85f;
                Cam(pos, hero.Chest, 44f, 50f, 4f, -4f);
            }
            else if (f < 22.05f) // F4a: the Ninja's scope view
            {
                var t = ninja.go.transform;
                var pos = ninja.Pos + t.right * 0.55f + up * 1.0f - t.forward * 1.25f;
                Cam(pos, hero.Chest, 19f, 85f, 4f);
                focusDistance = Vector3.Distance(pos, hero.Chest);
            }
            else if (f < 23.3f)  // F4b: bullet time - the sniper round grazes over the hero's head
            {
                float u = Ease(U(f, 22.05f, 23.3f));
                var toN = ninja.Pos - hero.Pos; toN.y = 0f; toN.Normalize();
                var side = Vector3.Cross(up, toN);
                float a = Mathf.Lerp(-25f, 20f, u) * Mathf.Deg2Rad;
                var arm = (side * Mathf.Cos(a) - toN * Mathf.Sin(a));
                var pos = hero.Pos + up * 1.5f + arm * 1.6f - toN * 0.9f;
                Cam(pos, hero.Pos + up * 1.55f + toN * 4f, 46f, 50f, 2.8f);
                focusDistance = Vector3.Distance(pos, hero.Chest);
            }
            else if (f < 24.2f)  // F5: the Knight takes hits
            {
                var t = knight.go.transform;
                var pos = knight.Pos + t.right * 2.8f + t.forward * 1.3f + up * 1.2f;
                Cam(pos, knight.Chest, 40f, 50f, 4f);
            }
            else if (f < 27f)    // G: slow-motion orbit around the elimination
            {
                float u = Ease(U(f, 24.2f, 27f));
                var t = knight.go.transform;
                float a = Mathf.Lerp(20f, 160f, u) * Mathf.Deg2Rad;
                var arm = t.right * Mathf.Cos(a) + t.forward * Mathf.Sin(a);
                var pos = knight.Pos + arm * Mathf.Lerp(3.0f, 3.6f, u) + up * Mathf.Lerp(1.3f, 0.75f, u);
                Cam(pos, knight.Pos + up * Mathf.Lerp(0.95f, 0.45f, u), 38f, 50f, 3.5f);
            }
            else if (f < 29.2f)  // H: telephoto over the shoulder, the long shot
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.right * 0.6f + up * 1.6f - t.forward * 1.5f;
                var target = ninja.Pos + up * 0.6f;
                Cam(pos, Vector3.Lerp(hero.Pos + t.forward * 10f + up * 1.2f, target, Ease(U(f, 27f, 27.7f))), Mathf.Lerp(30f, 15f, Ease(U(f, 27f, 27.7f))), 135f, 5.6f);
                focusDistance = Vector3.Distance(pos, target);
            }
            else if (f < 31.4f)  // I1: running from the storm, camera backing away in front
            {
                var pos = hero.Pos + F * 5.6f + R * 1.3f + up * 0.6f;
                Cam(pos, hero.Pos + up * 1.75f, 50f, 35f, 5.6f, 3f);
                focusDistance = Vector3.Distance(pos, hero.Chest);
            }
            else if (f < 33f)    // I2: wide from above, the purple wall sweeping across the meadow
            {
                float u = Ease(U(f, 31.4f, 33f));
                var pos = Vector3.Lerp(W(20f, 16f, 18f), W(14f, 22f, 23f), u);
                Cam(pos, hero.Pos + up, 50f, 24f, 16f);
            }
            else if (f < 35.8f)  // J1: dolly along the lineup
            {
                float u = Ease(U(f, 33f, 35.8f));
                var L = W(0f, 12f);
                var pos = L + F * Mathf.Lerp(5.4f, 6.4f, u) + R * Mathf.Lerp(7.5f, -6.5f, u) + up * Mathf.Lerp(0.5f, 1.3f, u);
                Cam(pos, L + up * 0.95f + R * Mathf.Lerp(3f, -3f, u) - F * 1.2f, 38f, 50f, 5.6f);
            }
            else if (f < 38f)    // J2: hero shot, the squad soft behind him
            {
                float u = Ease(U(f, 35.8f, 38f));
                var L = W(0f, 12f);
                var pos = L + F * Mathf.Lerp(3.3f, 2.4f, u) + R * 0.35f + up * Mathf.Lerp(0.45f, 0.6f, u);
                Cam(pos, hero.Pos + up * 1.2f, 34f, 70f, 2.8f);
                focusDistance = Vector3.Distance(pos, hero.Chest);
            }
            else                 // K: crane up and back for the title
            {
                float u = Ease(U(f, 38f, Duration));
                var L = W(0f, 12f);
                var pos = L + F * Mathf.Lerp(2.4f, 10f, u) + R * Mathf.Lerp(0.35f, 0f, u) + up * Mathf.Lerp(0.6f, 5.2f, u);
                Cam(pos, L + up * Mathf.Lerp(1.2f, 0.6f, u) - F * 1.2f, 34f + 8f * u, 50f, Mathf.Lerp(2.8f, 8f, u));
                focusDistance = Vector3.Distance(pos, hero.Chest);
            }
        }

        // ================================================================== shooting
        void Shoot(Actor from, Actor target, bool hit) => Shoot(from, target.Chest, hit, false, target);

        void Shoot(Actor from, Vector3 aim, bool hit, bool nearMiss, Actor target = null)
        {
            var w = Weapons.Get(from.weapon);
            var muzzle = from.Muzzle();
            from.anim.SetTrigger("Fire");
            from.fx = from.fx != null ? from.fx : MuzzleFlash.For(from.go);
            float size = w.pellets > 1 ? 1.45f : w.fireRate < 2f ? 1.35f : w.damage < 16f ? 0.8f : 1f;
            float ts = Mathf.Max(0.05f, Time.timeScale);
            float pitch = Mathf.Lerp(0.55f, 1f, Mathf.Sqrt(ts));
            Cue(GameAudio.GunSound(w), 1f, pitch);
            if (UnityEngine.Random.value < 0.6f) Cue("shell", 0.3f, pitch, 0f, 0.35f);
            if (from.fx != null) from.fx.Play(muzzle, aim - muzzle, size, from.go.transform.right);

            int pellets = Mathf.Max(1, w.pellets);
            for (int i = 0; i < pellets; i++)
            {
                var dir = (aim - muzzle).normalized;
                float spread = pellets > 1 ? w.spreadDegrees : (hit ? 0f : 0f);
                if (spread > 0f) dir = Quaternion.Euler(UnityEngine.Random.Range(-spread, spread), UnityEngine.Random.Range(-spread, spread), 0f) * dir;
                Vector3 end; Vector3 normal; int surface; bool landed;
                if (hit && target != null)
                {
                    end = aim + UnityEngine.Random.insideUnitSphere * 0.12f; normal = -dir; surface = MuzzleFlash.Flesh; landed = true;
                }
                else
                {
                    // misses: past the target into the ground / scenery behind it (real raycast for the impact surface)
                    var far = muzzle + dir * 160f;
                    if (!nearMiss && pellets == 1) far = muzzle + ((aim + new Vector3(UnityEngine.Random.Range(-1.6f, 1.6f), -1.6f, UnityEngine.Random.Range(-1.6f, 1.6f))) - muzzle).normalized * 160f;
                    if (Physics.Raycast(muzzle, (far - muzzle).normalized, out var h, 160f, ~0, QueryTriggerInteraction.Ignore))
                    { end = h.point; normal = h.normal; surface = MuzzleFlash.SurfaceOf(h.collider); landed = true; }
                    else { end = far; normal = Vector3.zero; surface = 0; landed = false; }
                }
                if (from.fx != null) from.fx.Tracer(muzzle, end, w.color, landed, normal, surface);
                if (landed && i < 3)
                {
                    float travel = Vector3.Distance(muzzle, end) / 140f;   // MuzzleFlash.TracerSpeed (game seconds)
                    var t = target;
                    bool fleshHit = surface == MuzzleFlash.Flesh;
                    int s = surface;
                    Delay(travel, () =>
                    {
                        float p2 = Mathf.Lerp(0.55f, 1f, Mathf.Sqrt(Mathf.Max(0.05f, Time.timeScale)));
                        Cue(GameAudio.ImpactSound(s), fleshHit ? 0.8f : 0.55f, p2);
                        if (fleshHit && t != null) { t.Flash(); t.anim.SetTrigger("Hit"); Cue("hitmarker", 0.5f, p2); }
                    });
                }
            }
            if (nearMiss) Cue("banner_whoosh", 0.9f, 0.6f, 0f, 0.15f);
        }

        // ================================================================== sound cues (mixed by Tools/Cinematic/make_promo.py)
        void Cue(string clip, float volume, float pitch = 1f, float loopSeconds = 0f, float delay = 0f)
        {
            if (cueCount++ > 0) cues.Append(",\n");
            cues.Append("  {\"t\": ").Append((film + delay).ToString("0.000", CultureInfo.InvariantCulture))
                .Append(", \"clip\": \"").Append(clip).Append("\", \"vol\": ").Append(volume.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(", \"pitch\": ").Append(pitch.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(", \"loop\": ").Append(loopSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append("}");
        }

        void CueReload(float seconds, float vol = 0.8f)
        {
            Cue("reload_out", vol, 1f, 0f, seconds * 0.11f);
            Cue("reload_in", vol, 1f, 0f, seconds * 0.63f);
            Cue("reload_charge", vol, 1f, 0f, seconds * 0.8f);
        }

        // ================================================================== capture
        IEnumerator Capture()
        {
            var wait = new WaitForEndOfFrame();
            while (!finished)
            {
                yield return wait;
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                RenderTexture.active = prev;
                var bytes = readback.EncodeToJPG(jpgQuality);
                File.WriteAllBytes(Path.Combine(outDir, "frame_" + frame.ToString("00000") + ".jpg"), bytes);
                frame++;
                if (frame >= Mathf.CeilToInt(Duration * fps)) Finish();
            }
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            Time.captureFramerate = 0;
            Time.timeScale = 1f;
            if (record && outDir != null)
            {
                File.WriteAllText(Path.Combine(outDir, "cues.json"), "[\n" + cues + "\n]\n");
                File.WriteAllText(Path.Combine(outDir, "meta.json"),
                    "{\"fps\": " + fps + ", \"frames\": " + frame + ", \"width\": " + width + ", \"height\": " + height + "}\n");
                Debug.Log("[Promo] done: " + frame + " frames in " + outDir + "  ->  python Tools/Cinematic/make_promo.py \"" + outDir + "\"");
            }
#if UNITY_EDITOR
            if (record) UnityEditor.EditorApplication.isPlaying = false;
#else
            if (record) Application.Quit();
#endif
        }

        void OnGUI()
        {
            if (rt == null) return;
            // the camera renders into the recording texture: show it letterboxed in the Game view
            float aspect = width / (float)height;
            float w = Screen.width, h = Screen.width / aspect;
            if (h > Screen.height) { h = Screen.height; w = h * aspect; }
            GUI.DrawTexture(new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h), rt, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(10, 10, 600, 24), (record ? "REC " : "") + film.ToString("0.00") + " / " + Duration + " s   frame " + frame);
        }

        // ================================================================== actor
        class Actor
        {
            readonly PromoCutscene d;
            public readonly string id;
            public readonly GameObject go;
            public readonly Animator anim;
            public MuzzleFlash fx;
            public int weapon;
            readonly Renderer rifle;
            readonly Renderer[] renderers;
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            float flashAt = -10f;

            // locomotion
            Vector3 moveTarget; bool moving; float moveSpeed;
            Vector3 moveDir; float moveUntil = -1f;
            public Actor face;
            public bool airborne; public Vector3 airVel;
            float hopStart = -10f, hopHeight;
            Vector3 lastPos; bool first = true;
            bool dead;

            // firing
            public bool firing; Actor fireTarget; float fireUntil, fireInterval, hitChance, nextShot;

            public Actor(PromoCutscene director, CastMember m)
            {
                d = director; id = m.id;
                go = Instantiate(m.model);
                go.name = "Promo_" + m.id;
                anim = go.GetComponent<Animator>();
                if (anim == null) anim = go.AddComponent<Animator>();
                if (m.controller != null) anim.runtimeAnimatorController = m.controller;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
                var list = new List<Renderer>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer) continue;
                    list.Add(r);
                    if (rifle == null && r.name.ToLowerInvariant().Contains("rifl")) rifle = r;
                    if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                }
                renderers = list.ToArray();
                if (go.GetComponent<FootFx>() == null) go.AddComponent<FootFx>();
            }

            public Vector3 Pos => go.transform.position;
            public Vector3 Chest => go.transform.position + Vector3.up * (anim.GetBool("Crouching") ? 0.72f : 1.08f);

            public void Show(bool on)
            {
                if (go.activeSelf == on) return;
                go.SetActive(on);
                if (on) { anim.SetBool("Grounded", true); first = true; }
            }

            public void Teleport(Vector3 p, float yaw)
            {
                go.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, yaw, 0f));
                first = true;
            }

            public void MoveTo(Vector3 target, float speed) { moveTarget = target; moving = true; moveSpeed = speed; moveUntil = -1f; }
            public void MoveDir(Vector3 dir, float speed, float untilFilm) { dir.y = 0f; moveDir = dir.normalized; moveSpeed = speed; moveUntil = untilFilm; moving = false; }
            public void StopMoving() { moving = false; moveUntil = -1f; }
            public void Hop(float h) { hopStart = d.game; hopHeight = h; anim.SetTrigger("Jump"); }

            public void StartFire(Actor target, float untilFilm, float rate, float hits)
            {
                firing = true; fireTarget = target; fireUntil = untilFilm; fireInterval = 1f / rate; hitChance = hits; nextShot = d.game;
                face = target;
            }

            public void Die(bool fromFront)
            {
                dead = true; firing = false; StopMoving();
                anim.SetBool("DeathBack", fromFront);
                anim.SetBool("Dead", true);
            }

            public void Revive()
            {
                if (!dead) return;
                dead = false;
                anim.SetBool("Dead", false);
                anim.Rebind();
                anim.Update(0f);
            }

            public void Flash() { flashAt = Time.time; }

            public Vector3 Muzzle()
            {
                if (rifle != null && rifle.enabled)
                {
                    var b = rifle.bounds; var dir = go.transform.forward; var e = b.extents;
                    return b.center + dir * (Mathf.Abs(e.x * dir.x) + Mathf.Abs(e.y * dir.y) + Mathf.Abs(e.z * dir.z));
                }
                return Pos + Vector3.up * 1.3f + go.transform.forward * 0.6f;
            }

            public void Tick(float dt)
            {
                if (!go.activeSelf) return;
                var t = go.transform;
                if (!airborne)
                {
                    var p = t.position;
                    if (!dead && moving)
                    {
                        var to = moveTarget - p; to.y = 0f;
                        float dist = to.magnitude;
                        float sp = Mathf.Min(moveSpeed, dist * 3f);
                        if (dist < 0.05f) moving = false; else p += to / dist * sp * dt;
                    }
                    else if (!dead && moveUntil > 0f)
                    {
                        if (d.film < moveUntil) p += moveDir * moveSpeed * dt; else moveUntil = -1f;
                    }
                    float hop = 0f;
                    float ht = d.game - hopStart;
                    if (ht >= 0f && ht < 0.75f) hop = Mathf.Sin(ht / 0.75f * Mathf.PI) * hopHeight;
                    p.y = d.Ground(p) + hop;
                    t.position = p;

                    // facing: a target, else the walking direction
                    Vector3 look = Vector3.zero;
                    if (face != null) look = face.Pos - p;
                    else if (moving) look = moveTarget - p;
                    else if (moveUntil > 0f) look = moveDir;
                    look.y = 0f;
                    if (!dead && look.sqrMagnitude > 0.01f)
                        t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(look), 420f * dt);
                }

                // animator from the actual motion (works for every way the cat is moved)
                if (dt > 0f)
                {
                    var v = first ? Vector3.zero : (t.position - lastPos) / dt;
                    first = false;
                    lastPos = t.position;
                    if (airborne || v.sqrMagnitude > 400f) v = Vector3.zero;
                    var local = t.InverseTransformDirection(new Vector3(v.x, 0f, v.z)) / WalkSpeed;
                    anim.SetFloat("MoveX", local.x, 0.1f, dt);
                    anim.SetFloat("MoveY", local.z, 0.1f, dt);
                    anim.SetFloat("MoveSpeed", new Vector2(local.x, local.z).magnitude, 0.1f, dt);
                }
                int upper = anim.GetLayerIndex("UpperBody");
                if (upper >= 0) anim.SetLayerWeight(upper, Mathf.MoveTowards(anim.GetLayerWeight(upper), dead ? 0f : 1f, dt * 8f));

                // firing bursts
                if (firing && !dead)
                {
                    if (d.film >= fireUntil || fireTarget == null) firing = false;
                    else if (d.game >= nextShot)
                    {
                        nextShot = d.game + fireInterval * UnityEngine.Random.Range(0.9f, 1.15f);
                        d.Shoot(this, fireTarget, UnityEngine.Random.value < hitChance);
                    }
                }

                // red hit flash (HitReaction look)
                float ft = (Time.time - flashAt) / 0.16f;
                if (ft >= 0f && ft < 1f)
                {
                    var c = Color.Lerp(new Color(2.4f, 0.75f, 0.65f), Color.white, ft * ft);
                    foreach (var r in renderers) { if (r == null) continue; r.GetPropertyBlock(block); block.SetColor("_BaseColor", c); r.SetPropertyBlock(block); }
                }
                else if (ft >= 1f && ft < 3f)
                {
                    foreach (var r in renderers) if (r != null) r.SetPropertyBlock(null);
                    flashAt = -10f;
                }
            }
        }
    }
}
