using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CatBattleground
{
    /// <summary>
    /// 34-second GAMEPLAY ACTION film (2026-11-01, user: firefights + grenades, a headshot, a sniper on a rooftop, fast cuts; the
    /// cats must not look stiff and neither they nor the camera may go through walls). Battle royale town only.
    /// Scene: CatBattleground > Cinematic > Build Gameplay Action Scene (copy of the Phase 2 arena).
    ///
    ///   0-6.4    COVER     Army + Soldier sprint into cover under fire, Army peeks out, HEADSHOT on Black (slow motion)
    ///   6.4-10.9 GRENADE   Knight charges the Soldier with the shotgun, Army throws a bomb, slow-motion blast
    ///   10.9-16  SNIPER    rooftop reveal, scope view, bullet cam all the way to Gold's head (bullet time), HEADSHOT
    ///   16-21.2  ROOF      the sniper pins Army down, Soldier suppresses, Army lobs a bomb onto the roof
    ///   21.2-31  WAVE 2    fast cuts: White and Baby push in, reload, run-and-gun, Baby's bomb, dive, last kill
    ///   31-34    END       crane up over the square (titles / music / SFX: Tools/Cinematic/make_gameplay_action.py)
    ///
    /// Less stiff than ActionTrailer: velocity with acceleration (no instant starts / stops), NavMesh paths around houses, step
    /// shuffles when turning on the spot, the spine bends to the aim (up to the roof, down from it), recoil kicks, hit flinches,
    /// the head snapping back on a headshot, the bomb visible in the paw before the throw, cats keep apart from each other.
    /// No wall clipping: every placement goes through FreeSpot, every step is a sphere sweep that slides along walls, camera
    /// positions are swept from the shot's subject (smoothly released), pushed out of scenery and out of the cats' bodies.
    ///
    /// Recording like ActionTrailer: Time.captureFramerate, JPG frames + cues.json in &lt;project&gt;/Recordings/Gameplay_&lt;date&gt;.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class GameplayAction : MonoBehaviour
    {
        [Serializable]
        public class CastMember
        {
            public string id;
            public GameObject model;
            public RuntimeAnimatorController controller;
        }

        [SerializeField] public CastMember[] cast = new CastMember[0];
        [SerializeField] public bool record = true;
        [SerializeField] public int width = 1920, height = 1080, fps = 30;
        [Range(50, 100)] [SerializeField] public int jpgQuality = 95;
        [Tooltip("Preview from this second (recordings always start at 0).")]
        [SerializeField] public float previewStart = 0f;

        public const float Duration = 34f;
        const float WalkSpeed = 1.4f, Sprint = 4f, Jog = 2.4f;
        const float BulletSpeed = 140f;   // MuzzleFlash.TracerSpeed

        // ------------------------------------------------------------------ state
        Camera cam;
        RenderTexture rt;
        Texture2D readback;
        DepthOfField dof;
        string outDir;
        int frame;
        float film, game;
        bool finished;
        readonly StringBuilder cues = new StringBuilder();
        int cueCount;

        class Stage { public Vector3 c, f, r; }
        Stage town, fight;   // fight: origin = Army's cover spot, f = towards the enemies
        Vector3 cover, peek, soldierSpot, roof, roofOut, finalSpot;
        Vector3 blackSpot, knightStart, goldFrom, goldTo, whiteSpot, babySpot;
        bool hasWall, hasRoof;

        Actor hero, soldier, knight, black, gold, ninja, white, baby;
        readonly List<Actor> all = new List<Actor>();
        readonly Dictionary<string, Actor> cats = new Dictionary<string, Actor>();

        float focusDistance = 10f, focalLength = 50f, aperture = 8f;
        Transform glint;

        readonly List<(float t, Action a)> events = new List<(float, Action)>();
        int nextEvent;
        readonly List<(float g, Action a)> pending = new List<(float, Action)>();
        readonly List<(float t, Action a)> filmPending = new List<(float, Action)>();
        readonly List<(float t, Action<float> shot)> shots = new List<(float, Action<float>)>();
        int shotIndex = -1;
        ulong bombIds = 9000;

        // the sniper round (bullet cam)
        Vector3 bulletFrom, bulletDir; float bulletG0 = -1f, bulletTravel = 0.2f, bulletScale = 0.12f;
        Vector3 diveDir;

        // ================================================================== setup
        bool hadBackfaces;

        void Start()
        {
            // the town meshes are single-sided MeshColliders: without this, rays / sweeps from behind a wall pass through it
            hadBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            cam = Camera.main;
            if (cam == null) { cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>(); cam.tag = "MainCamera"; }
            var tpc = cam.GetComponent<ThirdPersonCamera>(); if (tpc != null) tpc.enabled = false;
            cam.nearClipPlane = 0.05f; cam.farClipPlane = 900f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;

            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "GameplayActionRT" };
            rt.Create();
            cam.targetTexture = rt;
            readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            BuildVolume();
            BuildCast();
            if (!enabled) return;
            FindStages();
            BuildGlint();
            BuildTimeline();
            BuildShots();

            if (record)
            {
                previewStart = 0f;
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
                var args = Environment.GetCommandLineArgs();   // standalone recorder: -gameplayOut <folder>
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-gameplayOut") root = args[i + 1];
                outDir = Path.Combine(root, "Gameplay_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(outDir);
                Time.captureFramerate = fps;
                AudioListener.volume = 0f;   // the soundtrack is mixed afterwards from cues.json
                Debug.Log("[Gameplay] recording " + width + "x" + height + " @" + fps + " to " + outDir);
                StartCoroutine(Capture());
            }
            film = Mathf.Clamp(previewStart, 0f, Duration);
            while (nextEvent < events.Count && events[nextEvent].t <= film) { events[nextEvent].a(); nextEvent++; }
        }

        void OnDestroy()
        {
            Physics.queriesHitBackfaces = hadBackfaces;
            Time.captureFramerate = 0;
            Time.timeScale = 1f;
            AudioListener.volume = 1f;
            if (cam != null) cam.targetTexture = null;
            if (rt != null) rt.Release();
        }

        // ------------------------------------------------------------------ cast
        void BuildCast()
        {
            foreach (var m in cast)
            {
                if (m == null || m.model == null || string.IsNullOrEmpty(m.id)) continue;
                var a = new Actor(this, m.id, m.model, m.controller);
                a.weapon = Weapons.ForCharacter(m.id);
                cats[m.id] = a; all.Add(a);
            }
            Actor Pick(string id) => cats.TryGetValue(id, out var a) ? a : null;
            hero = Pick("army");
            if (hero == null) foreach (var a in cats.Values) { hero = a; break; }
            if (hero == null) { Debug.LogError("[Gameplay] no cast (run CatBattleground > Cinematic > Build Gameplay Action Scene)"); enabled = false; return; }
            soldier = Pick("soldier") ?? hero; knight = Pick("knight") ?? soldier; black = Pick("black") ?? knight;
            gold = Pick("gold") ?? black; ninja = Pick("ninja") ?? gold; white = Pick("white") ?? black; baby = Pick("baby") ?? gold;
            foreach (var a in all) a.Show(false);
        }

        // ------------------------------------------------------------------ places: square, cover wall, rooftop
        void FindStages()
        {
            // the middle of the biggest cluster of BattlefieldTown houses = a town's open square (ArenaBuilder.PlanTown)
            var houses = new List<Vector3>();
            var b = GameObject.Find("Arena/Buildings");
            if (b != null)
                foreach (Transform c in b.transform)
                    if (c.name.StartsWith("SM_House", StringComparison.OrdinalIgnoreCase)) houses.Add(c.position);
            if (houses.Count == 0 && b != null) foreach (Transform c in b.transform) houses.Add(c.position);
            Vector3 centre = Vector3.zero; int best = -1;
            foreach (var h in houses)
            {
                if (h.z > 600f) continue;   // not the zombie town (ArenaBuilder.ZombieCentre z 900)
                int n = 0; var sum = Vector3.zero;
                foreach (var o in houses) if (Flat(o - h) < 45f) { n++; sum += o; }
                if (n > best) { best = n; centre = sum / n; }
            }
            town = MakeStage(OpenGround(centre), 400f);

            FindCover();
            fight = new Stage { c = cover, f = town.f, r = town.r };
            soldierSpot = FreeSpot(W(fight, -1.6f, -1.4f));
            finalSpot = FreeSpot(W(fight, 0.5f, 4.5f));

            var peekEye = peek + Vector3.up * 1.25f;
            blackSpot = ViewSpot(peekEye, W(fight, 2.5f, 15f), 6f);
            whiteSpot = ViewSpot(peekEye, W(fight, 4f, 17f), 6f);
            babySpot = ViewSpot(peekEye, W(fight, -3f, 18f), 6f);
            knightStart = FreeSpot(W(fight, -3f, 10f));

            hasRoof = FindRoof(W(fight, 0f, 9f), out roof);
            if (!hasRoof) roof = FreeSpot(W(fight, 9f, 24f));   // no usable roof: the sniper lies on the far side of the square
            roofOut = W(fight, 0f, 9f) - roof; roofOut.y = 0f; roofOut = roofOut.sqrMagnitude > 0.01f ? roofOut.normalized : -fight.f;

            // Gold runs across the square on a line the sniper can see
            var eye = roof + Vector3.up * 1.35f;
            goldFrom = W(fight, 7f, 11f); goldTo = W(fight, -6f, 10f);
            foreach (float z in new[] { 11f, 8f, 14f, 5f, 17f })
            {
                var a = FreeSpot(W(fight, 7f, z)); var c = FreeSpot(W(fight, -6f, z));
                bool clear = true;
                for (int i = 0; i <= 4 && clear; i++)
                    clear = !Physics.Linecast(eye, Vector3.Lerp(a, c, i / 4f) + Vector3.up * 1.3f, ~0, QueryTriggerInteraction.Ignore);
                if (clear) { goldFrom = a; goldTo = c; break; }
            }
            Debug.Log($"[Gameplay] square {town.c}, cover {cover} (wall {hasWall}), peek {peek}, roof {roof} (roof {hasRoof})");
        }

        /// <summary>An open spot near p and the direction with the longest clear view (the fight runs along it).</summary>
        Stage MakeStage(Vector3 p, float rayTop)
        {
            p.y = TerrainY(p);
            Vector3 bestP = p; float bestOpen = -1f;
            for (float r = 0f; r <= 10f; r += 2f)
                for (float a = 0f; a < 360f; a += 45f)
                {
                    var q = p + new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad)) * r;
                    q.y = TerrainY(q);
                    if (Blocked(q) || Physics.Raycast(q + Vector3.up * 0.5f, Vector3.up, 60f, ~0, QueryTriggerInteraction.Ignore)) continue;
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

        /// <summary>
        /// The most open outdoor ground within 45 m of the town centre (the first recording picked the middle of the house cluster,
        /// which with TownScale 1.7 is a cramped yard between ruins): open sky above, average free distance all round at chest height.
        /// </summary>
        static Vector3 OpenGround(Vector3 centre)
        {
            float baseY = TerrainY(centre);
            Vector3 best = centre; best.y = baseY; float bestScore = -1f;
            for (float x = -45f; x <= 45f; x += 3f)
                for (float z = -45f; z <= 45f; z += 3f)
                {
                    if (x * x + z * z > 45f * 45f) continue;
                    var p = centre + new Vector3(x, 0f, z);
                    p.y = TerrainY(p);
                    if (Mathf.Abs(p.y - baseY) > 3f || Blocked(p)) continue;
                    if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.up, 60f, ~0, QueryTriggerInteraction.Ignore)) continue;   // under a roof
                    float sum = 0f, min = 25f;
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k * Mathf.PI / 8f;
                        float c = Clearance(p, new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)), 25f);
                        sum += c; min = Mathf.Min(min, c);
                    }
                    if (min < 4f) continue;
                    float score = sum / 16f - Flat(p - centre) * 0.05f;
                    if (score > bestScore) { bestScore = score; best = p; }
                }
            return best;
        }

        /// <summary>A town wall or house near the square with a spot behind it (seen from the enemy side) and a peek spot beside it.</summary>
        void FindCover()
        {
            var f = town.f; var side = town.r;
            float best = 1e9f;
            hasWall = false;
            var arena = GameObject.Find("Arena");
            if (arena != null)
                foreach (var tr in arena.GetComponentsInChildren<Transform>())
                {
                    if (!tr.name.StartsWith("SM_Wall", StringComparison.OrdinalIgnoreCase) && !tr.name.StartsWith("SM_House", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Flat(tr.position - town.c) > 18f) continue;
                    var cols = tr.GetComponentsInChildren<Collider>();
                    if (cols.Length == 0) continue;
                    var bb = cols[0].bounds; foreach (var c in cols) bb.Encapsulate(c.bounds);
                    var mid = new Vector3(bb.center.x, town.c.y, bb.center.z);
                    float ext = Mathf.Max(bb.extents.x, bb.extents.z);
                    float gy = GroundAt(mid - f * (ext + 2f), town.c.y + 3f);
                    var origin = new Vector3(mid.x, gy + 1.0f, mid.z) - f * (ext + 3f);
                    if (!Physics.Raycast(origin, f, out var hit, ext + 5f, ~0, QueryTriggerInteraction.Ignore) || !hit.collider.transform.IsChildOf(tr)) continue;
                    var s = hit.point - f * 0.75f; s.y = GroundAt(s, gy + 2f);
                    if (Blocked(s) || Mathf.Abs(s.y - town.c.y) > 1.5f) continue;
                    bool ok = false; Vector3 pk = s;
                    foreach (float k in new[] { 1.4f, -1.4f, 2f, -2f, 2.6f, -2.6f, 3.2f, -3.2f })
                    {
                        var q = s + side * k; q.y = GroundAt(q, s.y + 2f);
                        if (Mathf.Abs(q.y - s.y) > 0.6f || Blocked(q)) continue;
                        var step = q - s; step.y = 0f;
                        if (Physics.SphereCast(s + Vector3.up * 0.9f, 0.3f, step.normalized, out _, step.magnitude, ~0, QueryTriggerInteraction.Ignore)) continue;
                        if (Physics.Linecast(q + Vector3.up * 1.2f, q + f * 14f + Vector3.up * 1.1f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        pk = q; ok = true; break;
                    }
                    if (!ok) continue;
                    float score = Flat(s - town.c);
                    if (score < best) { best = score; cover = s; peek = pk; hasWall = true; }
                }
            if (!hasWall) { cover = FreeSpot(W(town, -1.5f, -2f)); peek = FreeSpot(W(town, 0.3f, -2f)); }
        }

        /// <summary>A flat roof spot of a town house 12-45 m from target, with room to stand and a clear view of target.</summary>
        bool FindRoof(Vector3 target, out Vector3 spot)
        {
            spot = target; float bestScore = -1e9f; bool found = false;
            var b = GameObject.Find("Arena/Buildings");
            if (b == null) return false;
            foreach (Transform house in b.transform)
            {
                if (!house.name.StartsWith("SM_House", StringComparison.OrdinalIgnoreCase)) continue;
                float dist = Flat(house.position - target);
                if (dist < 12f || dist > 45f) continue;
                var cols = house.GetComponentsInChildren<Collider>();
                if (cols.Length == 0) continue;
                var bb = cols[0].bounds; foreach (var c in cols) bb.Encapsulate(c.bounds);
                const float step = 0.8f;
                int nx = Mathf.FloorToInt((bb.size.x - 1.6f) / step), nz = Mathf.FloorToInt((bb.size.z - 1.6f) / step);
                if (nx < 3 || nz < 3) continue;
                var h = new float[nx + 1, nz + 1];
                for (int i = 0; i <= nx; i++)
                    for (int j = 0; j <= nz; j++)
                    {
                        h[i, j] = float.NaN;
                        var top = new Vector3(bb.min.x + 0.8f + i * step, bb.max.y + 2f, bb.min.z + 0.8f + j * step);
                        if (Physics.Raycast(top, Vector3.down, out var hit, bb.size.y + 4f, ~0, QueryTriggerInteraction.Ignore)
                            && hit.normal.y > 0.95f && hit.collider.transform.IsChildOf(house) && hit.point.y > target.y + 3f)
                            h[i, j] = hit.point.y;
                    }
                for (int i = 1; i < nx; i++)
                    for (int j = 1; j < nz; j++)
                    {
                        float y = h[i, j];
                        if (float.IsNaN(y)) continue;
                        // a floor, not the top of a parapet: the neighbours are at the same height
                        bool flat = true;
                        foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            if (float.IsNaN(h[i + di, j + dj]) || Mathf.Abs(h[i + di, j + dj] - y) > 0.15f) flat = false;
                        if (!flat) continue;
                        var p = new Vector3(bb.min.x + 0.8f + i * step, y, bb.min.z + 0.8f + j * step);
                        if (Blocked(p)) continue;
                        if (Physics.Linecast(p + Vector3.up * 1.35f, target + Vector3.up * 1.2f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        float score = (y - target.y) * 0.4f - Mathf.Abs(dist - 25f) * 0.15f - Flat(p - target) * 0.3f;
                        if (score > bestScore) { bestScore = score; spot = p; found = true; }
                    }
            }
            return found;
        }

        /// <summary>A free spot near <paramref name="around"/> that the eye can see (chest height), else just a free spot.</summary>
        static Vector3 ViewSpot(Vector3 eye, Vector3 around, float search)
        {
            for (float r = 0f; r <= search; r += 1.5f)
                for (int k = 0; k < 8; k++)
                {
                    float a = k * Mathf.PI / 4f;
                    var q = around + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    q.y = GroundAt(q, around.y + 3f);
                    if (!Blocked(q) && !Physics.Linecast(eye, q + Vector3.up * 1.3f, ~0, QueryTriggerInteraction.Ignore)) return q;
                    if (r == 0f) break;
                }
            return FreeSpot(around);
        }

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

        static Collider terrain;

        /// <summary>Height of the island's ground mesh (Arena/Terrain) under p - never a roof (the first recordings measured the
        /// town square from 400 m up, landed on a house roof and staged the whole fight at roof height).</summary>
        static float TerrainY(Vector3 p)
        {
            if (terrain == null) { var t = GameObject.Find("Arena/Terrain"); if (t != null) terrain = t.GetComponent<Collider>(); }
            if (terrain != null && terrain.Raycast(new Ray(new Vector3(p.x, 600f, p.z), Vector3.down), out var hit, 1200f)) return hit.point.y;
            return GroundAt(p, 400f);
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        static Vector3 FlatDir(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

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

        /// <summary>True when a cat-sized capsule standing at p overlaps scenery (walls, houses, rocks; the ground itself not).</summary>
        static bool Blocked(Vector3 p) =>
            Physics.CheckCapsule(p + Vector3.up * 0.5f, p + Vector3.up * 1.35f, 0.36f, ~0, QueryTriggerInteraction.Ignore);

        /// <summary>p, or the nearest free spot around it (rings up to 5 m) - nobody is placed inside a house or wall.</summary>
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

        // ------------------------------------------------------------------ post FX
        void BuildVolume()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            dof = profile.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            dof.focusDistance.Override(10f); dof.focalLength.Override(50f); dof.aperture.Override(8f);
            var mb = profile.Add<MotionBlur>(true);
            mb.intensity.Override(0.4f); mb.quality.Override(MotionBlurQuality.High);
            var grain = profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1); grain.intensity.Override(0.12f);
            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.12f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.28f); vignette.smoothness.Override(0.5f);
            var grade = profile.Add<ColorAdjustments>(true);
            grade.postExposure.Override(0.05f); grade.saturation.Override(10f); grade.contrast.Override(14f);
            var go = new GameObject("GameplayPostFX");
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true; volume.priority = 50f; volume.sharedProfile = profile;
        }

        /// <summary>Scope glint on the roof: a camera-facing star that flares for a moment.</summary>
        void BuildGlint()
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "ScopeGlint";
            var mat = Resources.Load<Material>("FX/Star");
            var r = q.GetComponent<MeshRenderer>();
            if (mat != null) { var m = new Material(mat); m.SetColor("_BaseColor", new Color(3f, 3.2f, 3.6f, 1f)); r.sharedMaterial = m; }
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            glint = q.transform;
            glint.gameObject.SetActive(false);
        }

        void UpdateGlint()
        {
            float g = Mathf.Exp(-Mathf.Pow((film - 11.75f) / 0.13f, 2f)) + 0.7f * Mathf.Exp(-Mathf.Pow((film - 16.05f) / 0.1f, 2f));
            bool on = g > 0.02f && ninja.go.activeSelf && !ninja.dead;
            if (glint.gameObject.activeSelf != on) glint.gameObject.SetActive(on);
            if (!on) return;
            var nt = ninja.go.transform;
            glint.position = ninja.Muzzle() - nt.forward * 0.35f + Vector3.up * 0.06f;
            glint.rotation = Quaternion.LookRotation(glint.position - cam.transform.position) * Quaternion.Euler(0f, 0f, film * 90f);
            float dist = Vector3.Distance(glint.position, cam.transform.position);
            glint.localScale = Vector3.one * (0.05f + 0.022f * dist) * g;
        }

        // ================================================================== timeline
        void At(float t, Action a) => events.Add((t, a));
        void Delay(float gameSeconds, Action a) => pending.Add((game + gameSeconds, a));

        void BuildTimeline()
        {
            Stage F = fight;
            var aimSquare = W(F, 0f, 9f, 1f);

            // --- COVER: sprint in under fire, peek, headshot
            At(0f, () =>
            {
                hero.Show(true); hero.Teleport(FreeSpot(W(F, -1f, -9f)), Yaw(F.f)); hero.MoveTo(cover, Sprint);
                soldier.Show(true); soldier.Teleport(FreeSpot(W(F, -3f, -10f)), Yaw(F.f)); soldier.MoveTo(soldierSpot, Sprint);
                black.Show(true); black.Teleport(blackSpot, Yaw(-F.f));
                ninja.Show(true); ninja.Teleport(roof, Yaw(roofOut)); ninja.faceAt = aimSquare;
                black.StartFire(hero, 2.7f, 6f, 0f);
                Cue("ambient_loop", 0.45f, 1f, Duration);
            });
            At(2.75f, () => { hero.SetCrouch(true); soldier.SetCrouch(true); });
            for (float t = 2.8f; t < 3.8f; t += 0.16f)
                At(t, () => { black.face = hero; Shoot(black, hero.Chest + Vector3.up * 0.15f, hasWall, false, hasWall ? hero : null); });
            At(3.9f, () =>
            {
                hero.SetCrouch(false); hero.MoveTo(peek, Jog); hero.face = black;
                hero.StartFire(black, 4.95f, 8f, 0f);
                black.StartFire(hero, 4.9f, 5f, 0f);
            });
            At(5.0f, () => Headshot(hero, black));

            // --- GRENADE: the Knight charges, the bomb
            At(6.3f, () =>
            {
                knight.Show(true); knight.Teleport(knightStart, Yaw(soldierSpot - knightStart));
                knight.MoveTo(FreeSpot(soldierSpot + F.f * 4.5f), Sprint);
                soldier.SetCrouch(false); soldier.StartFire(knight, 7.45f, 7f, 0.25f);
                hero.face = knight;
            });
            At(7.4f, () => knight.StopMoving());
            At(7.5f, () => { knight.face = soldier; Shoot(knight, soldier, true); CameraFx.Shake(0.35f); });
            At(7.95f, () => { Shoot(knight, soldier, true); CameraFx.Shake(0.3f); });
            At(8.5f, () =>
            {
                soldier.SetCrouch(true);
                ThrowBomb(hero, knight.Pos + FlatDir(knight.Pos - hero.Pos) * 0.4f, 0.6f, 9.7f, at =>
                {
                    if (Flat(knight.Pos - at) < 6f) Kill(knight, at, false);
                });
            });

            // --- SNIPER: Gold runs across the square, bullet cam, headshot
            At(10.6f, () =>
            {
                soldier.SetCrouch(false);
                gold.Show(true); gold.Teleport(goldFrom, Yaw(goldTo - goldFrom)); gold.MoveTo(goldTo, Sprint);
                ninja.face = gold;
            });
            At(11.7f, () => Cue("banner_whoosh", 0.4f, 1.6f));
            At(13.25f, () =>
            {
                // bullet time long enough to watch the round fly the whole way (about 1.5 film seconds)
                float d = Vector3.Distance(ninja.Muzzle(), gold.Head);
                bulletScale = Mathf.Clamp(d / BulletSpeed / 1.5f, 0.03f, 0.35f);
            });
            At(13.42f, () =>
            {
                var muzzle = ninja.Muzzle();
                var aim = gold.Head;
                float travel = Vector3.Distance(muzzle, aim) / BulletSpeed;
                for (int i = 0; i < 2; i++) { aim = gold.Head + gold.vel * travel; travel = Vector3.Distance(muzzle, aim) / BulletSpeed; }
                bulletFrom = muzzle; bulletDir = (aim - muzzle).normalized; bulletG0 = game; bulletTravel = travel;
                if (Shoot(ninja, aim, true, false, gold, true))
                    Delay(travel, () => { HeadshotKill(gold, ninja); gold.StopMoving(); });
                Cue("banner_whoosh", 0.8f, 0.5f, 0f, 0.1f);
            });

            // --- ROOF: pinned down, suppressing fire, bomb onto the roof
            At(16.2f, () =>
            {
                ninja.face = hero;
                Shoot(ninja, hero.Pos + hero.go.transform.right * 0.7f + hero.go.transform.forward * 0.4f, false, true);
                hero.Hop(0.35f); hero.MoveTo(cover, Sprint); hero.face = ninja;
            });
            At(16.85f, () => Shoot(ninja, hero.Pos - hero.go.transform.right * 0.6f + Vector3.up * 1.8f, false, true));
            At(17.3f, () => { soldier.StartFire(ninja, 18.6f, 8f, 0f); hero.MoveTo(peek, Jog); });
            At(18.4f, () =>
            {
                var target = roof + roofOut * 0.6f;
                ThrowBomb(hero, target, 1.15f, 20.05f, at =>
                {
                    Kill(ninja, at, false);
                });
            });
            At(19.35f, () =>
            {
                ninja.face = null; ninja.faceAt = null;
                ninja.MoveDir(Vector3.Cross(Vector3.up, roofOut), 3.4f, 20.05f);
                Cue("banner_whoosh", 0.5f, 1.2f);
            });

            // --- WAVE 2: fast cuts
            At(21.2f, () =>
            {
                white.Show(true); white.Teleport(whiteSpot, Yaw(-F.f)); white.MoveTo(FreeSpot(whiteSpot - F.f * 3f), Jog);
                baby.Show(true); baby.Teleport(babySpot, Yaw(-F.f)); baby.MoveTo(FreeSpot(babySpot - F.f * 2.5f), Jog);
                white.StartFire(hero, 24.3f, 5f, 0f); baby.StartFire(soldier, 24.4f, 6f, 0f);
                hero.StartFire(white, 22.5f, 9f, 0.4f);
            });
            At(22.6f, () => { soldier.firing = false; soldier.SetCrouch(true); soldier.Reload(1.5f); Cue("reload_out", 0.7f); Cue("reload_in", 0.7f, 1f, 0f, 0.7f); Cue("reload_charge", 0.7f, 1f, 0f, 1.1f); });
            At(23.4f, () => { hero.MoveDir(F.r, 3.2f, 24.6f); hero.StartFire(white, 24.3f, 9f, 0.3f); });
            At(24.1f, () => { soldier.SetCrouch(false); soldier.face = white; });
            At(24.3f, () =>
            {
                if (Shoot(soldier, white, true)) Delay(Vector3.Distance(soldier.Muzzle(), white.Chest) / BulletSpeed, () => Kill(white, soldier.Pos, true));
            });
            At(24.6f, () =>
            {
                baby.firing = false; baby.StopMoving();
                var target = hero.Pos + hero.vel * 0.9f;
                diveDir = FlatDir(hero.Pos - baby.Pos);
                diveDir = Vector3.Cross(Vector3.up, diveDir) * (Vector3.Dot(hero.vel, F.r) >= 0f ? 1f : -1f);
                ThrowBomb(baby, target, 0.75f, 25.6f, at =>
                {
                    hero.Fling(FlatDir(hero.Pos - at) * 2.5f + Vector3.up * 2.5f);
                    hero.Flinch(at);
                });
            });
            At(25.15f, () => { hero.firing = false; hero.Hop(0.55f); hero.MoveDir(diveDir, 4.2f, 25.9f); });
            At(26.5f, () => { hero.face = baby; hero.StartFire(baby, 27.3f, 10f, 0.5f); });
            At(27.3f, () =>
            {
                if (Shoot(hero, baby, true)) Delay(Vector3.Distance(hero.Muzzle(), baby.Chest) / BulletSpeed, () => Kill(baby, hero.Pos, true));
            });

            // --- END: regroup, reload, crane up
            At(28.6f, () =>
            {
                foreach (var c in cats.Values) { c.firing = false; c.face = null; c.faceAt = null; }
                soldier.SetCrouch(false);
                hero.MoveTo(finalSpot, 1.8f);
                soldier.MoveTo(FreeSpot(finalSpot + F.r * 1.6f - F.f * 0.6f), 1.8f);
            });
            At(29.4f, () => { hero.Reload(1.8f); Cue("reload_out", 0.6f); Cue("reload_in", 0.6f, 1f, 0f, 0.8f); Cue("reload_charge", 0.6f, 1f, 0f, 1.3f); });
            At(30.4f, () => { hero.faceAt = W(F, 0f, 20f, 1.2f); soldier.faceAt = W(F, 4f, 18f, 1.2f); });
            events.Sort((a, b) => a.t.CompareTo(b.t));
        }

        static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        // slow motion: headshot 1, bomb 1, bullet time + headshot 2, roof bomb, dive, last kill
        float TimeScaleAt(float f)
        {
            float s = 1f;
            s = Mathf.Min(s, Ramp(f, 5.05f, 5.15f, 6.05f, 6.4f, 0.22f));
            s = Mathf.Min(s, Ramp(f, 9.6f, 9.68f, 10.45f, 10.85f, 0.25f));
            s = Mathf.Min(s, Ramp(f, 13.3f, 13.42f, 14.92f, 15.05f, bulletScale));
            s = Mathf.Min(s, Ramp(f, 14.92f, 15.0f, 15.85f, 16.2f, 0.22f));
            s = Mathf.Min(s, Ramp(f, 19.95f, 20.05f, 20.75f, 21.15f, 0.28f));
            s = Mathf.Min(s, Ramp(f, 25.45f, 25.55f, 26.2f, 26.6f, 0.25f));
            s = Mathf.Min(s, Ramp(f, 27.35f, 27.42f, 27.8f, 28.05f, 0.4f));
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

        /// <summary>Headshot: aimed at the head, the round flies, the head snaps back, slow-motion fall.</summary>
        void Headshot(Actor from, Actor target)
        {
            from.face = target; from.firing = false;
            var aim = target.Head;
            if (!Shoot(from, aim, true, false, target, true)) return;
            float travel = Vector3.Distance(from.Muzzle(), aim) / BulletSpeed;
            Delay(travel, () => HeadshotKill(target, from));
        }

        void HeadshotKill(Actor target, Actor from)
        {
            if (target.dead) return;
            target.HeadSnap(from.Pos);
            target.Die(true);
            KillFx.Play(target.Chest);
            CameraFx.Shake(0.35f);
            Cue("hitmarker_kill", 1f); Cue("kill_poof", 0.9f, 1f, 0f, 0.05f); Cue("impact_flesh", 0.9f);
        }

        void Kill(Actor a, Vector3 from, bool flingSoft)
        {
            if (a.dead) return;
            bool front = Vector3.Dot(a.go.transform.forward, from - a.Pos) > 0f;
            a.Die(front);
            var away = FlatDir(a.Pos - from);
            a.Fling(away * (flingSoft ? 1.5f : 3.5f) + Vector3.up * (flingSoft ? 1.2f : 4.5f));
            KillFx.Play(a.Chest);
            Cue("kill_poof", 0.8f, UnityEngine.Random.Range(0.9f, 1.1f)); Cue("hitmarker_kill", 0.7f);
        }

        /// <summary>Throw animation with the bomb in the paw, release after 0.43 s (game), lands after <paramref name="flight"/> s;
        /// it goes off at film second <paramref name="boomAt"/> (film time, so slow motion cannot push it past its shot).</summary>
        void ThrowBomb(Actor from, Vector3 target, float flight, float boomAt, Action<Vector3> onBoom)
        {
            from.face = null; from.faceAt = target; from.firing = false;
            from.StartThrow(BombThrower.ReleaseDelay);
            Cue("bomb_throw", 0.8f, 1f, 0f, 0.35f);
            ulong id = bombIds++;
            Delay(BombThrower.ReleaseDelay, () =>
            {
                from.EndThrow();
                var fwd = FlatDir(target - from.Pos);
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

            foreach (var a in all) a.Tick(dt);
            Separate();
        }

        /// <summary>Cats never stand inside each other: overlapping pairs are pushed apart (along walls, never through them).</summary>
        void Separate()
        {
            const float gap = 0.75f;
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                {
                    var a = all[i]; var b = all[j];
                    if (!a.go.activeSelf || !b.go.activeSelf || a.dead || b.dead) continue;
                    var d = b.Pos - a.Pos;
                    if (Mathf.Abs(d.y) > 1f) continue;
                    d.y = 0f;
                    float m = d.magnitude;
                    if (m >= gap) continue;
                    var dir = m > 1e-3f ? d / m : a.go.transform.right;
                    var push = dir * (gap - m) * 0.5f;
                    a.Nudge(-push); b.Nudge(push);
                }
        }

        void LateUpdate()
        {
            if (finished) return;
            float dt = Time.deltaTime;
            foreach (var a in all) a.Pose(dt);   // after the Animator: aim, recoil, flinch, head snap, paw bomb
            Shot(film);
            CameraFx.Apply(cam.transform, cam, record ? 1f / fps : Time.unscaledDeltaTime);
            UpdateGlint();
            dof.focusDistance.value = Mathf.Max(0.3f, focusDistance);
            dof.focalLength.value = focalLength;
            dof.aperture.value = aperture;
            if (!record && film >= Duration) Finish();
        }

        // ================================================================== camera
        /// <summary>Places the camera. <paramref name="anchor"/> = what the shot is about (the camera is swept from there, so a wall
        /// in between pulls it in front of the wall); null = the nearest cat; pass the position itself for free-flying shots.
        /// <paramref name="closeUp"/> lets the camera come closer than 0.5 m to the cats (macro shots).</summary>
        void Cam(Vector3 pos, Vector3 look, float fov, float focal = 35f, float fstop = 8f, float roll = 0f, Vector3? anchor = null, bool closeUp = false)
        {
            pos = KeepCameraClear(pos, look, anchor, closeUp);
            cam.transform.position = pos;
            var dir = look - pos;
            cam.transform.rotation = Quaternion.LookRotation(dir.sqrMagnitude > 1e-6f ? dir : Vector3.forward, Vector3.up) * Quaternion.Euler(0f, 0f, roll);
            cam.fieldOfView = fov;
            focusDistance = dir.magnitude; focalLength = focal; aperture = fstop;
        }

        int clearShot = -2; float clearK = 1f;

        Vector3 KeepCameraClear(Vector3 pos, Vector3 look, Vector3? anchorIn, bool closeUp)
        {
            Vector3 anchor = pos; bool use = false;
            if (anchorIn.HasValue) { anchor = anchorIn.Value; use = (anchor - pos).sqrMagnitude > 1e-4f; }
            else
            {
                float best = 14f;
                foreach (var a in all)
                {
                    if (!a.go.activeSelf) continue;
                    float dd = Vector3.Distance(a.Pos, pos);
                    if (dd < best) { best = dd; anchor = a.Pos + Vector3.up * 1.15f; use = true; }
                }
            }

            // 1. a wall / floor / roof between the subject and the camera pulls the camera in front of it
            //    (pulled in at once, let out again slowly, so it never pumps back and forth inside a shot)
            if (use)
            {
                var to = pos - anchor;
                float dist = to.magnitude;
                float k = dist > 0.05f ? Mathf.Max(0.25f, FreeLength(anchor, pos) - 0.12f) / dist : 1f;
                k = Mathf.Min(1f, k);
                float step = (record ? 1f / fps : Time.unscaledDeltaTime) * 1.5f / Mathf.Max(1f, dist);
                if (shotIndex != clearShot) { clearShot = shotIndex; clearK = k; }
                else clearK = k < clearK ? k : Mathf.MoveTowards(clearK, k, step);
                pos = anchor + to * clearK;
            }

            // 2. never inside a cat
            if (!closeUp)
                foreach (var a in all)
                {
                    if (!a.go.activeSelf) continue;
                    var p0 = a.Pos + Vector3.up * 0.15f; var p1 = a.Pos + Vector3.up * 1.5f;
                    var c = p0 + Vector3.Project(pos - p0, Vector3.up);
                    c.y = Mathf.Clamp(c.y, p0.y, p1.y);
                    var v = pos - c;
                    float body = c.y > a.Pos.y + 0.9f ? 0.65f : 0.5f;   // the chibi head is wider than the body
                    if (v.magnitude < body) pos = c + (v.sqrMagnitude > 1e-4f ? v.normalized : -cam.transform.forward) * body;
                }

            // 3. never below the ground, never inside scenery
            float gy = GroundAt(pos, pos.y + 0.6f);
            if (pos.y < gy + 0.18f) pos.y = gy + 0.18f;
            if (use)
            {
                // the pushes above may have moved it behind a wall again: one more two-way check
                var to = pos - anchor; float dist = to.magnitude;
                if (dist > 0.05f) { float free = FreeLength(anchor, pos); if (free < dist) pos = anchor + to / dist * Mathf.Max(0.25f, free - 0.12f); }
            }
            return pos;
        }

        /// <summary>
        /// Free distance from a towards b. The town meshes are single-sided MeshColliders: a ray only hits a face from its front,
        /// so a wall seen from behind (or a camera inside a wall) is invisible to one-way casts. Casting both ways catches it.
        /// </summary>
        static float FreeLength(Vector3 a, Vector3 b)
        {
            var d = b - a; float len = d.magnitude;
            if (len < 1e-4f) return 0f;
            var dir = d / len;
            float free = len;
            foreach (var h in Physics.RaycastAll(a, dir, len, ~0, QueryTriggerInteraction.Ignore))
                if (h.collider.attachedRigidbody == null) free = Mathf.Min(free, h.distance);
            foreach (var h in Physics.RaycastAll(b, -dir, len, ~0, QueryTriggerInteraction.Ignore))
                if (h.collider.attachedRigidbody == null) free = Mathf.Min(free, len - h.distance);
            // a slightly fat ray as well, so the lens does not graze an edge
            if (Physics.SphereCast(a, 0.12f, dir, out var s, len, ~0, QueryTriggerInteraction.Ignore) && s.collider.attachedRigidbody == null)
                free = Mathf.Min(free, s.distance);
            return free;
        }

        static float Ease(float x) => Smooth(x);

        /// <summary>Handheld wobble (film seconds).</summary>
        static Vector3 Shake(float f, float amount) =>
            new Vector3(Mathf.PerlinNoise(f * 3.1f, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, f * 2.7f) - 0.5f, Mathf.PerlinNoise(f * 2.3f, 5.1f) - 0.5f) * amount;

        void Add(float t, Action<float> shot) => shots.Add((t, shot));

        Vector3 BulletPos()
        {
            if (bulletG0 < 0f) return ninja.Muzzle();
            float t = Mathf.Clamp(game - bulletG0, 0f, bulletTravel);
            return bulletFrom + bulletDir * (0.3f + BulletSpeed * t);
        }

        void BuildShots()
        {
            Stage F = fight;
            Vector3 up = Vector3.up;
            Vector3 roofSide = Vector3.Cross(up, roofOut);

            // ---------------- COVER
            Add(0f, u =>          // 1: high behind the two cats, craning down as they sprint for the wall
            {
                var e = Ease(u);
                var pos = Vector3.Lerp(W(F, -2.5f, -15.5f, 6.5f), W(F, -2.2f, -12.5f, 2.8f), e);
                Cam(pos + Shake(film, 0.12f), Vector3.Lerp(hero.Pos + up, W(F, 0f, 5f, 1.2f), 0.45f), 52f, 24f, 11f, -2f, hero.Chest + up * 0.4f);
            });
            Add(1.6f, u =>        // 2: low tracking beside the sprinting hero
            {
                var pos = hero.Pos + F.r * 2.3f + F.f * 1.1f + up * 0.5f;
                Cam(pos + Shake(film, 0.08f), hero.Chest + F.f * 1.2f, 50f, 35f, 4f, -5f, hero.Chest);
            });
            Add(2.7f, u =>        // 3: tucked in behind the wall, rounds chewing the cover
            {
                var pos = cover - F.f * 1.5f + F.r * 1.4f + up * 0.75f;
                Cam(pos + Shake(film, 0.1f), cover + up * 0.95f + F.f * 0.5f, 46f, 35f, 3.5f, 4f, hero.Chest);
            });
            Add(3.9f, u =>        // 4: over the shoulder as he steps out and lines up
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.right * 0.65f + up * 1.55f - t.forward * 1.5f;
                Cam(pos + Shake(film, 0.05f), black.Chest, Mathf.Lerp(34f, 26f, Ease(u)), 70f, 5.6f, 0f, hero.Head);
                focusDistance = Vector3.Distance(pos, black.Chest);
            });
            Add(5.0f, u =>        // 5: slow-motion orbit round Black going down
            {
                var t = black.go.transform;
                float a = Mathf.Lerp(40f, 140f, Ease(u)) * Mathf.Deg2Rad;
                var pos = black.Pos + (t.right * Mathf.Cos(a) + t.forward * Mathf.Sin(a)) * 2.6f + up * Mathf.Lerp(1.4f, 0.8f, u);
                Cam(pos, black.Pos + up * 0.8f, 40f, 50f, 3.5f, 0f, black.Chest);
            });

            // ---------------- GRENADE
            Add(6.4f, u =>        // 6: behind the Knight's shoulder as he charges
            {
                var t = knight.go.transform;
                var pos = knight.Pos - t.forward * 1.9f + t.right * 0.6f + up * 1.25f;
                Cam(pos + Shake(film, 0.12f), knight.Pos + t.forward * 6f + up * 0.9f, 48f, 35f, 5.6f, 3f, knight.Chest);
            });
            Add(7.5f, u =>        // 7: in front of the Knight, shotgun at the lens
            {
                var t = knight.go.transform;
                var pos = knight.Pos + t.forward * Mathf.Lerp(2.4f, 1.9f, u) - t.right * 0.6f + up * 0.9f;
                Cam(pos, knight.Chest + t.forward * 0.5f, 42f, 35f, 4f, 6f, knight.Chest);
            });
            Add(8.5f, u =>        // 8: side medium, the bomb in the paw, the throw
            {
                var t = hero.go.transform;
                var pos = hero.Pos - t.right * 2.4f + t.forward * 1.1f + up * 1.1f;
                Cam(pos, hero.Pos + up * 1.3f + t.forward * 0.8f, 40f, 50f, 4f, -3f, hero.Chest);
            });
            Add(9.3f, u =>        // 9: chase the bomb, slow-motion blast
            {
                var dir = FlatDir(lastBombTarget - hero.Pos);
                var side = Vector3.Cross(up, dir);
                var pos = Vector3.Lerp(hero.Pos + up * 1.8f + side * 1.6f, lastBombTarget + side * 4.5f + up * 1.6f - dir * 2.5f, Ease(u));
                var look = lastBomb != null ? lastBomb.transform.position : lastBombTarget + up * 0.6f;
                Cam(pos, look, 46f, 35f, 5.6f, 4f, look);
            });

            // ---------------- SNIPER
            Add(10.9f, u =>       // 10: from the street up the wall to the sniper on the roof (rise first, then move in)
            {
                float rise = Ease(Mathf.Clamp01(u / 0.6f)), inward = Ease(Mathf.Clamp01((u - 0.45f) / 0.55f));
                var flat = roof + roofOut * Mathf.Lerp(8f, 2.4f, inward) + roofSide * 1.3f;
                float street = GroundAt(roof + roofOut * 8f, roof.y - 2f);
                flat.y = Mathf.Lerp(street + 1.3f, roof.y + 2.1f, rise);
                var look = Vector3.Lerp(roof + up * 0.4f, ninja.Head, rise);
                Cam(flat, look, 44f, 50f, 4f, 0f, inward > 0.05f ? ninja.Head + up * 0.4f : flat);
            });
            Add(12.3f, u =>       // 11: through the scope, zooming in on Gold
            {
                var t = ninja.go.transform;
                // behind the big chibi head, over its right shoulder (0.55 m back was inside the head: black frames)
                var pos = ninja.Head + t.right * 0.75f + up * 0.45f - t.forward * 1.6f;
                Cam(pos, gold.Head, Mathf.Lerp(18f, 7.5f, Ease(u)), 135f, 5.6f, 0f, ninja.Head + up * 0.1f);
                focusDistance = Vector3.Distance(pos, gold.Head);
            });
            Add(13.42f, u =>      // 12: bullet cam all the way to the head
            {
                var b = BulletPos();
                var side = Vector3.Cross(up, bulletDir).normalized;
                var pos = b - bulletDir * 1.1f + side * 0.35f + up * 0.18f;
                Cam(pos, b + bulletDir * 3f, 46f, 35f, 5.6f, Mathf.Sin(film * 3f) * 4f, b);
                focusDistance = 1.2f;
            });
            Add(14.95f, u =>      // 13: low angle, Gold drops in slow motion, the roof behind
            {
                var toRoof = FlatDir(roof - gold.Pos);
                var side = Vector3.Cross(up, toRoof);
                var pos = gold.Pos - toRoof * 2.6f + side * 0.9f + up * 0.35f;
                Cam(pos, gold.Pos + up * 0.9f, 44f, 35f, 4f, 3f, gold.Chest);
            });

            // ---------------- ROOF
            Add(16.2f, u =>       // 14: behind the hero, looking up at the sniper, rounds snapping past
            {
                var toRoof = FlatDir(roof - hero.Pos);
                var side = Vector3.Cross(up, toRoof);
                var pos = hero.Pos - toRoof * 2.2f + side * 0.7f + up * 0.6f;
                Cam(pos + Shake(film, 0.14f), roof + up * 1.2f, 50f, 35f, 8f, -4f, hero.Chest);
            });
            Add(17.3f, u =>       // 15: the Soldier fires up at the roof
            {
                var t = soldier.go.transform;
                // low over the shoulder, the Soldier in frame and the roof above
                var pos = soldier.Pos - t.forward * 1.3f + t.right * 0.9f + up * 0.6f;
                Cam(pos + Shake(film, 0.08f), Vector3.Lerp(soldier.Chest, roof + up * 1.2f, 0.3f), 50f, 35f, 5.6f, 5f, soldier.Chest);
            });
            Add(18.4f, u =>       // 16: low behind the hero, the bomb arcs up towards the roof
            {
                var toRoof = FlatDir(roof - hero.Pos);
                var side = Vector3.Cross(up, toRoof);
                var pos = hero.Pos - toRoof * 1.8f - side * 0.6f + up * 0.5f;
                var look = Vector3.Lerp(hero.Pos + up * 1.4f + toRoof * 2f, lastBomb != null ? lastBomb.transform.position : roof + up, Ease(u));
                Cam(pos, look, 52f, 24f, 8f, -3f, hero.Chest);
            });
            Add(19.3f, u =>       // 17: on the roof, the sniper turns and runs, the blast
            {
                var pos = roof - roofOut * 3.2f + roofSide * 1.8f + up * 1.6f;
                Cam(pos + Shake(film, 0.06f), ninja.Chest, 48f, 35f, 5.6f, 2f, ninja.Head + up * 0.3f);
            });

            // ---------------- WAVE 2 (fast cuts)
            Add(21.2f, u =>       // 18: macro on the hero's muzzle
            {
                var m = hero.Muzzle(); var t = hero.go.transform;
                Cam(m + t.right * 0.45f + t.forward * 0.25f + up * 0.08f, m - t.forward * 0.25f, 32f, 85f, 2f, -4f, m, true);
            });
            Add(21.9f, u =>       // 19: White hit
            {
                var t = white.go.transform;
                var pos = white.Pos + t.forward * 2.3f + t.right * 0.8f + up * 1.1f;
                Cam(pos + Shake(film, 0.06f), white.Chest, 40f, 50f, 3.5f, -3f, white.Chest);
            });
            Add(22.6f, u =>       // 20: the Soldier crouched, reloading
            {
                var t = soldier.go.transform;
                var pos = soldier.Pos + t.forward * 1.2f + t.right * 0.55f + up * 0.85f;
                Cam(pos, soldier.Pos + up * 0.85f, 38f, 70f, 2.8f, 4f, soldier.Chest);
            });
            Add(23.4f, u =>       // 21: run-and-gun, camera racing ahead, dutch angle
            {
                var pos = hero.Pos + F.r * 3f + F.f * 1.4f + up * 0.7f;
                Cam(pos + Shake(film, 0.1f), hero.Chest, 46f, 35f, 4f, -8f, hero.Chest);
            });
            Add(24.6f, u =>       // 22: behind Baby's shoulder, the bomb leaves the paw
            {
                var t = baby.go.transform;
                var pos = baby.Pos - t.forward * 1.8f + t.right * 0.6f + up * 1.3f;
                Cam(pos + Shake(film, 0.08f), baby.Pos + t.forward * 6f + up * 0.6f, 46f, 35f, 5.6f, 3f, baby.Chest);
            });
            Add(25.3f, u =>       // 23: slow-motion dive away from the blast
            {
                var side = Vector3.Cross(up, diveDir);
                var pos = hero.Pos + side * 2.7f + diveDir * 0.6f + up * 0.5f;
                Cam(pos, hero.Chest, 44f, 35f, 4f, 2f, hero.Chest);
            });
            Add(26.6f, u =>       // 24: front low, the hero answers
            {
                var t = hero.go.transform;
                var pos = hero.Pos + t.forward * 2.2f - t.right * 0.5f + up * 0.5f;
                Cam(pos + Shake(film, 0.06f), hero.Chest + up * 0.2f, 44f, 35f, 4f, -4f, hero.Chest);
            });
            Add(27.6f, u =>       // 25: Baby goes down
            {
                var toHero = FlatDir(hero.Pos - baby.Pos);
                var pos = baby.Pos + toHero * 3f + Vector3.Cross(up, toHero) * 1.5f + up * 1f;
                Cam(pos, baby.Pos + up * 0.6f, 42f, 50f, 4f, 0f, baby.Chest);
            });

            // ---------------- END
            Add(28.6f, u =>       // 26: slow orbit round the two, reloading
            {
                float a = Mathf.Lerp(-50f, 40f, Ease(u)) * Mathf.Deg2Rad;
                var pos = hero.Pos + (F.f * Mathf.Cos(a) + F.r * Mathf.Sin(a)) * 3f + up * 1.3f;
                Cam(pos, hero.Pos + up * 1.1f, 46f, 50f, 4f, 0f, hero.Chest);
            });
            Add(31.0f, u =>       // 27: crane up and away over the square
            {
                float e = Ease(Mathf.Clamp01(u / 0.8f));
                var pos = finalSpot - F.f * Mathf.Lerp(3f, 10f, e) + F.r * Mathf.Lerp(1.5f, 4f, e);
                pos.y = finalSpot.y + Mathf.Lerp(1.4f, 9f, e);
                Cam(pos, finalSpot + F.f * Mathf.Lerp(0f, 7f, e) + up * 0.9f, Mathf.Lerp(46f, 52f, e), 24f, 11f, 0f, hero.Chest + up * 0.4f);
            });
            shots.Sort((a, b) => a.t.CompareTo(b.t));
        }

        void Shot(float f)
        {
            int i = shots.Count - 1;
            while (i > 0 && shots[i].t > f) i--;
            shotIndex = i;
            float start = shots[i].t, end = i + 1 < shots.Count ? shots[i + 1].t : Duration;
            shots[i].shot(Mathf.Clamp01((f - start) / Mathf.Max(0.01f, end - start)));
        }

        // ================================================================== shooting
        bool Shoot(Actor from, Actor target, bool hit) => Shoot(from, target.Chest, hit, false, target);

        /// <summary>One shot (all pellets). Returns true when a round reaches the target (no wall in between).</summary>
        bool Shoot(Actor from, Vector3 aim, bool hit, bool nearMiss, Actor target = null, bool head = false)
        {
            if (!from.go.activeSelf || from.dead) return false;
            var w = Weapons.Get(from.weapon);
            var muzzle = from.Muzzle();
            from.anim.SetTrigger("Fire");
            from.Recoil(w.pellets > 1 ? 7f : w.fireRate < 2f ? 9f : 3.5f);
            from.fx = from.fx != null ? from.fx : MuzzleFlash.For(from.go);
            float size = w.pellets > 1 ? 1.45f : w.fireRate < 2f ? 1.35f : w.damage < 16f ? 0.8f : 1f;
            float pitch = Mathf.Lerp(0.55f, 1f, Mathf.Sqrt(Mathf.Max(0.05f, Time.timeScale)));
            Cue(GameAudio.GunSound(w), 1f, pitch);
            if (UnityEngine.Random.value < 0.5f) Cue("shell", 0.3f, pitch, 0f, 0.35f);
            if (from.fx != null) from.fx.Play(muzzle, aim - muzzle, size, from.go.transform.right);
            CameraFx.Kick(0.1f, UnityEngine.Random.Range(-0.06f, 0.06f));

            bool reached = false;
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
                    end = aim + (head ? Vector3.zero : UnityEngine.Random.insideUnitSphere * 0.12f); normal = -dir; surface = MuzzleFlash.Flesh; landed = true;
                    reached = true;
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
                    float travel = Vector3.Distance(muzzle, end) / BulletSpeed;
                    var t = target; bool flesh = surface == MuzzleFlash.Flesh; int s = surface; var src = from.Pos;
                    Delay(travel, () =>
                    {
                        float p2 = Mathf.Lerp(0.55f, 1f, Mathf.Sqrt(Mathf.Max(0.05f, Time.timeScale)));
                        Cue(GameAudio.ImpactSound(s), flesh ? 0.8f : 0.5f, p2);
                        if (flesh && t != null && !t.dead)
                        {
                            t.Flash(); t.Stagger(src); t.Flinch(src);
                            t.anim.SetTrigger("Hit");
                            Cue("hitmarker", 0.5f, p2);
                        }
                    });
                }
            }
            if (nearMiss) Cue("banner_whoosh", 0.9f, 0.6f, 0f, 0.15f);
            return reached;
        }

        // ================================================================== sound cues (mixed by Tools/Cinematic/make_gameplay_action.py)
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
                Debug.Log("[Gameplay] done: " + frame + " frames in " + outDir + "  ->  python Tools/Cinematic/make_gameplay_action.py \"" + outDir + "\"");
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
            float aspect = width / (float)height;
            float w = Screen.width, h = Screen.width / aspect;
            if (h > Screen.height) { h = Screen.height; w = h * aspect; }
            GUI.DrawTexture(new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h), rt, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(10, 10, 600, 24), (record ? "REC " : "") + film.ToString("0.00") + " / " + Duration + " s   frame " + frame + "   shot " + (shotIndex + 1));
        }

        // ================================================================== actor
        class Actor
        {
            readonly GameplayAction d;
            public readonly string id;
            public readonly GameObject go;
            public readonly Animator anim;
            public MuzzleFlash fx;
            public int weapon;
            public bool dead, firing;
            readonly Renderer rifle;
            readonly Renderer[] renderers;
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            readonly Transform spine, spine1, spine2, neck, head, leftHand, leftTip;
            float flashAt = -10f;

            // locomotion
            public Vector3 vel;                       // horizontal velocity actually moved (m/s)
            float vy;                                 // falling / flung
            readonly List<Vector3> path = new List<Vector3>();
            int pathIndex; bool moving; float moveSpeed;
            Vector3 moveDir; float moveUntil = -1f;
            public Actor face;
            public Vector3? faceAt;
            Vector3 fling; float flingUntil = -1f;
            Vector3 strafeDir; float strafeUntil = -1f, nextStrafe;
            float staggerUntil = -1f; Vector3 staggerDir;
            float hopStart = -10f, hopHeight;
            float turnRate;
            bool crouch;

            // procedural pose on top of the animation
            float aimW, recoil, flinch, headSnap;
            Vector3 flinchAxis = Vector3.right, snapAxis = Vector3.right;
            float throwUntil = -1f;
            GameObject pawBomb;

            Actor fireTarget; float fireUntil, fireInterval, hitChance, nextShot;

            public Actor(GameplayAction director, string id, GameObject model, RuntimeAnimatorController controller)
            {
                d = director; this.id = id;
                go = Instantiate(model);
                go.name = "Gameplay_" + id;
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
                    if (rifle == null && r.name.ToLowerInvariant().Contains("rifl")) rifle = r;
                    if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                }
                renderers = list.ToArray();
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    if (IsBone(t, "Spine")) spine = t;
                    else if (IsBone(t, "Spine1")) spine1 = t;
                    else if (IsBone(t, "Spine2")) spine2 = t;
                    else if (IsBone(t, "Neck")) neck = t;
                    else if (IsBone(t, "Head")) head = t;
                    else if (IsBone(t, "LeftHand")) leftHand = t;
                    else if (IsBone(t, "LeftHandMiddle1") || (leftTip == null && IsBone(t, "LeftHandIndex1"))) leftTip = t;
                }
                if (go.GetComponent<FootFx>() == null) go.AddComponent<FootFx>();
            }

            static bool IsBone(Transform t, string n) => t.name == n || t.name.EndsWith(":" + n, StringComparison.Ordinal);

            public Vector3 Pos => go.transform.position;
            public Vector3 Chest => go.transform.position + Vector3.up * (crouch ? 0.72f : 1.08f);
            public Vector3 Head => head != null ? head.position + Vector3.up * 0.07f : go.transform.position + Vector3.up * (crouch ? 1.0f : 1.42f);

            public void Show(bool on)
            {
                if (go.activeSelf == on) return;
                go.SetActive(on);
                if (on) anim.SetBool("Grounded", true);
            }

            public void Teleport(Vector3 p, float yaw)
            {
                p = FreeSpot(p);   // never inside a house / wall
                go.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, yaw, 0f));
                vel = Vector3.zero; vy = 0f; flingUntil = -1f; strafeUntil = -1f; moving = false; moveUntil = -1f;
            }

            /// <summary>Walk / run to target along the NavMesh (around houses), easing in and out.</summary>
            public void MoveTo(Vector3 target, float speed)
            {
                target = FreeSpot(target);
                path.Clear();
                var nav = new NavMeshPath();
                if (NavMesh.SamplePosition(Pos, out var a, 1.5f, NavMesh.AllAreas) && NavMesh.SamplePosition(target, out var b, 1.5f, NavMesh.AllAreas)
                    && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, nav) && nav.status != NavMeshPathStatus.PathInvalid)
                    for (int i = 1; i < nav.corners.Length; i++) path.Add(nav.corners[i]);
                if (path.Count == 0 || Flat(path[path.Count - 1] - target) > 0.3f) path.Add(target);
                pathIndex = 0; moving = true; moveSpeed = speed; moveUntil = -1f;
            }

            public void MoveDir(Vector3 dir, float speed, float untilFilm) { dir.y = 0f; moveDir = dir.normalized; moveSpeed = speed; moveUntil = untilFilm; moving = false; }
            public void StopMoving() { moving = false; moveUntil = -1f; }
            public void Hop(float h) { hopStart = d.game; hopHeight = h; anim.SetTrigger("Jump"); }
            public void Fling(Vector3 v) { fling = new Vector3(v.x, 0f, v.z); vy = v.y; flingUntil = d.game + 0.6f; }
            public void Nudge(Vector3 push) { if (!dead) go.transform.position = Step(Pos, push); }
            public void SetCrouch(bool on) { crouch = on; anim.SetBool("Crouching", on); }

            public void Reload(float seconds)
            {
                anim.SetFloat("ReloadSpeed", 1.8f / Mathf.Max(0.3f, seconds));
                anim.SetTrigger("Reload");
                throwUntil = d.game + seconds;   // no aim twist while the paws are busy
            }

            public void StartThrow(float release)
            {
                anim.SetTrigger("Throw");
                throwUntil = d.game + release + 0.45f;
                if (pawBomb == null && leftHand != null) pawBomb = Bomb.CreateVisual("PawBomb_" + id);
            }

            public void EndThrow() { if (pawBomb != null) { Destroy(pawBomb); pawBomb = null; } }

            public void Stagger(Vector3 from)
            {
                if (dead) return;
                staggerDir = FlatDir(Pos - from);
                staggerUntil = d.game + 0.12f;
            }

            public void Flinch(Vector3 from)
            {
                var away = FlatDir(Pos - from);
                flinchAxis = Vector3.Cross(Vector3.up, away);   // bends the chest away from the hit
                flinch = 14f;
            }

            public void HeadSnap(Vector3 from)
            {
                var away = FlatDir(Pos - from);
                snapAxis = Vector3.Cross(Vector3.up, away);
                headSnap = 38f;
                Flinch(from);
            }

            public void Recoil(float degrees) => recoil = Mathf.Min(recoil + degrees, 14f);

            public void StartFire(Actor target, float untilFilm, float rate, float hits)
            {
                if (target == null) return;
                firing = true; fireTarget = target; fireUntil = untilFilm; fireInterval = 1f / rate; hitChance = hits; nextShot = d.game;
                face = target; faceAt = null;
            }

            public void Die(bool fromFront)
            {
                dead = true; firing = false; StopMoving(); EndThrow();
                anim.SetBool("DeathBack", fromFront); anim.SetBool("Dead", true);
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
                if (!go.activeSelf || dt <= 0f) return;
                var t = go.transform;
                var p = t.position;

                // ---- where this cat wants to go
                Vector3 want = Vector3.zero;
                if (!dead)
                {
                    if (moving && path.Count > 0)
                    {
                        while (pathIndex < path.Count - 1 && Flat(path[pathIndex] - p) < 0.4f) pathIndex++;
                        var to = path[pathIndex] - p; to.y = 0f;
                        float rest = to.magnitude;
                        for (int i = pathIndex; i < path.Count - 1; i++) rest += Flat(path[i + 1] - path[i]);
                        if (pathIndex == path.Count - 1 && to.magnitude < 0.06f) moving = false;
                        else want = to.normalized * Mathf.Min(moveSpeed, rest * 2.2f + 0.15f);   // eases into the last metre
                    }
                    else if (moveUntil > 0f)
                    {
                        if (d.film < moveUntil) want = moveDir * moveSpeed; else moveUntil = -1f;
                    }
                    else if (firing && !crouch)
                    {
                        // shooters do not stand like statues: short side steps between bursts
                        if (d.game >= nextStrafe)
                        {
                            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                            strafeDir = (t.right * side + (UnityEngine.Random.value < 0.3f ? -t.forward * 0.6f : Vector3.zero)).normalized;
                            strafeUntil = d.game + UnityEngine.Random.Range(0.35f, 0.7f);
                            nextStrafe = strafeUntil + UnityEngine.Random.Range(0.3f, 0.9f);
                        }
                        if (d.game < strafeUntil) want = strafeDir * 1.1f;
                    }
                }

                // ---- velocity with acceleration: no instant starts or stops
                if (flingUntil > d.game) vel = fling;
                else if (!dead && staggerUntil > d.game) vel = staggerDir * 1.6f;
                else
                {
                    float accel = want.sqrMagnitude > vel.sqrMagnitude ? 11f : 15f;
                    vel = Vector3.MoveTowards(vel, dead ? Vector3.zero : want, accel * dt);
                }
                var np = Step(p, vel * dt);
                var moved = np - p; moved.y = 0f;
                if (flingUntil <= d.game) vel = moved / dt;   // walking into a wall shows as standing, not running in place

                // ---- height: steps up stairs, falls off edges, hops
                float gy = GroundAt(np, p.y + 0.6f);
                float ht = d.game - hopStart;
                bool hopping = ht >= 0f && ht < 0.7f;
                if (hopping) np.y = gy + Mathf.Sin(ht / 0.7f * Mathf.PI) * hopHeight;
                else if (np.y > gy + 0.02f || vy > 0f)
                {
                    vy -= 14f * dt;
                    np.y = Mathf.Max(gy, p.y + vy * dt);
                    if (np.y <= gy) vy = 0f;
                }
                else { np.y = gy; vy = 0f; }
                anim.SetBool("Grounded", !hopping && np.y < gy + 0.15f);
                t.position = np;

                // ---- facing: eased, speed-capped turns
                Vector3 look = Vector3.zero;
                if (face != null && face.go.activeSelf) look = face.Pos - np;
                else if (faceAt.HasValue) look = faceAt.Value - np;
                else if (vel.sqrMagnitude > 0.25f) look = vel;
                else if (moveUntil > 0f) look = moveDir;
                look.y = 0f;
                float before = t.eulerAngles.y;
                if (!dead && look.sqrMagnitude > 0.01f)
                {
                    var target = Quaternion.LookRotation(look);
                    var eased = Quaternion.Slerp(t.rotation, target, 1f - Mathf.Exp(-9f * dt));
                    t.rotation = Quaternion.RotateTowards(t.rotation, eased, 540f * dt);
                }
                turnRate = Mathf.Lerp(turnRate, Mathf.DeltaAngle(before, t.eulerAngles.y) / dt, 1f - Mathf.Exp(-12f * dt));

                // ---- animation from the real motion; a little shuffle step when turning on the spot
                var local = t.InverseTransformDirection(vel) / WalkSpeed;
                if (!dead && vel.sqrMagnitude < 0.04f && Mathf.Abs(turnRate) > 70f) local.x += Mathf.Clamp(turnRate / 400f, -0.45f, 0.45f);
                anim.SetFloat("MoveX", local.x, 0.12f, dt);
                anim.SetFloat("MoveY", local.z, 0.12f, dt);
                anim.SetFloat("MoveSpeed", new Vector2(local.x, local.z).magnitude, 0.12f, dt);
                int upper = anim.GetLayerIndex("UpperBody");
                if (upper >= 0) anim.SetLayerWeight(upper, Mathf.MoveTowards(anim.GetLayerWeight(upper), dead ? 0f : 1f, dt * 8f));

                // ---- automatic fire
                if (firing && !dead)
                {
                    if (d.film >= fireUntil || fireTarget == null || !fireTarget.go.activeSelf || fireTarget.dead) firing = false;
                    else if (d.game >= nextShot)
                    {
                        nextShot = d.game + fireInterval * UnityEngine.Random.Range(0.9f, 1.15f);
                        d.Shoot(this, fireTarget, UnityEngine.Random.value < hitChance);
                    }
                }

                // ---- hit flash
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

            /// <summary>After the Animator: the spine bends to the aim (pitch + the yaw the body has not turned yet), recoil kicks,
            /// hit flinches and the headshot snap decay away; the bomb sits in the left paw during the wind-up.</summary>
            public void Pose(float dt)
            {
                if (!go.activeSelf) return;
                var t = go.transform;
                recoil *= Mathf.Exp(-13f * dt);
                flinch *= Mathf.Exp(-9f * dt);
                headSnap *= Mathf.Exp(-4.5f * dt);

                Vector3? aimPoint = null;
                if (!dead && d.game > throwUntil)
                {
                    if (face != null && face.go.activeSelf) aimPoint = face.dead ? face.Pos + Vector3.up * 0.4f : face.Chest;
                    else if (faceAt.HasValue) aimPoint = faceAt.Value;
                }
                aimW = Mathf.MoveTowards(aimW, aimPoint.HasValue ? 1f : 0f, dt * 4f);

                float pitch = 0f, yaw = 0f;
                if (aimPoint.HasValue)
                {
                    var from = Pos + Vector3.up * (crouch ? 0.72f : 1.08f);
                    var dir = aimPoint.Value - from;
                    float flat = new Vector2(dir.x, dir.z).magnitude;
                    pitch = Mathf.Clamp(-Mathf.Atan2(dir.y, Mathf.Max(0.01f, flat)) * Mathf.Rad2Deg, -40f, 40f);   // + = down
                    var fd = dir; fd.y = 0f;
                    if (fd.sqrMagnitude > 1e-4f) yaw = Mathf.Clamp(Vector3.SignedAngle(t.forward, fd, Vector3.up), -35f, 35f);
                    pitch *= aimW; yaw *= aimW;
                }
                var right = t.right;
                Bend(spine, yaw * 0.25f, pitch * 0.25f, right, 0f);
                Bend(spine1, yaw * 0.3f, pitch * 0.3f - recoil * 0.4f, right, flinch * 0.5f);
                Bend(spine2, yaw * 0.3f, pitch * 0.3f - recoil * 0.6f, right, flinch * 0.5f);
                Bend(head, yaw * 0.15f, pitch * 0.15f, right, 0f);
                if (headSnap > 0.3f)
                {
                    // a positive turn about Cross(up, away) tips the top towards "away": the head is knocked away from the shooter
                    if (neck != null) neck.rotation = Quaternion.AngleAxis(headSnap * 0.4f, snapAxis) * neck.rotation;
                    if (head != null) head.rotation = Quaternion.AngleAxis(headSnap * 0.6f, snapAxis) * head.rotation;
                }

                if (pawBomb != null && leftHand != null)
                {
                    var tip = leftTip != null ? leftTip.position : leftHand.position;
                    pawBomb.transform.SetPositionAndRotation(Vector3.Lerp(leftHand.position, tip, 0.6f), leftHand.rotation);
                }
            }

            void Bend(Transform bone, float yaw, float pitch, Vector3 right, float flinchDeg)
            {
                if (bone == null) return;
                var q = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitch, right);
                if (flinchDeg > 0.05f) q = Quaternion.AngleAxis(flinchDeg, flinchAxis) * q;   // chest pushed away from the hit
                bone.rotation = q * bone.rotation;
            }
        }
    }
}
