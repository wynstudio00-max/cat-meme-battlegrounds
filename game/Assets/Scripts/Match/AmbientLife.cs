using System.Collections.Generic;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Makes the island feel alive around the camera (added by <see cref="SceneAtmosphere"/>, arena only, never on a server):
    ///  * falling leaves (green / lime / autumn) drifting with the wind and tumbling,
    ///  * glittering pollen / dust specks floating in the sunlight,
    ///  * butterflies fluttering over the ground, each with its own colour, wandering and flapping.
    /// Everything follows the camera and stops when the camera is high in the air (drop plane, free fall).
    /// Textures / materials from FxAssets (Leaf, Wing, MuzzleSpark).
    /// </summary>
    public class AmbientLife : MonoBehaviour
    {
        const int ButterflyCount = 14;
        const float ButterflyRange = 32f;

        ParticleSystem leaves, pollen;
        AudioSource ambience;
        readonly List<Butterfly> butterflies = new List<Butterfly>();
        Material wingMat;
        Camera cam;

        static readonly Color[] LeafColors =
        {
            new Color(0.42f, 0.7f, 0.22f), new Color(0.62f, 0.8f, 0.25f), new Color(0.95f, 0.62f, 0.18f), new Color(0.9f, 0.42f, 0.15f), new Color(0.8f, 0.75f, 0.25f),
        };
        static readonly Color[] WingColors =
        {
            new Color(1f, 0.58f, 0.15f), new Color(0.35f, 0.7f, 1f), new Color(1f, 0.92f, 0.35f), new Color(1f, 1f, 1f), new Color(1f, 0.55f, 0.8f), new Color(0.6f, 0.95f, 0.5f),
        };

        class Butterfly
        {
            public Transform root, wingL, wingR;
            public Vector3 target;
            public float speed, flapRate, phase, retarget;
        }

        void OnDestroy() { if (ambience != null) Destroy(ambience.gameObject); }

        void Start()
        {
            var leafMat = Resources.Load<Material>("FX/Leaf");
            var sparkMat = Resources.Load<Material>("FX/MuzzleSpark");
            wingMat = Resources.Load<Material>("FX/Wing");
            if (leafMat != null) leaves = BuildLeaves(leafMat);
            if (sparkMat != null) pollen = BuildPollen(sparkMat);
            if (wingMat != null) for (int i = 0; i < ButterflyCount; i++) butterflies.Add(MakeButterfly(i));
            if (leafMat == null || wingMat == null)
                Debug.LogWarning("[AmbientLife] Resources/FX/Leaf or Wing missing (menu: CatBattleground > Rebuild Weapon FX Assets)");
        }

        // ------------------------------------------------------------------ particles
        ParticleSystem BuildLeaves(Material mat)
        {
            var ps = NewSystem("Leaves", mat, 300);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 11f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.11f, 0.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.025f;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(LeafColors[0], 0f), new GradientColorKey(LeafColors[1], 0.3f), new GradientColorKey(LeafColors[4], 0.55f),
                                 new GradientColorKey(LeafColors[2], 0.8f), new GradientColorKey(LeafColors[3], 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            main.startColor = new ParticleSystem.MinMaxGradient(grad) { mode = ParticleSystemGradientMode.RandomColor };
            var em = ps.emission; em.enabled = true; em.rateOverTime = 10f;
            var shape = ps.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = new Vector3(44f, 2f, 44f);
            var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
            var w = SceneAtmosphere.WindDir * 1.1f;
            vel.x = new ParticleSystem.MinMaxCurve(w.x * 0.6f, w.x * 1.4f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.75f, -0.45f);
            vel.z = new ParticleSystem.MinMaxCurve(w.y * 0.6f, w.y * 1.4f);
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.9f; noise.frequency = 0.35f; noise.scrollSpeed = 0.25f; noise.octaveCount = 2;
            var rot = ps.rotationOverLifetime; rot.enabled = true; rot.z = new ParticleSystem.MinMaxCurve(-3.5f, 3.5f);
            Fade(ps, 0.08f, 0.85f);
            var r = ps.GetComponent<ParticleSystemRenderer>(); r.alignment = ParticleSystemRenderSpace.View;
            return ps;
        }

        ParticleSystem BuildPollen(Material spark)
        {
            var mat = new Material(spark); mat.SetColor("_BaseColor", new Color(1.6f, 1.45f, 1.05f));
            var ps = NewSystem("Pollen", mat, 400);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.055f);
            main.startColor = new Color(1f, 0.95f, 0.75f, 0.85f);
            var em = ps.emission; em.enabled = true; em.rateOverTime = 34f;
            var shape = ps.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = new Vector3(26f, 5f, 26f);
            var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.World;
            var w = SceneAtmosphere.WindDir * 0.35f;
            vel.x = new ParticleSystem.MinMaxCurve(w.x * 0.5f, w.x * 1.5f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.05f, 0.12f);
            vel.z = new ParticleSystem.MinMaxCurve(w.y * 0.5f, w.y * 1.5f);
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.35f; noise.frequency = 0.5f; noise.scrollSpeed = 0.2f;
            Fade(ps, 0.25f, 0.7f);
            // twinkle: size pulses
            var size = ps.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.25f, 1.2f), new Keyframe(0.5f, 0.7f), new Keyframe(0.75f, 1.1f), new Keyframe(1f, 0.5f)));
            return ps;
        }

        ParticleSystem NewSystem(string name, Material mat, int max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true; main.playOnAwake = true; main.duration = 5f; main.prewarm = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f; main.maxParticles = max;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat; r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        static void Fade(ParticleSystem ps, float inUntil, float outFrom)
        {
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, inUntil), new GradientAlphaKey(1f, outFrom), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        // ------------------------------------------------------------------ butterflies
        Butterfly MakeButterfly(int i)
        {
            var b = new Butterfly();
            b.root = new GameObject("Butterfly" + i).transform;
            b.root.SetParent(transform, false);
            float size = Random.Range(0.09f, 0.14f);
            var color = WingColors[i % WingColors.Length];
            b.wingL = Wing(b.root, size, color, true);
            b.wingR = Wing(b.root, size, color, false);
            // little dark body
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(b.root, false);
            body.transform.localScale = new Vector3(size * 0.12f, size * 0.45f, size * 0.12f);
            body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var br = body.GetComponent<Renderer>();
            br.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(0.08f, 0.06f, 0.05f) };
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            b.speed = Random.Range(1.1f, 1.9f);
            b.flapRate = Random.Range(9f, 13f);
            b.phase = Random.value * 10f;
            b.root.gameObject.SetActive(false);
            return b;
        }

        Transform Wing(Transform parent, float size, Color color, bool left)
        {
            var hinge = new GameObject(left ? "WingL" : "WingR").transform;
            hinge.SetParent(parent, false);
            var quad = new GameObject("Quad");
            quad.transform.SetParent(hinge, false);
            var mf = quad.AddComponent<MeshFilter>();
            mf.sharedMesh = WingMesh(left);
            var mr = quad.AddComponent<MeshRenderer>();
            mr.sharedMaterial = wingMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", color); mr.SetPropertyBlock(block);
            hinge.localScale = Vector3.one * size;
            return hinge;
        }

        static Mesh leftWing, rightWing;
        // a quad lying flat (XZ), hinge along the body (local z axis), wing tip towards -x (left) or +x (right); front = +z
        static Mesh WingMesh(bool left)
        {
            if (left && leftWing != null) return leftWing;
            if (!left && rightWing != null) return rightWing;
            float s = left ? -1f : 1f;
            var m = new Mesh { name = left ? "WingL" : "WingR" };
            m.vertices = new[] { new Vector3(0, 0, -0.5f), new Vector3(s, 0, -0.5f), new Vector3(s, 0, 0.5f), new Vector3(0, 0, 0.5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = left ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals(); m.RecalculateBounds();
            if (left) leftWing = m; else rightWing = m;
            return m;
        }

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            var cp = cam.transform.position;
            float ground = GroundY(cp);
            bool low = cp.y - ground < 45f;     // not from the plane / free fall

            if (ambience == null) ambience = GameAudio.Loop("ambient_loop", null, 0f);
            if (ambience != null) ambience.volume = Mathf.MoveTowards(ambience.volume, low ? 0.4f : 0.08f, Time.deltaTime * 0.3f);
            if (leaves != null) Follow(leaves, new Vector3(cp.x, ground + 9f, cp.z), low);
            if (pollen != null) Follow(pollen, new Vector3(cp.x, ground + 2.5f, cp.z), low);

            foreach (var b in butterflies) UpdateButterfly(b, cp, low);
        }

        static void Follow(ParticleSystem ps, Vector3 pos, bool on)
        {
            ps.transform.position = pos;
            var em = ps.emission;
            if (em.enabled != on) em.enabled = on;
        }

        void UpdateButterfly(Butterfly b, Vector3 cp, bool low)
        {
            var go = b.root.gameObject;
            if (!low) { if (go.activeSelf) go.SetActive(false); return; }
            var flat = b.root.position - cp; flat.y = 0f;
            if (!go.activeSelf || flat.magnitude > ButterflyRange)
            {
                // (re)appear somewhere around the camera, low over the ground
                var p = RandomAround(cp, 6f, ButterflyRange * 0.8f);
                b.root.position = p;
                b.target = RandomAround(p, 2f, 6f);
                go.SetActive(true);
            }
            var pos = b.root.position;
            b.retarget -= Time.deltaTime;
            if (b.retarget <= 0f || (b.target - pos).sqrMagnitude < 0.3f)
            {
                b.target = RandomAround(pos, 1.5f, 6f);
                b.retarget = Random.Range(1.5f, 4f);
            }
            float t = Time.time + b.phase;
            var to = b.target - pos;
            var dir = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            // fluttery path: bob up and down with the wing beats, a little wind drift
            var wind = new Vector3(SceneAtmosphere.WindDir.x, 0f, SceneAtmosphere.WindDir.y) * 0.25f;
            var vel = dir * b.speed + Vector3.up * Mathf.Sin(t * b.flapRate * 0.5f) * 0.6f + wind;
            pos += vel * Time.deltaTime;
            float g = GroundY(pos);
            pos.y = Mathf.Clamp(pos.y, g + 0.25f, g + 2.4f);
            b.root.position = pos;
            var look = new Vector3(vel.x, 0f, vel.z);
            if (look.sqrMagnitude > 1e-4f) b.root.rotation = Quaternion.Slerp(b.root.rotation, Quaternion.LookRotation(look), Time.deltaTime * 4f);
            // flap: wings swing up around the body axis (z); sometimes glide with the wings open
            float glide = Mathf.PerlinNoise(b.phase, Time.time * 0.4f) > 0.68f ? 0.25f : 1f;
            float flap = Mathf.Lerp(10f, 75f, (Mathf.Sin(t * b.flapRate * Mathf.PI * 2f * glide) * 0.5f + 0.5f));
            b.wingL.localRotation = Quaternion.Euler(0f, 0f, -flap);
            b.wingR.localRotation = Quaternion.Euler(0f, 0f, flap);
        }

        static Vector3 RandomAround(Vector3 c, float min, float max)
        {
            var d = Random.insideUnitCircle.normalized * Random.Range(min, max);
            var p = new Vector3(c.x + d.x, 0f, c.z + d.y);
            p.y = GroundY(p) + Random.Range(0.4f, 1.6f);
            return p;
        }

        static float GroundY(Vector3 p)
        {
            if (Physics.Raycast(new Vector3(p.x, 300f, p.z), Vector3.down, out var hit, 600f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return 0f;
        }
    }
}
