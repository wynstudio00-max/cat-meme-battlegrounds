using System.Collections.Generic;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// One thrown bomb (2026-10-27). Not a network object: <see cref="BombThrower"/> tells every machine the start point and
    /// velocity, each one simulates the same bouncing ball with its own Rigidbody, and only the server decides where it blows up
    /// (its copy's position) and sends that to everyone. Model: Resources/Weapons/Bomb.fbx (Tools/BlenderAnim/make_bomb.py),
    /// coloured here by material name; a primitive sphere if the model is missing. A burning fuse (sparks, small light, hiss).
    /// </summary>
    public class Bomb : MonoBehaviour
    {
        public const float Radius = 0.1f;     // model radius in metres (before Scale)
        public const float Scale = 1.4f;      // a little bigger than real so it reads at a distance

        static readonly Dictionary<ulong, Bomb> live = new Dictionary<ulong, Bomb>();
        static GameObject model;
        static Material body, accent, metal, fuse;
        static PhysicsMaterial bounce;

        ulong id;
        Rigidbody rb;
        Light sparkLight;
        AudioSource hiss;
        float nextBounceSound;

        public static Bomb Find(ulong id) => live.TryGetValue(id, out var b) && b != null ? b : null;

        /// <summary>Just the coloured model, no physics or fuse (the bomb held in the paw before the throw).</summary>
        public static GameObject CreateVisual(string name)
        {
            if (model == null) model = Resources.Load<GameObject>("Weapons/Bomb");
            GameObject go;
            bool primitive = model == null;
            if (!primitive) { go = Instantiate(model); go.transform.localScale = Vector3.one * Scale; }
            else { go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.transform.localScale = Vector3.one * Radius * 2f; Object.Destroy(go.GetComponent<Collider>()); }
            go.name = name;
            PaintRenderers(go, primitive);
            foreach (var c in go.GetComponentsInChildren<Collider>()) Object.Destroy(c);
            return go;
        }

        public static Bomb Spawn(ulong id, Vector3 position, Vector3 velocity, Transform thrower, float lifetime)
        {
            Remove(id);
            if (model == null) model = Resources.Load<GameObject>("Weapons/Bomb");
            GameObject go;
            if (model != null) go = Instantiate(model);
            else { go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.transform.localScale = Vector3.one * Radius * 2f; Object.Destroy(go.GetComponent<Collider>()); }
            go.name = "Bomb_" + id;
            go.transform.SetPositionAndRotation(position, Random.rotation);
            if (model != null) go.transform.localScale = Vector3.one * Scale;
            var b = go.AddComponent<Bomb>();
            b.id = id;
            b.Setup(velocity, thrower, model == null);
            live[id] = b;
            Object.Destroy(go, lifetime);
            return b;
        }

        public static void Remove(ulong id)
        {
            if (live.TryGetValue(id, out var b)) { live.Remove(id); if (b != null) Object.Destroy(b.gameObject); }
        }

        void OnDestroy() { if (live.TryGetValue(id, out var b) && b == this) live.Remove(id); }

        void Setup(Vector3 velocity, Transform thrower, bool primitive)
        {
            PaintRenderers(gameObject, primitive);
            var col = gameObject.AddComponent<SphereCollider>();
            col.radius = primitive ? 0.5f : Radius;
            if (bounce == null) bounce = new PhysicsMaterial("Bomb") { bounciness = 0.35f, dynamicFriction = 0.6f, staticFriction = 0.7f, bounceCombine = PhysicsMaterialCombine.Maximum };
            col.sharedMaterial = bounce;
            rb = gameObject.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.6f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearVelocity = velocity;
            rb.angularVelocity = Random.insideUnitSphere * 8f;
            if (thrower != null)   // never bounce off the cat that threw it
                foreach (var c in thrower.GetComponentsInChildren<Collider>(true)) Physics.IgnoreCollision(col, c, true);

            if (Application.isBatchMode) return;
            // burning fuse at the top of the ball
            var tip = new GameObject("Fuse").transform;
            tip.SetParent(transform, false);
            tip.localPosition = new Vector3(0.04f, Radius + 0.075f, 0f);
            var sparkMat = Resources.Load<Material>("FX/MuzzleSpark");
            if (sparkMat != null)
            {
                var ps = tip.gameObject.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.loop = true; main.startLifetime = 0.25f; main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.06f); main.gravityModifier = 0.6f;
                main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 60;
                main.startColor = new Color(1f, 0.85f, 0.4f);
                var em = ps.emission; em.rateOverTime = 45f;
                var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.01f;
                var r = tip.GetComponent<ParticleSystemRenderer>();
                var m = new Material(sparkMat); m.SetColor("_BaseColor", new Color(4f, 2.6f, 1f));
                r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Play();
            }
            sparkLight = tip.gameObject.AddComponent<Light>();
            sparkLight.type = LightType.Point; sparkLight.color = new Color(1f, 0.7f, 0.35f); sparkLight.range = 2.2f;
            sparkLight.shadows = LightShadows.None;
            hiss = GameAudio.Loop("bomb_fuse_loop", transform, 0.55f, 1.5f, 18f);
        }

        void Update()
        {
            if (sparkLight != null) sparkLight.intensity = 1.5f + Mathf.PerlinNoise(Time.time * 18f, id) * 2.5f;
        }

        void OnCollisionEnter(Collision c)
        {
            float v = c.relativeVelocity.magnitude;
            if (v < 1.5f || Time.time < nextBounceSound || Application.isBatchMode) return;
            nextBounceSound = Time.time + 0.12f;
            GameAudio.Play("bomb_bounce", transform.position, Mathf.Clamp01(v / 10f), 2f, 30f);
        }

        // colours by material name (the FBX materials do not survive URP import reliably)
        static void PaintRenderers(GameObject go, bool primitive)
        {
            if (body == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                Material M(Color c, float smooth, float metallic)
                {
                    var m = new Material(shader); m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metallic);
                    return m;
                }
                body = M(new Color(0.035f, 0.04f, 0.06f), 0.65f, 0f);
                accent = M(new Color(0.8f, 1f, 0f), 0.4f, 0f);   // #CCFF00
                metal = M(new Color(0.55f, 0.55f, 0.58f), 0.6f, 0.8f);
                fuse = M(new Color(0.45f, 0.3f, 0.16f), 0.1f, 0f);
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (primitive) { r.sharedMaterial = body; continue; }
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var n = mats[i] != null ? mats[i].name : "";
                    mats[i] = n.Contains("Accent") ? accent : n.Contains("Metal") ? metal : n.Contains("Fuse") ? fuse : body;
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }
    }
}
