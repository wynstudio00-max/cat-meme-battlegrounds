using System.Collections.Generic;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// The drop plane every match starts on: the user's C-130 style model (Resources/Vehicles/DropPlane.fbx, exported from
    /// CHARACTER/dropship_plane.blend), scaled to <see cref="PlaneLength"/>, propellers spinning; falls back to the old primitive
    /// airship when the model is missing. Pure visual, positioned from the replicated route on every client. The cats ride
    /// hidden inside it (PlayerDrop) and jump out of the rear at <see cref="ExitOffset"/>.
    /// </summary>
    public class DropShipVisual : MonoBehaviour
    {
        /// <summary>Length of the plane in the game (metres); the model is scaled to it.</summary>
        public const float PlaneLength = 24f;
        /// <summary>Where the cats leave the plane: behind the centre (rear ramp) and below it.</summary>
        public static readonly Vector3 ExitOffset = new Vector3(0f, -2.5f, -PlaneLength * 0.42f);

        GameObject body;
        AudioSource engine;
        Transform propeller;
        readonly List<Transform> props = new List<Transform>();

        void Awake()
        {
            body = new GameObject("Body");
            body.transform.SetParent(transform, false);
            var model = Resources.Load<GameObject>("Vehicles/DropPlane");   // the user's plane (CHARACTER/dropship_plane.blend)
            if (model != null) BuildPlane(model, body.transform);
            else BuildShip(body.transform);                                 // fallback: the old primitive airship
            body.SetActive(false);
        }

        void BuildPlane(GameObject model, Transform root)
        {
            var plane = Instantiate(model, root);
            plane.name = "DropPlane";
            foreach (var c in plane.GetComponentsInChildren<Collider>()) Destroy(c);
            foreach (var r in plane.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // scale to PlaneLength along +Z (the nose) and centre the model on the route point
            var b = Bounds(plane);
            float k = PlaneLength / Mathf.Max(0.01f, b.size.z);
            plane.transform.localScale *= k;
            b = Bounds(plane);
            plane.transform.position -= b.center - root.position;
            foreach (var t in plane.GetComponentsInChildren<Transform>())
                if (t.name.ToUpperInvariant().StartsWith("BALING")) props.Add(t);
        }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds(go.transform.position, Vector3.one);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        void Update()
        {
            var match = MatchManager.Instance;
            bool show = match != null && match.IsSpawned && match.ShipVisible;
            if (body.activeSelf != show) body.SetActive(show);
            if (show && engine == null) engine = GameAudio.Loop("plane_loop", body.transform, 1f, 30f, 650f);   // stops with the body
            if (!show) return;

            // a little life: slow bank and bob while it flies
            float t = Time.time;
            var rot = Quaternion.LookRotation(match.ShipDirection, Vector3.up) * Quaternion.Euler(Mathf.Sin(t * 0.7f) * 1.2f, 0f, Mathf.Sin(t * 0.5f) * 2.5f);
            transform.SetPositionAndRotation(match.ShipPosition + Vector3.up * Mathf.Sin(t * 0.9f) * 0.4f, rot);
            if (propeller != null) propeller.Rotate(0f, 0f, 720f * Time.deltaTime, Space.Self);
            var fwd = transform.forward;
            foreach (var p in props) p.RotateAround(p.position, fwd, 1500f * Time.deltaTime);
        }

        // Local space: +z is the bow, y = 0 is the deck the cats stand on.
        void BuildShip(Transform root)
        {
            var wood = Mat(new Color(0.55f, 0.36f, 0.2f));
            var darkWood = Mat(new Color(0.38f, 0.24f, 0.14f));
            var cloth = Mat(new Color(0.93f, 0.9f, 0.82f));
            var red = Mat(new Color(0.85f, 0.25f, 0.22f));
            var metal = Mat(new Color(0.55f, 0.58f, 0.62f));

            Part(root, PrimitiveType.Cube, "Hull", darkWood, new Vector3(0f, -1.3f, 0f), new Vector3(6.4f, 2.6f, 18f));
            Part(root, PrimitiveType.Cube, "Keel", wood, new Vector3(0f, -2.9f, -0.5f), new Vector3(3.2f, 1.2f, 15f));
            Part(root, PrimitiveType.Cube, "Bow", darkWood, new Vector3(0f, -1.0f, 9.6f), new Vector3(3.6f, 2.0f, 3f)).transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            Part(root, PrimitiveType.Cube, "Deck", wood, new Vector3(0f, -0.05f, 0f), new Vector3(6.0f, 0.12f, 17.5f));

            // rails
            Part(root, PrimitiveType.Cube, "RailL", wood, new Vector3(-3.0f, 0.55f, -0.5f), new Vector3(0.15f, 1.1f, 15f));
            Part(root, PrimitiveType.Cube, "RailR", wood, new Vector3(3.0f, 0.55f, -0.5f), new Vector3(0.15f, 1.1f, 15f));
            Part(root, PrimitiveType.Cube, "RailBack", wood, new Vector3(0f, 0.55f, -8.2f), new Vector3(6.0f, 1.1f, 0.15f));

            // cabin at the stern
            Part(root, PrimitiveType.Cube, "Cabin", wood, new Vector3(0f, 1.3f, -6.2f), new Vector3(4.4f, 2.6f, 3.4f));
            Part(root, PrimitiveType.Cube, "CabinRoof", red, new Vector3(0f, 2.75f, -6.2f), new Vector3(5.0f, 0.3f, 4.0f));

            // mast and balloon
            Part(root, PrimitiveType.Cylinder, "Mast", darkWood, new Vector3(0f, 4.5f, 1.5f), new Vector3(0.25f, 4.5f, 0.25f));
            Part(root, PrimitiveType.Sphere, "Balloon", red, new Vector3(0f, 11.5f, 0.5f), new Vector3(11f, 8.5f, 17f));
            Part(root, PrimitiveType.Sphere, "BalloonStripe", cloth, new Vector3(0f, 11.5f, 0.5f), new Vector3(11.2f, 3.0f, 17.2f));

            // ropes from the balloon down to the rails
            for (int i = 0; i < 4; i++)
            {
                float sx = i % 2 == 0 ? -1f : 1f, sz = i < 2 ? -4.5f : 5.5f;
                var rope = Part(root, PrimitiveType.Cylinder, "Rope" + i, metal, new Vector3(sx * 2.0f, 4.2f, sz * 0.9f + 0.5f), new Vector3(0.08f, 4.4f, 0.08f));
                rope.transform.localRotation = Quaternion.Euler(sz < 0f ? 5f : -5f, 0f, sx * 20f);
            }

            // propeller at the stern
            Part(root, PrimitiveType.Cylinder, "PropHub", metal, new Vector3(0f, 0.9f, -9.1f), new Vector3(0.5f, 0.2f, 0.5f)).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            propeller = new GameObject("Propeller").transform;
            propeller.SetParent(root, false);
            propeller.localPosition = new Vector3(0f, 0.9f, -9.4f);
            Part(propeller, PrimitiveType.Cube, "Blade1", metal, Vector3.zero, new Vector3(3.2f, 0.35f, 0.08f));
            Part(propeller, PrimitiveType.Cube, "Blade2", metal, Vector3.zero, new Vector3(0.35f, 3.2f, 0.08f));

            // flag on the mast
            Part(root, PrimitiveType.Cube, "Flag", red, new Vector3(0f, 8.3f, 1.1f), new Vector3(0.05f, 0.8f, 1.3f));
        }

        static GameObject Part(Transform parent, PrimitiveType type, string name, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Destroy(go.GetComponent<Collider>());
            return go;
        }

        static Material Mat(Color color)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            mat.SetFloat("_Smoothness", 0.1f);
            return mat;
        }
    }
}
