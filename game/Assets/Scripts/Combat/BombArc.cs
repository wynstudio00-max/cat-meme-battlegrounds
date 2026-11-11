using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// The owner's aiming aid while G is held (BombThrower, 2026-10-28): the flight path the bomb will take (same gravity and drag
    /// as its Rigidbody, up to the first thing it touches) and a circle the size of the blast where it lands. Local only.
    /// </summary>
    public class BombArc : MonoBehaviour
    {
        const int MaxPoints = 90, RingPoints = 48;
        const float Step = 0.025f, BallRadius = Bomb.Radius * Bomb.Scale;
        static readonly Color Lime = new Color(0.8f, 1f, 0f);

        LineRenderer path, ring;
        readonly Vector3[] pts = new Vector3[MaxPoints];
        readonly Vector3[] ringPts = new Vector3[RingPoints + 1];

        public static BombArc Create()
        {
            var go = new GameObject("BombArc");
            var a = go.AddComponent<BombArc>();
            a.path = Line(go.transform, "Path", 0.07f, 0.03f);
            a.ring = Line(go.transform, "Blast", 0.08f, 0.08f);
            a.ring.loop = true;
            a.Hide();
            return a;
        }

        static LineRenderer Line(Transform parent, string name, float w0, float w1)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startWidth = w0; lr.endWidth = w1;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            lr.alignment = LineAlignment.View;
            return lr;
        }

        public void Hide()
        {
            if (path != null) path.enabled = false;
            if (ring != null) ring.enabled = false;
        }

        public void Show(Vector3 start, Vector3 velocity, Transform thrower, float blastRadius)
        {
            var p = start; var v = velocity; var g = Physics.gravity;
            int n = 0; pts[n++] = p;
            bool landed = false; Vector3 hitPoint = p, hitNormal = Vector3.up;
            while (n < MaxPoints)
            {
                var next = p + v * Step;
                var seg = next - p;
                if (Cast(p, seg, thrower, out var hit))
                {
                    pts[n++] = hit.point; landed = true; hitPoint = hit.point; hitNormal = hit.normal;
                    break;
                }
                p = next; v += g * Step; v *= 1f - 0.05f * Step;   // Rigidbody linearDamping 0.05
                pts[n++] = p;
            }
            path.enabled = true;
            path.positionCount = n;
            path.SetPositions(pts);
            var c = Lime; c.a = 0.9f; path.startColor = c; c.a = 0.35f; path.endColor = c;

            ring.enabled = landed;
            if (!landed) return;
            var up = hitNormal.y > 0.3f ? hitNormal : Vector3.up;
            var t1 = Vector3.Cross(up, Mathf.Abs(up.x) < 0.9f ? Vector3.right : Vector3.forward).normalized;
            var t2 = Vector3.Cross(up, t1);
            for (int i = 0; i <= RingPoints; i++)
            {
                float a = i * Mathf.PI * 2f / RingPoints;
                ringPts[i] = hitPoint + up * 0.08f + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * blastRadius;
            }
            ring.positionCount = RingPoints + 1;
            ring.SetPositions(ringPts);
            var rc = new Color(1f, 0.35f, 0.25f, 0.55f + 0.25f * Mathf.Sin(Time.time * 8f));
            ring.startColor = rc; ring.endColor = rc;
        }

        // first solid thing along the segment that is not the thrower (bombs ignore their thrower)
        static bool Cast(Vector3 from, Vector3 seg, Transform thrower, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue; bool any = false;
            foreach (var h in Physics.SphereCastAll(from, BallRadius, seg.normalized, seg.magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                if (thrower != null && h.collider.transform.IsChildOf(thrower)) continue;
                if (h.distance <= 0f && h.point == Vector3.zero) continue;   // started inside: skip
                if (h.distance < bestDist) { bestDist = h.distance; best = h; any = true; }
            }
            return any;
        }

        void OnDestroy()
        {
            if (path != null) Destroy(path.material);
            if (ring != null) Destroy(ring.material);
        }
    }
}
