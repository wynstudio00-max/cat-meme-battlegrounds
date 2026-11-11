using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// How a pickup lies in the world (purely cosmetic, every client). 2026-10-28 (user: no glow, items just lying on the floor like
    /// PUBG): no beam, ring, sparkles, spin or bob any more. The item rests on whatever is under it, turned to a random heading,
    /// tipped a little and nudged off its point, so a room's loot looks dropped rather than placed. The randomness comes from the
    /// item's position, so every machine shows it lying the same way. Picking it up plays the pickup sound.
    /// </summary>
    public class LootVisual : MonoBehaviour
    {
        [SerializeField] Transform visual;

        static bool quitting;

        /// <summary>Colour per loot type (still used by the HUD / feedback).</summary>
        public static Color ColorOf(LootType t) =>
            t == LootType.Medkit ? new Color(0.35f, 1f, 0.45f) : t == LootType.Armor ? new Color(0.35f, 0.68f, 1f) : new Color(1f, 0.76f, 0.22f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { quitting = false; Application.quitting += () => quitting = true; }

        void Start()
        {
            if (visual == null) visual = transform;
            foreach (var t in GetComponentsInChildren<Transform>(true))   // prefabs built before this change still have a beam
                if (t.name == "Beam") t.gameObject.SetActive(false);
            Settle();
        }

        void Settle()
        {
            // the same "random" lie on every machine: seeded by where the item is
            var p = transform.position;
            var rng = new System.Random(Mathf.RoundToInt(p.x * 73.1f) ^ (Mathf.RoundToInt(p.z * 91.7f) << 10) ^ Mathf.RoundToInt(p.y * 17f));
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            var offset = new Vector3(R(-0.35f, 0.35f), 0f, R(-0.35f, 0.35f));
            float yaw = R(0f, 360f);
            float tiltX = R(-6f, 6f), tiltZ = R(-6f, 6f);

            // rest on the floor / ground under the nudged spot (ignore our own pickup trigger)
            var from = p + offset + Vector3.up * 0.6f;
            float ground = p.y - 0.1f;
            foreach (var hit in Physics.RaycastAll(from, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                if (!hit.collider.transform.IsChildOf(transform) && hit.collider.GetComponentInParent<Health>() == null && hit.point.y > ground - 0.5f && hit.point.y < from.y)
                { ground = Mathf.Max(ground, hit.point.y); }
            visual.position = new Vector3(p.x + offset.x, ground + 0.005f, p.z + offset.z);
            visual.rotation = Quaternion.Euler(tiltX, yaw, tiltZ);
        }

        void OnDestroy()
        {
            // picked up: only during a running match (not when the scene unloads or the match resets)
            if (quitting || Application.isBatchMode || !gameObject.scene.isLoaded) return;
            var match = MatchManager.Instance;
            if (match == null || match.State != MatchState.Playing) return;
            GameAudio.Play("pickup", transform.position + Vector3.up * 0.3f, 0.8f, 2f, 25f, 1f, 0.05f);
        }
    }
}
