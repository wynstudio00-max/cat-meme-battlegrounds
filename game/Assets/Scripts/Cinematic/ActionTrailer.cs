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
    /// 40-second ACTION TRAILER (2026-10-31, user: fast, switching cameras, firefights, explosions; battle royale + zombies, 16:9).
    /// Scene: CatBattleground > Cinematic > Build Action Trailer Scene (copy of the Phase 2 arena). Everything is scripted here, offline:
    /// cats / zombies are the plain models driven through their Animators, guns are MuzzleFlash tracers, bombs are the game's Bomb +
    /// BombFx, kills KillFx.
    ///
    ///   0-5.6   DROP     plane fly-by, the Army cat jumps, free fall, chute, landing in the town square of the island
    ///   5.6-22  FIGHT    sprint, Knight charges, over-the-shoulder burst, shotgun blast, bomb throw + slow-motion explosion,
    ///                    Ninja scope + bullet-time near miss, sideways run-and-gun, wide firefight with a second bomb,
    ///                    long-range kill on the Ninja, storm wall, three macro close-ups
    ///   22.6-33  HORDE   (zombie town, dark green grade) scream close-up, horde charging, back-to-back last stand,
    ///                    bomb into the horde, boss roar, the boss goes down
    ///   33-40    TITLE   crane up over the square (titles, music and SFX are added by Tools/Cinematic/make_trailer.py)
    ///
    /// Recording works like PromoCutscene: Time.captureFramerate, every frame -> JPG + cues.json (sound events) in
    /// &lt;project&gt;/Recordings/Trailer_&lt;date&gt;. Film time (cuts, camera) is unscaled, game time goes through Time.timeScale.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class ActionTrailer : MonoBehaviour
    {
        [Serializable]
        public class CastMember
        {
            public string id;
            public GameObject model;
            public RuntimeAnimatorController controller;
        }

        [SerializeField] public CastMember[] cast = new CastMember[0];
        [SerializeField] public GameObject zombieModel;                       // Assets/Art/Zombies/ZombieCat.fbx
        [SerializeField] public RuntimeAnimatorController zombieController;   // Assets/Animations/Zombie.controller
        [SerializeField] public bool record = true;
        [SerializeField] public int width = 1920, height = 1080, fps = 30;
        [Range(50, 100)] [SerializeField] public int jpgQuality = 95;
        [Tooltip("Preview from this second (recordings always start at 0).")]
        [SerializeField] public float previewStart = 0f;

        public const float Duration = 40f;
        const float WalkSpeed = 1.4f, Sprint = 4f, ZombieRun = 3.1f;
        const float PlaneHeight = 110f, PlaneSpeed = 46f, JumpAt = 1.55f;
        const int Horde = 12;

        // ------------------------------------------------------------------ state
        Camera cam;
        RenderTexture rt;
        Texture2D readback;
        DepthOfField dof;
        ColorAdjustments grade;
        Vignette vignette;
        string outDir;
        int frame;
        float film, game;
        bool finished;
        readonly StringBuilder cues = new StringBuilder();
        int cueCount;

        class Stage { public Vector3 c, f, r; }
        Stage town, zone;     // BR town square, zombie town square
        Vector3 planeDir;

        Actor hero, knight, ninja, soldier, black, gold, boss;
        readonly List<Actor> zombies = new List<Actor>();
        readonly List<Actor> all = new List<Actor>();
        readonly Dictionary<string, Actor> cats = new Dictionary<string, Actor>();

        Transform plane;
        readonly List<Transform> props = new List<Transform>();
        GameObject chute; Transform canopy; float chuteOpenedAt = -1f, chuteCollapseAt = -1f; Vector3 canopyScale = Vector3.one;
        GameObject zoneWall;
        ParticleSystem windStreaks;
        float focusDistance = 10f, focalLength = 50f, aperture = 8f;
        float darkness;   // 0 = day grade, 1 = horde grade

        readonly List<(float t, Action a)> events = new List<(float, Action)>();
        int nextEvent;
        readonly List<(float g, Action a)> pending = new List<(float, Action)>();
        readonly List<(float t, Action a)> filmPending = new List<(float, Action)>();   // added while playing (bomb blasts)
        readonly List<(float t, Action<float> shot)> shots = new List<(float, Action<float>)>();
        ulong bombIds = 7000;

        // ================================================================== setup
        void Start()
        {
            cam = Camera.main;
            if (cam == null) { cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>(); cam.tag = "MainCamera"; }
            var tpc = cam.GetComponent<ThirdPersonCamera>(); if (tpc != null) tpc.enabled = false;
            cam.nearClipPlane = 0.06f; cam.farClipPlane = 900f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;

            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "TrailerRT" };
            rt.Create();
            cam.targetTexture = rt;
            readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            BuildVolume();
            FindStages();
            BuildCast();
            if (!enabled) return;
            BuildPlane();
            BuildWind();
            BuildTimeline();
            BuildShots();

            if (record)
            {
                previewStart = 0f;
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
                outDir = Path.Combine(root, "Trailer_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(outDir);
                Time.captureFramerate = fps;
                AudioListener.volume = 0f;   // the soundtrack is mixed afterwards from cues.json
                Debug.Log("[Trailer] recording " + width + "x" + height + " @" + fps + " to " + outDir);
                StartCoroutine(Capture());
            }
            film = Mathf.Clamp(previewStart, 0f, Duration);
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

        // ------------------------------------------------------------------ the two stages
        void FindStages()
        {
            // BR: the middle of the biggest cluster of BattlefieldTown houses = a town's open square (ArenaBuilder.PlanTown)
            var houses = new List<Vector3>();
            var b = GameObject.Find("Arena/Buildings");
            if (b != null)
                foreach (Transform c in b.transform)
                    if (c.name.StartsWith("SM_House", StringComparison.OrdinalIgnoreCase)) houses.Add(c.position);
            if (houses.Count == 0 && b != null) foreach (Transform c in b.transform) houses.Add(c.position);
            Vector3 centre = Vector3.zero; int best = -1;
            foreach (var h in houses)
            {
                int n = 0; var sum = Vector3.zero;
                foreach (var o in houses) if (Flat(o - h) < 45f) { n++; sum += o; }
                if (n > best) { best = n; centre = sum / n; }
            }
            town = MakeStage(centre, 400f);

            var start = GameObject.Find("Arena/ZombieArena/ZombieStart");
            var zc = start != null ? start.transform.position : new Vector3(0f, 0f, 900f);
            zone = MakeStage(zc, zc.y + 60f);
            Debug.Log($"[Trailer] town {town.c} -> {town.f}, zombie town {zone.c} -> {zone.f}");
        }

        /// <summary>An open spot near p and the direction with the longest clear view (the fight runs along it).</summary>
        Stage MakeStage(Vector3 p, float rayTop)
        {
            p.y = GroundAt(p, rayTop);
            Vector3 bestP = p; float bestOpen = -1f;
            for (float r = 0f; r <= 10f; r += 2f)
                for (float a = 0f; a < 360f; a += 45f)
                {
                    var q = p + new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad)) * r;
                    q.y = GroundAt(q, q.y + 30f);
                    float open = Clearance(q, Vector3.zero, 12f);
                    if (open > bestOpen + 0.5f) { bestOpen = open; bestP = q; }
                    if (r == 0f) break;
                }
            Vector3 bestF = Vector3.forward; float far = -1f;
            for (float a = 0f; a < 360f; a += 22.5f)
            {
                var d = new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad));
                float c = Clearance(bestP, d, 40f);
                if (c > far) { far = c; bestF = d; }
            }
            return new Stage { c = bestP, f = bestF, r = Vector3.Cross(Vector3.up, bestF).normalized };
        }

        /// <summary>Free distance from p (chest height) along dir; with dir = 0 the smallest free distance all round.</summary>
        static float Clearance(Vector3 p, Vector3 dir, float max)
        {
            var o = p + Vector3.up * 1.1f;
            if (dir != Vector3.zero) return Physics.Raycast(o, dir, out var h, max, ~0, QueryTriggerInteraction.Ignore) ? h.distance : max;
            float min = max;
            for (float a = 0f; a < 360f; a += 30f)
            {
                var d = new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad));
                if (Physics.Raycast(o, d, out var h, max, ~0, QueryTriggerInteraction.Ignore)) min = Mathf.Min(min, h.distance);
            }
            return min;
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        /// <summary>Ground height under p, with the ray starting at <paramref name="top"/> (below roofs when it starts near the ground).</summary>
        static float GroundAt(Vector3 p, float top)
        {
            var hits = Physics.RaycastAll(new Vector3(p.x, top, p.z), Vector3.down, top + 200f, ~0, QueryTriggerInteraction.Ignore);
            float y = float.NegativeInfinity;
            foreach (var h in hits) if (h.point.y <= top && h.point.y > y && h.collider.attachedRigidbody == null) y = h.point.y;
            return float.IsNegativeInfinity(y) ? p.y : y;
        }

        /// <summary>Stage space: x right, z forward, h above the ground.</summary>
        static Vector3 W(Stage s, float x, float z, float h = 0f)
        {
            var p = s.c + s.r * x + s.f * z;
            p.y = GroundAt(p, s.c.y + 3f) + h;
            return p;
        }

        // ------------------------------------------------------------------ cast
        void BuildCast()
        {
            foreach (var m in cast)
            {
                if (m == null || m.model == null || string.IsNullOrEmpty(m.id)) continue;
                var a = new Actor(this, m.id, m.model, m.controller, false);
                a.weapon = Weapons.ForCharacter(m.id);
                cats[m.id] = a; all.Add(a);
            }
            Actor Pick(string id) => cats.TryGetValue(id, out var a) ? a : null;
            hero = Pick("army");
            if (hero == null) foreach (var a in cats.Values) { hero = a; break; }
            if (hero == null) { Debug.LogError("[Trailer] no cast (run CatBattleground > Cinematic > Build Action Trailer Scene)"); enabled = false; return; }
            knight = Pick("knight") ?? hero; ninja = Pick("ninja") ?? hero; soldier = Pick("soldier") ?? hero;
            black = Pick("black") ?? knight; gold = Pick("gold") ?? knight;

            if (zombieModel != null)
            {
                for (int i = 0; i < Horde + 1; i++)
                {
                    var z = new Actor(this, "zombie" + i, zombieModel, zombieController, true);
                    if (i == Horde) { z.SetScale(1.65f); boss = z; } else { z.SetScale(UnityEngine.Random.Range(0.88f, 1.06f)); zombies.Add(z); }
                    all.Add(z);
                }
            }
            else Debug.LogWarning("[Trailer] no zombie model - the horde shots stay empty");
            foreach (var a in all) a.Show(false);
        }

        // ------------------------------------------------------------------ props
        void BuildPlane()
        {
            planeDir = town.r;
            var root = new GameObject("TrailerPlane").transform;
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

        Vector3 PlanePos(float f) => new Vector3(town.c.x, town.c.y + PlaneHeight, town.c.z) + planeDir * (PlaneSpeed * (f - JumpAt)) - planeDir * 4f;

        void UpdatePlane()
        {
            bool show = film < 3.6f;
            if (plane.gameObject.activeSelf != show) plane.gameObject.SetActive(show);
            if (!show) return;
            plane.SetPositionAndRotation(PlanePos(film) + Vector3.up * Mathf.Sin(film * 0.9f) * 0.4f,
                Quaternion.LookRotation(planeDir, Vector3.up) * Quaternion.Euler(Mathf.Sin(film * 0.7f) * 1.2f, 0f, Mathf.Sin(film * 0.5f) * 2.5f));
            var fwd = plane.forward;
            foreach (var p in props) p.RotateAround(p.position, fwd, 1500f * Time.unscaledDeltaTime);
        }

        void BuildChute()
        {
            chute = new GameObject("TrailerParachute");
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
            }
            pivot.localScale = canopyScale;
            canopy = pivot;
        }

        void UpdateChute()
        {
            if (chute == null || !chute.activeSelf) return;
            float t = game - chuteOpenedAt;
            float open = t < 0.14f ? Mathf.Lerp(0.15f, 1.18f, t / 0.14f) : 1f + 0.18f * Mathf.Exp(-(t - 0.14f) * 5.5f) * Mathf.Cos((t - 0.14f) * 17f);
            float collapse = 1f;
            if (chuteCollapseAt > 0f)
            {
                float c = Mathf.Clamp01((game - chuteCollapseAt) / 0.6f);
                collapse = 1f - c * c;
                chute.transform.localRotation = Quaternion.Euler(-70f * c, 0f, 0f);
                chute.transform.localPosition = Vector3.down * (1.2f * c);
                if (c >= 1f) { chute.SetActive(false); return; }
            }
            chute.transform.localScale = new Vector3(open, Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(t / 0.25f)) * open * collapse, open);
            float b = Mathf.Sin(game * 2.6f) * 0.035f;
            canopy.localScale = Vector3.Scale(canopyScale, new Vector3(1f - b * 0.5f, 1f + b, 1f - b * 0.5f));
        }

        void BuildZoneWall()
        {
            zoneWall = new GameObject("TrailerZoneWall");
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
                tri[k] = a; tri[k + 1] = a + 1; tri[k + 2] = a + 2; tri[k + 3] = a + 2; tri[k + 4] = a + 1; tri[k + 5] = a + 3;
            }
            var mesh = new Mesh { name = "TrailerZoneWall", vertices = v, triangles = tri, uv = uv };
            mesh.bounds = new Bounds(Vector3.up * 0.5f, Vector3.one);
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
            var c = town.c + town.f * 60f;   // the wall sweeps in from behind the cats
            float radius = 70f - 7f * Mathf.Max(0f, film - 19.6f);
            zoneWall.transform.position = new Vector3(c.x, town.c.y - 10f, c.z);
            zoneWall.transform.localScale = new Vector3(radius * 2f, 60f, radius * 2f);
        }

        void BuildWind()
        {
            var src = Resources.Load<Material>("FX/MuzzleSpark");
            var go = new GameObject("TrailerWindStreaks");
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
                windStreaks.Emit(new ParticleSystem.EmitParams
                {
                    position = c + UnityEngine.Random.insideUnitSphere * 7f + Vector3.down * UnityEngine.Random.Range(4f, 16f),
                    velocity = Vector3.up * UnityEngine.Random.Range(6f, 12f),
                    startSize = UnityEngine.Random.Range(0.012f, 0.03f), startLifetime = UnityEngine.Random.Range(0.35f, 0.7f),
                    startColor = Color.white, applyShapeToPosition = false,
                }, 1);
        }

        // ------------------------------------------------------------------ post FX: DOF, motion blur, grain, CA, the horde grade
        void BuildVolume()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            dof = profile.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(10f); dof.focalLength.Override(50f); dof.aperture.Override(8f);
            var mb = profile.Add<MotionBlur>(true);
            mb.intensity.Override(0.45f); mb.quality.Override(MotionBlurQuality.High);
            var grain = profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1); grain.intensity.Override(0.16f);
            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.16f);
            vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.3f); vignette.smoothness.Override(0.5f);
            grade = profile.Add<ColorAdjustments>(true);
            grade.postExposure.Override(0f); grade.saturation.Override(8f); grade.contrast.Override(14f); grade.colorFilter.Override(Color.white);
            var go = new GameObject("TrailerPostFX");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true; volume.priority = 50f; volume.sharedProfile = profile;
        }

        void UpdateGrade()
        {
            darkness = Mathf.MoveTowards(darkness, film >= 22.6f && film < 33.2f ? 1f : film >= 33.2f ? 0.55f : 0f, Time.unscaledDeltaTime * 3f);
            grade.postExposure.value = Mathf.Lerp(0.05f, -0.75f, darkness);
            grade.saturation.value = Mathf.Lerp(8f, -18f, darkness);
            grade.contrast.value = Mathf.Lerp(14f, 26f, darkness);
            grade.colorFilter.value = Color.Lerp(Color.white, new Color(0.78f, 1f, 0.82f), darkness);
            vignette.intensity.value = Mathf.Lerp(0.3f, 0.48f, darkness);
        }

        // ================================================================== timeline
        void At(float t, Action a) => events.Add((t, a));
        void Delay(float gameSeconds, Action a) => pending.Add((game + gameSeconds, a));
        static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        void BuildTimeline()
        {
            Stage T = town, Z = zone;

            // --- DROP
            At(0f, () => Cue("plane_loop", 1f, 1f, 2.6f));
            At(0.55f, () => Cue("banner_whoosh", 1f, 0.7f));
            At(JumpAt, () =>
            {
                hero.Show(true);
                var exit = plane.TransformPoint(DropShipVisual.ExitOffset);
                hero.Teleport(exit, Yaw(planeDir));
                hero.airborne = true;
                hero.airVel = planeDir * (PlaneSpeed * 0.5f) + Vector3.down * 2f;
                hero.anim.SetBool("Grounded", false);
                hero.anim.SetBool("Skydive", true);
                Cue("banner_whoosh", 0.8f);
            });
            At(2.4f, () => { hero.Teleport(T.c + Vector3.up * 70f - T.f * 12f, Yaw(T.f)); hero.airVel = T.f * 9f + Vector3.down * 28f; Cue("wind_freefall_loop", 1f, 1f, 1.3f); });
            At(3.6f, () =>
            {
                hero.Teleport(T.c + Vector3.up * 24f - T.f * 3f, Yaw(T.f)); hero.airVel = Vector3.down * 9f;
                BuildChute(); chute.SetActive(true); chuteOpenedAt = game;
                hero.anim.SetBool("Parachute", true);
                CameraFx.Shake(0.5f); Cue("parachute_open", 1f); Cue("wind_chute_loop", 0.7f, 1f, 1.1f);
            });
            At(4.6f, () => { hero.Teleport(W(T, 0f, 0f, 3.0f), Yaw(T.f)); hero.airVel = Vector3.down * 4.4f; });
            At(4.6f, () => Cue("ambient_loop", 0.45f, 1f, 18f));

            // --- FIGHT
            At(5.6f, () => { hero.Teleport(W(T, -6f, 2f), Yaw(T.r)); hero.MoveDir(T.r, Sprint, 6.8f); });
            At(6.75f, () =>
            {
                knight.Show(true); knight.Teleport(W(T, 9f, 18f), Yaw(-T.f));
                knight.MoveTo(W(T, 5f, 11f), Sprint);
                hero.face = knight;
            });
            At(7.65f, () => { hero.StartFire(knight, 8.8f, 10f, 0f); knight.MoveDir(-knight.go.transform.right, 2.8f, 8.8f); });
            At(8.95f, () => { knight.face = hero; Shoot(knight, hero, false); CameraFx.Shake(0.4f); });
            At(9.3f, () => { Shoot(knight, hero, false); CameraFx.Shake(0.35f); });
            At(9.7f, () => ThrowBomb(hero, knight.Pos + knight.go.transform.forward * -0.6f, 0.6f, 11.3f, k => KillNear(k, 4.5f)));
            At(12.2f, () =>
            {
                knight.Show(false);
                ninja.Show(true); ninja.Teleport(W(T, -8f, 22f), Yaw(hero.Pos - W(T, -8f, 22f))); ninja.anim.SetBool("Crouching", true); ninja.face = hero;
            });
            At(12.75f, () => { Shoot(ninja, hero.Pos + Vector3.up * 1.95f + hero.go.transform.right * 0.25f, false, true); CameraFx.Shake(0.25f); });
            At(14.55f, () => hero.Hop(0.55f));   // dodge while running sideways
            At(14.4f, () =>
            {
                hero.Teleport(W(T, -3f, 3f), Yaw(T.f)); hero.face = ninja;
                hero.MoveDir(T.r, 3.6f, 15.6f); hero.StartFire(ninja, 15.6f, 9f, 0f);
            });
            At(15.6f, () =>
            {
                hero.firing = false; hero.StopMoving();
                soldier.Show(true); soldier.Teleport(W(T, -4f, 6f), Yaw(T.f));
                black.Show(true); black.Revive(); black.Teleport(W(T, 6f, 19f), Yaw(-T.f));
                gold.Show(true); gold.Revive(); gold.Teleport(W(T, -2f, 21f), Yaw(-T.f));
                soldier.StartFire(black, 17f, 8f, 0.2f);
                black.StartFire(soldier, 17f, 7f, 0f);
                gold.StartFire(soldier, 15.75f, 6f, 0f);
                hero.StartFire(gold, 17f, 9f, 0.15f);
            });
            At(15.75f, () => ThrowBomb(gold, W(T, -5f, 8.5f), 0.6f, 16.85f, k => Cue("hitmarker_armor", 0.4f)));
            At(16.95f, () => { black.Die(true); KillFx.Play(black.Chest); Cue("kill_poof", 0.7f); });
            At(17.0f, () =>
            {
                foreach (var a in new[] { soldier, black, gold }) { a.firing = false; a.Show(false); }
                hero.Teleport(W(T, -2f, 2f), Yaw(T.f)); hero.face = ninja; hero.StopMoving();
            });
            At(17.7f, () => Shoot(hero, ninja, false));
            At(17.95f, () => Shoot(hero, ninja, true));
            At(18.55f, () =>
            {
                Shoot(hero, ninja.Pos + Vector3.up * 0.95f, true, false, ninja);
                Delay(0.12f, () => { ninja.Die(true); KillFx.Play(ninja.Chest); CameraFx.Shake(0.4f); Cue("kill_poof", 1f); Cue("hitmarker_kill", 0.9f); });
            });
            At(19.6f, () =>
            {
                ninja.Show(false);
                hero.face = null; hero.firing = false;
                hero.Teleport(W(T, -1f, -2f), Yaw(T.f)); hero.MoveDir(T.f, Sprint, 22f);
                soldier.Show(true); soldier.face = null; soldier.Teleport(W(T, 1.4f, -3f), Yaw(T.f)); soldier.MoveDir(T.f, Sprint, 22f);
                BuildZoneWall();
                Cue("zone_loop", 1f, 1f, 1.5f); Cue("announce_alarm", 0.6f);
            });
            At(21.0f, () =>
            {
                if (zoneWall != null) Destroy(zoneWall);
                zoneWall = null;
                hero.StopMoving(); soldier.StopMoving();
                hero.Teleport(W(T, 0f, 0f), Yaw(T.f)); soldier.Teleport(W(T, 3f, 1f), Yaw(T.f));
                black.Show(true); black.Revive(); black.Teleport(W(T, -3f, 1f), Yaw(T.f));
                var aim = W(T, 0f, 20f, 1f);
                foreach (var a in new[] { hero, soldier, black }) { a.faceAt = aim; a.firing = false; }
            });
            At(21.02f, () => Burst(hero, W(T, 0f, 20f, 1f), 4, 0.08f));
            At(21.35f, () => Burst(soldier, W(T, 3f, 20f, 1f), 4, 0.09f));
            At(21.68f, () => Burst(black, W(T, -3f, 20f, 1f), 4, 0.1f));

            // --- HORDE (zombie town)
            At(22.6f, () =>
            {
                foreach (var c in cats.Values) { c.Show(false); c.firing = false; c.face = null; c.faceAt = null; c.StopMoving(); c.Revive(); }
                Cue("ambient_loop", 0.25f, 0.8f, 10.4f);
                if (boss == null && zombies.Count == 0) return;
                var lead = zombies.Count > 0 ? zombies[0] : boss;
                lead.Show(true); lead.Teleport(W(Z, 0.4f, 7f), Yaw(-Z.f));
                lead.anim.SetTrigger("Scream"); Cue("zombie_scream", 1f); CameraFx.Shake(0.25f);
            });
            At(23.8f, () =>
            {
                // the squad in the middle of the square, the horde pouring in from the far street
                hero.Show(true); hero.Teleport(W(Z, 0f, 0f), Yaw(Z.f));
                soldier.Show(true); soldier.Teleport(W(Z, -1.3f, -0.6f), Yaw(Z.f - Z.r * 0.6f));
                knight.Show(true); knight.Revive(); knight.Teleport(W(Z, 1.3f, -0.6f), Yaw(Z.f + Z.r * 0.6f));
                for (int i = 0; i < zombies.Count; i++)
                {
                    var z = zombies[i];
                    float lane = (i % 4 - 1.5f) * 2.2f + UnityEngine.Random.Range(-0.6f, 0.6f);
                    float back = 16f + (i / 4) * 3.2f + UnityEngine.Random.Range(-0.8f, 0.8f);
                    z.Show(true); z.Revive(); z.Teleport(W(Z, lane, back), Yaw(-Z.f));
                    z.MoveTo(W(Z, lane * 0.45f, 2.2f + UnityEngine.Random.Range(0f, 1.2f)), ZombieRun * UnityEngine.Random.Range(0.9f, 1.15f));
                }
                Cue("zombie_groan", 0.8f, 0.9f); Cue("zombie_groan", 0.7f, 1.1f, 0f, 0.5f); Cue("zombie_attack", 0.6f, 1f, 0f, 1.0f);
            });
            // back-to-back: volleys pick the nearest zombie still standing
            for (float t = 25.25f; t < 26.8f; t += 0.13f)
            {
                float tt = t;
                At(tt, () => Volley(((int)((tt - 25.25f) / 0.13f) % 3) switch { 0 => hero, 1 => soldier, _ => knight }));
            }
            At(25.25f, () => knight.anim.SetBool("Crouching", true));
            At(26.8f, () => { foreach (var c in new[] { hero, soldier, knight }) { c.face = null; c.firing = false; } knight.anim.SetBool("Crouching", false); });
            At(26.85f, () =>
            {
                var target = Centroid(alive: true);
                ThrowBomb(hero, target, 0.8f, 28.15f, k => KillNear(k, 5.5f));
            });
            At(28.6f, () =>
            {
                if (boss == null) return;
                boss.Show(true); boss.Revive(); boss.Teleport(W(Z, 0f, 9f), Yaw(-Z.f));
                boss.anim.SetTrigger("Scream"); Cue("zombie_scream", 1f, 0.68f); CameraFx.Shake(0.6f);
            });
            At(29.6f, () =>
            {
                if (boss == null) return;
                boss.MoveTo(W(Z, 0f, 2.5f), 2.2f);
                hero.StartFire(boss, 30.35f, 10f, 1f); soldier.StartFire(boss, 30.35f, 8f, 1f); knight.StartFire(boss, 30.35f, 2.5f, 1f);
            });
            At(30.35f, () =>
            {
                if (boss == null) return;
                boss.Die(true); KillFx.Play(boss.Chest); CameraFx.Shake(0.7f);
                Cue("zombie_death", 1f, 0.68f); Cue("kill_poof", 1f, 0.8f); Cue("hitmarker_kill", 1f);
            });
            At(31.0f, () =>
            {
                // a new wave pours in from every side
                foreach (var c in new[] { hero, soldier, knight }) c.face = null;
                int k = 0;
                foreach (var z in zombies)
                {
                    float a = (k++ * 360f / Mathf.Max(1, zombies.Count)) * Mathf.Deg2Rad;
                    var dir = Z.f * Mathf.Cos(a) + Z.r * Mathf.Sin(a);
                    var from = Z.c + dir * 15f;
                    z.Show(true); z.Revive(); z.Teleport(new Vector3(from.x, GroundAt(from, Z.c.y + 3f), from.z), Yaw(-dir));
                    z.MoveTo(Z.c + dir * 3f, ZombieRun);
                }
                Cue("zombie_groan", 0.9f, 0.85f); Cue("zombie_scream", 0.6f, 1.15f, 0f, 0.6f);
            });
            At(31.1f, () => { hero.StartFire(zombies.Count > 0 ? zombies[0] : boss, 33f, 9f, 0.3f); soldier.StartFire(zombies.Count > 3 ? zombies[3] : boss, 33f, 8f, 0.3f); });
            At(33.0f, () => { foreach (var c in cats.Values) c.firing = false; });
            events.Sort((a, b) => a.t.CompareTo(b.t));
        }

        // slow motion: bomb, bullet time, the Ninja kill, the horde bomb, the boss
        float TimeScaleAt(float f)
        {
            float s = 1f;
            s = Mathf.Min(s, Ramp(f, 10.55f, 10.7f, 11.9f, 12.25f, 0.28f));
            s = Mathf.Min(s, Ramp(f, 12.8f, 12.9f, 13.95f, 14.35f, 0.12f));
            s = Mathf.Min(s, Ramp(f, 18.6f, 18.7f, 19.3f, 19.6f, 0.2f));
            s = Mathf.Min(s, Ramp(f, 28.05f, 28.12f, 28.45f, 28.6f, 0.25f));
            s = Mathf.Min(s, Ramp(f, 30.35f, 30.42f, 30.85f, 31.0f, 0.22f));
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

        // ------------------------------------------------------------------ actions
        Bomb lastBomb;
        Vector3 lastBombTarget;

        /// <summary>Throw animation, the bomb leaves the paw after 0.43 s (game) and lands after <paramref name="flight"/> s (game);
        /// it goes off at film second <paramref name="boomAt"/> (film time, so slow motion cannot push it past the shot).
        /// onBoom gets the blast point.</summary>
        void ThrowBomb(Actor from, Vector3 target, float flight, float boomAt, Action<Vector3> onBoom)
        {
            from.faceAt = target;
            from.anim.SetTrigger("Throw");
            Cue("bomb_throw", 0.8f, 1f, 0f, 0.35f);
            ulong id = bombIds++;
            Delay(0.43f, () =>
            {
                var fwd = target - from.Pos; fwd.y = 0f; fwd.Normalize();
                var start = from.Pos + Vector3.up * 1.35f + fwd * 0.35f - Vector3.Cross(Vector3.up, fwd) * 0.25f;
                var g = Physics.gravity;
                var v = (target + Vector3.up * 0.15f - start) / flight - 0.5f * g * flight;
                lastBomb = Bomb.Spawn(id, start, v, from.go.transform, 30f);
                lastBombTarget = target;
                Cue("bomb_fuse_loop", 0.5f, 1f, Mathf.Max(0.2f, boomAt - film));
                filmPending.Add((boomAt, () =>
                {
                    var b = Bomb.Find(id);
                    var at = b != null ? b.transform.position : target;
                    Bomb.Remove(id);
                    BombFx.Explode(at, 5.5f);
                    CameraFx.Shake(Mathf.Clamp01(0.9f - Vector3.Distance(cam.transform.position, at) * 0.03f));
                    Cue("bomb_explode", 1f, Mathf.Lerp(0.6f, 1f, Mathf.Sqrt(Mathf.Max(0.05f, Time.timeScale))));
                    onBoom?.Invoke(at);
                }));
            });
        }

        void KillNear(Vector3 at, float radius)
        {
            foreach (var a in all)
            {
                if (a == hero || a == soldier || !a.go.activeSelf || a.dead) continue;
                if (Flat(a.Pos - at) > radius) continue;
                a.Die(Vector3.Dot(a.go.transform.forward, at - a.Pos) > 0f);
                a.Fling((a.Pos - at).normalized * 3.5f + Vector3.up * 4.5f);
                if (!a.isZombie) KillFx.Play(a.Chest);
                Cue(a.isZombie ? "zombie_death" : "kill_poof", 0.7f, UnityEngine.Random.Range(0.85f, 1.15f), 0f, 0.05f);
            }
        }

        void Volley(Actor shooter)
        {
            Actor best = null; float bd = 1e9f;
            foreach (var z in zombies) if (z.go.activeSelf && !z.dead) { float d = Flat(z.Pos - shooter.Pos); if (d < bd) { bd = d; best = z; } }
            if (best == null) return;
            shooter.face = best;
            var target = best;
            Shoot(shooter, target, true);
            target.hits++;
            if (target.hits >= 2)
            {
                target.dead = true;   // nobody shoots it again
                float travel = Vector3.Distance(shooter.Muzzle(), target.Chest) / 140f;
                Delay(travel + 0.02f, () => { target.dead = false; target.Die(true); Cue("zombie_death", 0.6f, UnityEngine.Random.Range(0.9f, 1.2f)); });
            }
        }

        void Burst(Actor a, Vector3 aim, int count, float gap)
        {
            for (int i = 0; i < count; i++) { int k = i; Delay(k * gap, () => Shoot(a, aim + UnityEngine.Random.insideUnitSphere * 0.6f, false, false)); }
        }

        Vector3 Centroid(bool alive)
        {
            var s = Vector3.zero; int n = 0;
            foreach (var z in zombies) if (z.go.activeSelf && (!alive || !z.dead)) { s += z.Pos; n++; }
            return n > 0 ? s / n : W(zone, 0f, 6f);
        }

        // ================================================================== frame loop
        void Update()
        {
            if (finished) return;
            if (record) film = frame / (float)fps;
            else film = Mathf.Min(Duration, film + Time.unscaledDeltaTime);

            Time.timeScale = TimeScaleAt(film);
            float dt = Time.deltaTime;
            game += dt;

            while (nextEvent < events.Count && events[nextEvent].t <= film) { events[nextEvent].a(); nextEvent++; }
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].g <= game) { var a = pending[i].a; pending.RemoveAt(i); a(); }
            for (int i = filmPending.Count - 1; i >= 0; i--)
                if (filmPending[i].t <= film) { var a = filmPending[i].a; filmPending.RemoveAt(i); a(); }

            if (hero.airborne) FlyHero(dt);
            foreach (var a in all) a.Tick(dt);
            UpdatePlane();
            UpdateChute();
            UpdateZoneWall();
            UpdateGrade();
            if (film > 2.4f && film < 3.6f) { EmitWind(110f); CameraFx.Sustain(0.25f); }
            else if (film >= 3.6f && film < 4.6f) EmitWind(20f);
        }

        void FlyHero(float dt)
        {
            bool chuteOpen = chute != null && chute.activeSelf && chuteCollapseAt < 0f;
            var v = hero.airVel;
            float vy = chuteOpen ? Mathf.Lerp(v.y, -4.4f, 1f - Mathf.Exp(-4f * dt)) : Mathf.Max(v.y - 9.8f * dt, -34f);
            var flat = Vector3.Lerp(new Vector3(v.x, 0f, v.z), Vector3.zero, 1f - Mathf.Exp(-(chuteOpen ? 2f : 0.3f) * dt));
            hero.airVel = new Vector3(flat.x, vy, flat.z);
            var p = hero.Pos + hero.airVel * dt;
            float gy = GroundAt(p, p.y + 0.5f);
            if (p.y <= gy && film > 4.6f)
            {
                p.y = gy;
                hero.airborne = false;
                hero.anim.SetBool("Grounded", true);
                chuteCollapseAt = game + 0.08f;
                CameraFx.Shake(0.35f);
                Cue("land", 1f);
                Delay(0.25f, () => { hero.anim.SetBool("Parachute", false); hero.anim.SetBool("Skydive", false); });
            }
            hero.go.transform.position = p;
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
        void Cam(Vector3 pos, Vector3 look, float fov, float focal = 35f, float fstop = 8f, float roll = 0f)
        {
            pos = KeepCameraClear(pos, look);
            cam.transform.position = pos;
            var dir = look - pos;
            cam.transform.rotation = Quaternion.LookRotation(dir.sqrMagnitude > 1e-6f ? dir : Vector3.forward, Vector3.up) * Quaternion.Euler(0f, 0f, roll);
            cam.fieldOfView = fov;
            focusDistance = dir.magnitude; focalLength = focal; aperture = fstop;
        }

        /// <summary>
        /// Third-person camera collision for the scripted shots: the nearest visible actor (or the look point) is the anchor; a sphere
        /// cast from it to the wanted camera place stops in front of any wall / floor / roof in between, and the camera never goes
        /// below the ground. Shots far from every actor (plane, wide crane) only get the ground check.
        /// </summary>
        Vector3 KeepCameraClear(Vector3 pos, Vector3 look)
        {
            Vector3 anchor = look; float best = 14f; bool found = false;
            foreach (var a in all)
            {
                if (!a.go.activeSelf || a.airborne) continue;
                float dd = Vector3.Distance(a.Pos, pos);
                if (dd < best) { best = dd; anchor = a.Pos + Vector3.up * (a.isZombie ? 1.2f : 1.15f); found = true; }
            }
            if (!found && Vector3.Distance(look, pos) > 14f) anchor = pos;   // nothing near: nothing to protect
            var to = pos - anchor;
            float dist = to.magnitude;
            if (dist > 0.05f && Physics.SphereCast(anchor, 0.18f, to / dist, out var hit, dist, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider.attachedRigidbody == null)
                pos = anchor + to / dist * Mathf.Max(0.25f, hit.distance - 0.05f);
            float gy = GroundAt(pos, pos.y + 0.6f);
            if (pos.y < gy + 0.12f) pos.y = gy + 0.12f;
            return pos;
        }

        /// <summary>True when a cat-sized capsule standing at p overlaps scenery (walls, houses, rocks; the ground itself not).</summary>
        static bool Blocked(Vector3 p) =>
            Physics.CheckCapsule(p + Vector3.up * 0.5f, p + Vector3.up * 1.35f, 0.36f, ~0, QueryTriggerInteraction.Ignore);

        /// <summary>p, or the nearest free spot around it (rings up to 5 m) - so nobody is placed inside a house or wall.</summary>
        static Vector3 FreeSpot(Vector3 p)
        {
            if (!Blocked(p)) return p;
            for (float r = 0.6f; r <= 5f; r += 0.6f)
                for (int k = 0; k < 16; k++)
                {
                    float a = k * Mathf.PI / 8f;
                    var q = p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    q.y = GroundAt(q, p.y + 1.5f);
                    if (Mathf.Abs(q.y - p.y) < 1.2f && !Blocked(q)) return q;
                }
            return p;
        }

        /// <summary>A step that stops at walls and slides along them.</summary>
        static Vector3 Step(Vector3 p, Vector3 delta)
        {
            delta.y = 0f;
            float len = delta.magnitude;
            if (len < 1e-5f) return p;
            var o = p + Vector3.up * 0.9f;
            if (!Physics.SphereCast(o, 0.34f, delta / len, out var hit, len + 0.05f, ~0, QueryTriggerInteraction.Ignore)
                || hit.collider.attachedRigidbody != null) return p + delta;
            var n = hit.normal; n.y = 0f;
            if (n.sqrMagnitude < 1e-4f) return p;
            var slide = Vector3.ProjectOnPlane(delta, n.normalized);
            float sl = slide.magnitude;
            if (sl < 1e-5f || Physics.SphereCast(o, 0.34f, slide / sl, out _, sl + 0.05f, ~0, QueryTriggerInteraction.Ignore)) return p;
            return p + slide;
        }

        static float Ease(float x) => Smooth(x);

        /// <summary>Handheld wobble (film seconds): small, fast, never repeating.</summary>
        static Vector3 Shake(float f, float amount) =>
            new Vector3(Mathf.PerlinNoise(f * 3.1f, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, f * 2.7f) - 0.5f, Mathf.PerlinNoise(f * 2.3f, 5.1f) - 0.5f) * amount;

        void Add(float t, Action<float> shot) => shots.Add((t, shot));

        void BuildShots()
        {
            Stage T = town, Z = zone;
            Vector3 up = Vector3.up;

            // ---------------- DROP
            Add(0f, u =>          // A1: under the wing as the plane roars past
            {
                var p0 = PlanePos(0.7f) - planeDir * 2f + Vector3.Cross(up, planeDir) * 9f + up * -4f;
                Cam(p0 + Shake(film, 0.25f), plane.position, 58f, 24f, 16f, -6f + u * 4f);
            });
            Add(1.25f, u =>       // A2: riding beside the tail, the cat leaps off the ramp
            {
                var pos = plane.TransformPoint(new Vector3(-9f + 2f * u, -1.2f - 2.2f * u, -22f + 3f * u));
                var exit = plane.TransformPoint(DropShipVisual.ExitOffset);
                var look = hero.go.activeSelf ? Vector3.Lerp(exit, hero.Pos + Vector3.up, 0.75f) : exit;
                Cam(pos + Shake(film, 0.2f), look, 56f, 28f, 16f, -6f);
            });
            Add(2.4f, u =>        // A3: straight down over the falling cat, the town rushing up
            {
                var pos = hero.Pos + up * 3.2f - T.f * 1.6f + T.r * 0.5f;
                Cam(pos + Shake(film, 0.1f), hero.Pos + Vector3.down * 6f + T.f * 2f, 74f, 20f, 16f, Mathf.Sin(film * 2f) * 6f);
            });
            Add(3.6f, u =>        // A4: below, the canopy bursts open against the sky
            {
                float a = Mathf.Lerp(-30f, 30f, u) * Mathf.Deg2Rad;
                var pos = hero.Pos + (T.r * Mathf.Sin(a) + T.f * Mathf.Cos(a)) * 4.5f + Vector3.down * 3f;
                Cam(pos, hero.Pos + up * 2.2f, 56f, 30f, 11f);
            });
            Add(4.6f, u =>        // A5: low on the square, touch-down
            {
                var pos = W(T, 2.0f, 4.2f, 0.25f);
                Cam(pos, hero.Pos + up * 0.9f, 44f, 50f, 4f, 2f);
            });

            // ---------------- FIGHT
            Add(5.6f, u =>        // B1: low side tracking, sprinting across the square
            {
                var pos = hero.Pos + T.f * 2.6f + T.r * 0.9f + up * 0.45f;
                Cam(pos + Shake(film, 0.08f), hero.Pos + up * 1.0f + T.r * 1.2f, 50f, 35f, 4f, -5f);
            });
            Add(6.75f, u =>       // B2: behind the Knight's shoulder as he charges
            {
                var t = knight.go.transform;
                var pos = knight.Pos - t.forward * 2.0f + t.right * 0.7f + up * 1.3f;
                Cam(pos + Shake(film, 0.12f), knight.Pos + t.forward * 6f + up * 0.9f, 48f, 35f, 5.6f, 3f);
            });
            Add(7.65f, u =>       // B3: over the hero's shoulder, the burst
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.right * 0.7f + up * 1.6f - t.forward * 1.6f;
                Cam(pos + Shake(film, 0.06f), knight.Chest, 36f, 50f, 5.6f);
                focusDistance = Vector3.Distance(pos, knight.Chest);
            });
            Add(8.8f, u =>        // B4: in front of the Knight, shotgun right at the lens
            {
                var t = knight.go.transform;
                var pos = knight.Pos + t.forward * Mathf.Lerp(2.4f, 1.9f, u) - t.right * 0.6f + up * 0.9f;
                Cam(pos, knight.Chest + t.forward * 0.5f, 42f, 35f, 4f, 6f);
            });
            Add(9.6f, u =>        // B5: side medium, the bomb leaves the paw
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.right * -2.6f + t.forward * 1.2f + up * 1.1f;
                Cam(pos, hero.Pos + up * 1.3f + t.forward * 0.8f, 40f, 50f, 4f, -3f);
            });
            Add(10.45f, u =>      // B6: chase the bomb in slow motion, then the blast
            {
                var b = lastBomb != null ? lastBomb.transform.position : lastBombTarget;
                var side = Vector3.Cross(up, (lastBombTarget - hero.Pos).normalized);
                var pos = Vector3.Lerp(hero.Pos + up * 1.8f + side * 1.6f, lastBombTarget + side * 4.5f + up * 1.6f - (lastBombTarget - hero.Pos).normalized * 2.5f, Ease(u));
                Cam(pos, lastBomb != null ? b : lastBombTarget + up * 0.6f, 46f, 35f, 5.6f, 4f);
            });
            Add(12.2f, u =>       // B7: the Ninja's scope
            {
                var t = ninja.go.transform;
                var pos = ninja.Pos + t.right * 0.5f + up * 0.95f - t.forward * 1.1f;
                Cam(pos, hero.Chest, Mathf.Lerp(22f, 16f, u), 85f, 4f);
                focusDistance = Vector3.Distance(pos, hero.Chest);
            });
            Add(12.85f, u =>      // B8: bullet time - the round grazes the hero's head
            {
                var toN = ninja.Pos - hero.Pos; toN.y = 0f; toN.Normalize();
                var side = Vector3.Cross(up, toN);
                float a = Mathf.Lerp(-35f, 30f, Ease(u)) * Mathf.Deg2Rad;
                var pos = hero.Pos + up * 1.55f + (side * Mathf.Cos(a) - toN * Mathf.Sin(a)) * 1.5f - toN * 0.8f;
                Cam(pos, hero.Pos + up * 1.6f + toN * 3f, 48f, 50f, 2.8f);
                focusDistance = Vector3.Distance(pos, hero.Chest);
            });
            Add(14.4f, u =>       // B9: run-and-gun, camera racing in front, dutch angle
            {
                var t = hero.go.transform;
                var pos = hero.Pos + T.r * 3.0f + T.f * 1.4f + up * 0.7f;
                Cam(pos + Shake(film, 0.1f), hero.Chest, 46f, 35f, 4f, -9f);
            });
            Add(15.6f, u =>       // B10: wide, the square lit up by tracers, gold's bomb lands
            {
                var pos = Vector3.Lerp(W(T, -11f, -4f, 3.2f), W(T, -9f, -2f, 4.2f), Ease(u));
                Cam(pos + Shake(film, 0.12f), W(T, 0f, 12f, 1f), 52f, 24f, 11f, 2f);
            });
            Add(17.0f, u =>       // B11: telephoto over the shoulder, the long shot
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.right * 0.55f + up * 1.6f - t.forward * 1.4f;
                var target = ninja.Pos + up * 0.6f;
                Cam(pos, target, Mathf.Lerp(28f, 13f, Ease(Mathf.Clamp01(u * 2f))), 135f, 5.6f);
                focusDistance = Vector3.Distance(pos, target);
            });
            Add(18.6f, u =>       // B12: slow-motion orbit round the Ninja going down
            {
                var t = ninja.go.transform;
                float a = Mathf.Lerp(30f, 150f, Ease(u)) * Mathf.Deg2Rad;
                var pos = ninja.Pos + (t.right * Mathf.Cos(a) + t.forward * Mathf.Sin(a)) * 2.8f + up * Mathf.Lerp(1.2f, 0.6f, u);
                Cam(pos, ninja.Pos + up * 0.5f, 40f, 50f, 3.5f);
            });
            Add(19.6f, u =>       // B13: the storm wall behind them, camera backing away in front
            {
                var pos = hero.Pos + T.f * 5.2f + T.r * 0.7f + up * 0.6f;
                Cam(pos + Shake(film, 0.14f), (hero.Pos + soldier.Pos) * 0.5f + up * 1.4f, 52f, 35f, 5.6f, 4f);
            });
            Add(21.0f, u =>       // B14a: macro on the hero's muzzle
            {
                var m = hero.Muzzle(); var t = hero.go.transform;
                Cam(m + t.right * 0.45f + t.forward * 0.25f + up * 0.08f, m - t.forward * 0.25f, 32f, 85f, 2f, -4f);
            });
            Add(21.33f, u =>      // B14b: the soldier's face behind the gun
            {
                var t = soldier.go.transform;
                Cam(soldier.Pos + t.forward * 1.1f - t.right * 0.35f + up * 1.25f, soldier.Pos + up * 1.25f, 34f, 85f, 2f, 5f);
            });
            Add(21.66f, u =>      // B14c: low wide of the three firing
            {
                var pos = W(T, 0f, 4.5f, 0.3f);
                Cam(pos + Shake(film, 0.08f), hero.Pos + up * 1.0f, 60f, 20f, 8f, -3f);
            });

            // ---------------- HORDE
            Add(22.6f, u =>       // Z1: push in on the screaming zombie
            {
                var lead = zombies.Count > 0 ? zombies[0] : boss;
                if (lead == null) { Cam(W(Z, 0f, -3f, 1.6f), W(Z, 0f, 8f, 1f), 50f); return; }
                var t = lead.go.transform;
                var pos = lead.Pos + t.forward * Mathf.Lerp(2.6f, 1.5f, Ease(u)) + up * 1.0f;
                Cam(pos + Shake(film, 0.06f), lead.Pos + up * 1.35f, 40f, 50f, 2.8f);
            });
            Add(23.8f, u =>       // Z2: ground level, the horde charges at the lens
            {
                var pos = W(Z, 0.6f, 4f, 0.22f);
                Cam(pos + Shake(film, 0.1f), W(Z, 0f, 14f, 1.2f), 62f, 20f, 8f, 3f);
            });
            Add(25.25f, u =>      // Z3: orbit round the back-to-back squad
            {
                float a = Mathf.Lerp(-60f, 70f, Ease(u)) * Mathf.Deg2Rad;
                var pos = Z.c + (Z.f * Mathf.Cos(a) + Z.r * Mathf.Sin(a)) * 3.6f + up * 1.6f;
                pos.y = W(Z, 0f, 0f).y + 1.6f;
                Cam(pos + Shake(film, 0.08f), W(Z, 0f, 0f, 1.05f), 50f, 35f, 4f);
            });
            Add(26.8f, u =>       // Z4: bomb into the horde, slow-motion blast from low behind
            {
                var pos = W(Z, -1.6f, -2.2f, 0.9f);
                var look = lastBomb != null ? lastBomb.transform.position : lastBombTarget + up;
                Cam(pos + Shake(film, 0.05f), Vector3.Lerp(hero.Pos + up * 1.4f + Z.f * 3f, look, Ease(u * 1.4f)), 50f, 35f, 8f, -2f);
            });
            Add(28.6f, u =>       // Z5: the boss roars, low angle
            {
                if (boss == null) { Cam(W(Z, 0f, 2f, 0.5f), W(Z, 0f, 9f, 2f), 50f); return; }
                var pos = boss.Pos + boss.go.transform.forward * 3.2f + up * 0.35f;
                Cam(pos + Shake(film, 0.12f), boss.Pos + up * 2.4f, 48f, 35f, 4f, -4f);
            });
            Add(29.6f, u =>       // Z6: over the squad as everyone unloads on the boss, then its fall in slow motion
            {
                var pos = hero.Pos - Z.f * 2.4f + Z.r * 1.2f + up * 1.7f;
                var look = boss != null ? boss.Pos + up * 1.8f : W(Z, 0f, 6f, 1.5f);
                Cam(pos + Shake(film, 0.08f), look, Mathf.Lerp(46f, 38f, u), 50f, 5.6f);
            });
            Add(31.0f, u =>       // Z7 + title: crane up, the new wave closing in from all sides
            {
                float e = Ease(Mathf.Clamp01(u / 0.55f));
                var pos = Z.c - Z.f * Mathf.Lerp(3f, 9f, e) + Z.r * Mathf.Lerp(1.5f, 3f, e) + up * Mathf.Lerp(1.6f, 16f, e);
                pos.y = W(Z, 0f, 0f).y + Mathf.Lerp(1.6f, 16f, e);
                Cam(pos, W(Z, 0f, 0f, 0.9f), Mathf.Lerp(50f, 44f, e), 24f, 11f, Mathf.Lerp(-3f, 0f, e));
            });
            shots.Sort((a, b) => a.t.CompareTo(b.t));
        }

        void Shot(float f)
        {
            int i = shots.Count - 1;
            while (i > 0 && shots[i].t > f) i--;
            float start = shots[i].t, end = i + 1 < shots.Count ? shots[i + 1].t : Duration;
            shots[i].shot(Mathf.Clamp01((f - start) / Mathf.Max(0.01f, end - start)));
        }

        // ================================================================== shooting
        void Shoot(Actor from, Actor target, bool hit) => Shoot(from, target.Chest, hit, false, target);

        void Shoot(Actor from, Vector3 aim, bool hit, bool nearMiss, Actor target = null)
        {
            if (!from.go.activeSelf) return;
            var w = Weapons.Get(from.weapon);
            var muzzle = from.Muzzle();
            from.anim.SetTrigger("Fire");
            from.fx = from.fx != null ? from.fx : MuzzleFlash.For(from.go);
            float size = w.pellets > 1 ? 1.45f : w.fireRate < 2f ? 1.35f : w.damage < 16f ? 0.8f : 1f;
            float pitch = Mathf.Lerp(0.55f, 1f, Mathf.Sqrt(Mathf.Max(0.05f, Time.timeScale)));
            Cue(GameAudio.GunSound(w), 1f, pitch);
            if (UnityEngine.Random.value < 0.5f) Cue("shell", 0.3f, pitch, 0f, 0.35f);
            if (from.fx != null) from.fx.Play(muzzle, aim - muzzle, size, from.go.transform.right);
            CameraFx.Kick(0.12f, UnityEngine.Random.Range(-0.08f, 0.08f));   // the shot camera jolts a little with every round

            int pellets = Mathf.Max(1, w.pellets);
            for (int i = 0; i < pellets; i++)
            {
                var dir = (aim - muzzle).normalized;
                if (pellets > 1) dir = Quaternion.Euler(UnityEngine.Random.Range(-w.spreadDegrees, w.spreadDegrees), UnityEngine.Random.Range(-w.spreadDegrees, w.spreadDegrees), 0f) * dir;
                Vector3 end, normal; int surface; bool landed;
                if (hit && target != null && Physics.Linecast(muzzle, aim, out var wall, ~0, QueryTriggerInteraction.Ignore)
                    && wall.collider.attachedRigidbody == null)
                {
                    // a wall in between: the round stops there (no hits through houses)
                    end = wall.point; normal = wall.normal; surface = MuzzleFlash.SurfaceOf(wall.collider); landed = true;
                }
                else if (hit && target != null)
                {
                    end = aim + UnityEngine.Random.insideUnitSphere * 0.12f; normal = -dir; surface = MuzzleFlash.Flesh; landed = true;
                }
                else
                {
                    var far = muzzle + dir * 160f;
                    if (!nearMiss && pellets == 1) far = muzzle + ((aim + new Vector3(UnityEngine.Random.Range(-1.6f, 1.6f), -1.4f, UnityEngine.Random.Range(-1.6f, 1.6f))) - muzzle).normalized * 160f;
                    if (Physics.Raycast(muzzle, (far - muzzle).normalized, out var h, 160f, ~0, QueryTriggerInteraction.Ignore))
                    { end = h.point; normal = h.normal; surface = MuzzleFlash.SurfaceOf(h.collider); landed = true; }
                    else { end = far; normal = Vector3.zero; surface = 0; landed = false; }
                }
                if (from.fx != null) from.fx.Tracer(muzzle, end, w.color, landed, normal, surface);
                if (landed && i < 3)
                {
                    float travel = Vector3.Distance(muzzle, end) / 140f;
                    var t = target; bool flesh = surface == MuzzleFlash.Flesh; int s = surface;
                    Delay(travel, () =>
                    {
                        float p2 = Mathf.Lerp(0.55f, 1f, Mathf.Sqrt(Mathf.Max(0.05f, Time.timeScale)));
                        Cue(GameAudio.ImpactSound(s), flesh ? 0.8f : 0.5f, p2);
                        if (flesh && t != null)
                        {
                            t.Flash(); t.Stagger(from.Pos);
                            if (!t.isZombie) t.anim.SetTrigger("Hit");
                            Cue(t.isZombie ? "zombie_hit" : "hitmarker", 0.5f, p2);
                        }
                    });
                }
            }
            if (nearMiss) Cue("banner_whoosh", 0.9f, 0.6f, 0f, 0.15f);
        }

        // ================================================================== sound cues (mixed by Tools/Cinematic/make_trailer.py)
        void Cue(string clip, float volume, float pitch = 1f, float loopSeconds = 0f, float delay = 0f)
        {
            if (cueCount++ > 0) cues.Append(",\n");
            cues.Append("  {\"t\": ").Append((film + delay).ToString("0.000", CultureInfo.InvariantCulture))
                .Append(", \"clip\": \"").Append(clip).Append("\", \"vol\": ").Append(volume.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(", \"pitch\": ").Append(pitch.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(", \"loop\": ").Append(loopSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append("}");
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
                File.WriteAllBytes(Path.Combine(outDir, "frame_" + frame.ToString("00000") + ".jpg"), readback.EncodeToJPG(jpgQuality));
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
                Debug.Log("[Trailer] done: " + frame + " frames in " + outDir + "  ->  python Tools/Cinematic/make_trailer.py \"" + outDir + "\"");
            }
#if UNITY_EDITOR
            if (record) UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        void OnGUI()
        {
            if (rt == null) return;
            float aspect = width / (float)height;
            float w = Screen.width, h = Screen.width / aspect;
            if (h > Screen.height) { h = Screen.height; w = h * aspect; }
            GUI.DrawTexture(new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h), rt, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(10, 10, 600, 24), (record ? "REC " : "") + film.ToString("0.00") + " / " + Duration + " s   frame " + frame);
        }

        // ================================================================== actor (cats and zombies)
        class Actor
        {
            readonly ActionTrailer d;
            public readonly string id;
            public readonly GameObject go;
            public readonly Animator anim;
            public readonly bool isZombie;
            public MuzzleFlash fx;
            public int weapon, hits;
            public bool dead;
            readonly Renderer rifle;
            readonly Renderer[] renderers;
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            float flashAt = -10f, scale = 1f;

            Vector3 moveTarget; bool moving; float moveSpeed;
            Vector3 moveDir; float moveUntil = -1f;
            public Actor face;
            public Vector3? faceAt;
            public bool airborne; public Vector3 airVel;
            Vector3 fling; float flingUntil = -1f;
            Vector3 lastPos; bool first = true;
            Vector3 strafeDir; float strafeUntil = -1f, nextStrafe;     // little side steps while shooting (less statue-like)
            float staggerUntil = -1f; Vector3 staggerDir;              // pushed back a bit when hit
            float hopStart = -10f, hopHeight;

            public bool firing; Actor fireTarget; float fireUntil, fireInterval, hitChance, nextShot;

            public Actor(ActionTrailer director, string id, GameObject model, RuntimeAnimatorController controller, bool zombie)
            {
                d = director; this.id = id; isZombie = zombie;
                go = Instantiate(model);
                go.name = "Trailer_" + id;
                anim = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
                if (controller != null) anim.runtimeAnimatorController = controller;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
                var list = new List<Renderer>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer) continue;
                    list.Add(r);
                    if (!zombie && rifle == null && r.name.ToLowerInvariant().Contains("rifl")) rifle = r;
                    if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                }
                renderers = list.ToArray();
                if (!zombie && go.GetComponent<FootFx>() == null) go.AddComponent<FootFx>();
            }

            public Vector3 Pos => go.transform.position;
            public Vector3 Chest => go.transform.position + Vector3.up * (isZombie ? 1.15f * scale : anim.GetBool("Crouching") ? 0.72f : 1.08f);

            public void SetScale(float s) { scale = s; go.transform.localScale = Vector3.one * s; }

            public void Show(bool on)
            {
                if (go.activeSelf == on) return;
                go.SetActive(on);
                if (on) { if (!isZombie) anim.SetBool("Grounded", true); first = true; }
            }

            public void Teleport(Vector3 p, float yaw)
            {
                if (!airborne && p.y < d.town.c.y + 20f && p.y < d.zone.c.y + 20f) p = FreeSpot(p);   // never inside a house / wall
                go.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, yaw, 0f)); first = true; flingUntil = -1f; strafeUntil = -1f;
            }
            public void MoveTo(Vector3 target, float speed) { moveTarget = FreeSpot(target); moving = true; moveSpeed = speed; moveUntil = -1f; }
            public void Hop(float h) { hopStart = d.game; hopHeight = h; if (!isZombie) anim.SetTrigger("Jump"); }
            public void Stagger(Vector3 from)
            {
                if (dead) return;
                staggerDir = Pos - from; staggerDir.y = 0f;
                staggerDir = staggerDir.sqrMagnitude > 0.01f ? staggerDir.normalized : -go.transform.forward;
                staggerUntil = d.game + (isZombie ? 0.16f : 0.12f);
            }
            public void MoveDir(Vector3 dir, float speed, float untilFilm) { dir.y = 0f; moveDir = dir.normalized; moveSpeed = speed; moveUntil = untilFilm; moving = false; }
            public void StopMoving() { moving = false; moveUntil = -1f; }
            public void Fling(Vector3 v) { fling = v; flingUntil = d.game + 0.6f; }

            public void StartFire(Actor target, float untilFilm, float rate, float hits)
            {
                if (target == null) return;
                firing = true; fireTarget = target; fireUntil = untilFilm; fireInterval = 1f / rate; hitChance = hits; nextShot = d.game;
                face = target; faceAt = null;
            }

            public void Die(bool fromFront)
            {
                dead = true; firing = false; StopMoving();
                if (isZombie) { anim.SetInteger("DeathIndex", UnityEngine.Random.Range(0, 2)); anim.SetBool("Dead", true); }
                else { anim.SetBool("DeathBack", fromFront); anim.SetBool("Dead", true); }
            }

            public void Revive()
            {
                hits = 0;
                if (!dead && !anim.GetBool("Dead")) return;
                dead = false;
                anim.SetBool("Dead", false);
                anim.Rebind(); anim.Update(0f);
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
                    if (flingUntil > d.game) { float y = p.y; p = Step(p, fling * dt); p.y = y + fling.y * dt; fling.y -= 12f * dt; }
                    else if (!dead && staggerUntil > d.game) p = Step(p, staggerDir * (isZombie ? 2.2f : 1.6f) * dt);
                    else if (!dead && moving)
                    {
                        var to = moveTarget - p; to.y = 0f;
                        float dist = to.magnitude;
                        if (dist < 0.08f)
                        {
                            moving = false;
                            if (isZombie) { anim.SetTrigger("Attack"); d.Cue("zombie_attack", 0.35f, UnityEngine.Random.Range(0.9f, 1.2f)); }
                        }
                        else p = Step(p, to / dist * Mathf.Min(moveSpeed, dist * 3f) * dt);
                    }
                    else if (!dead && moveUntil > 0f)
                    {
                        if (d.film < moveUntil) p = Step(p, moveDir * moveSpeed * dt); else moveUntil = -1f;
                    }
                    else if (!dead && !isZombie && firing)
                    {
                        // shooters do not stand like statues: short side steps / small back-steps between bursts
                        if (d.game >= nextStrafe)
                        {
                            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                            strafeDir = (t.right * side + (UnityEngine.Random.value < 0.3f ? -t.forward * 0.6f : Vector3.zero)).normalized;
                            strafeUntil = d.game + UnityEngine.Random.Range(0.35f, 0.7f);
                            nextStrafe = strafeUntil + UnityEngine.Random.Range(0.25f, 0.8f);
                        }
                        if (d.game < strafeUntil) p = Step(p, strafeDir * 1.25f * dt);
                    }
                    float gy = GroundAt(p, p.y + 1.2f);
                    float ht = d.game - hopStart;
                    float hop = ht >= 0f && ht < 0.7f ? Mathf.Sin(ht / 0.7f * Mathf.PI) * hopHeight : 0f;
                    p.y = flingUntil > d.game ? Mathf.Max(gy, p.y) : gy + hop;
                    t.position = p;

                    Vector3 look = Vector3.zero;
                    if (face != null && face.go.activeSelf) look = face.Pos - p;
                    else if (faceAt.HasValue) look = faceAt.Value - p;
                    else if (moving) look = moveTarget - p;
                    else if (moveUntil > 0f) look = moveDir;
                    look.y = 0f;
                    if (!dead && look.sqrMagnitude > 0.01f)   // eased turn: fast start, soft finish (no robotic snap)
                        t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(look), 1f - Mathf.Exp(-(isZombie ? 7f : 11f) * dt));
                }

                if (dt > 0f)
                {
                    var v = first ? Vector3.zero : (t.position - lastPos) / dt;
                    first = false;
                    lastPos = t.position;
                    if (airborne || v.sqrMagnitude > 400f || flingUntil > d.game) v = Vector3.zero;
                    if (isZombie)
                    {
                        float sp = new Vector2(v.x, v.z).magnitude / scale;
                        bool run = sp > 0.15f;
                        anim.SetFloat("Gait", run ? 2f : 0f, 0.1f, dt);
                        anim.SetFloat("MoveRate", run ? Mathf.Clamp(sp / ZombieAnimator.RunClipSpeed, 0.4f, 4.5f) : 1f);
                    }
                    else
                    {
                        var local = t.InverseTransformDirection(new Vector3(v.x, 0f, v.z)) / WalkSpeed;
                        anim.SetFloat("MoveX", local.x, 0.1f, dt);
                        anim.SetFloat("MoveY", local.z, 0.1f, dt);
                        anim.SetFloat("MoveSpeed", new Vector2(local.x, local.z).magnitude, 0.1f, dt);
                        int upper = anim.GetLayerIndex("UpperBody");
                        if (upper >= 0) anim.SetLayerWeight(upper, Mathf.MoveTowards(anim.GetLayerWeight(upper), dead ? 0f : 1f, dt * 8f));
                    }
                }

                if (firing && !dead)
                {
                    if (d.film >= fireUntil || fireTarget == null || !fireTarget.go.activeSelf) firing = false;
                    else if (d.game >= nextShot)
                    {
                        nextShot = d.game + fireInterval * UnityEngine.Random.Range(0.9f, 1.15f);
                        d.Shoot(this, fireTarget, UnityEngine.Random.value < hitChance);
                    }
                }

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
