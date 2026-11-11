using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Local player only. After you are eliminated in a match the camera follows a cat that is still alive:
    /// Tab / mouse click cycles through them, the mouse orbits. Shows your placement. Back to normal once you
    /// are alive again (new match). Nothing here reveals who is a bot: cats are only numbered by their position in the list.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class SpectateController : MonoBehaviour
    {
        [SerializeField] float delayAfterDeath = 2.5f;   // let the death animation play first

        Health health;
        ThirdPersonCamera cam;
        float deadSince = -1f;
        int placement;
        int index;
        readonly List<NetworkPlayer> candidates = new List<NetworkPlayer>();

        public bool IsSpectating => cam != null && cam.SpectateTarget != null;
        /// <summary>The cat being watched (null when not spectating) and how many are left to watch.</summary>
        public NetworkPlayer Target => IsSpectating && index < candidates.Count ? candidates[index] : null;
        public int CandidateCount => candidates.Count;
        /// <summary>UI buttons: watch the next / previous cat.</summary>
        public void Step(int delta) => index += delta;

        void Awake() => health = GetComponent<Health>();

        void OnDisable()
        {
            if (cam != null) cam.SetSpectateTarget(null);
        }

        void Update()
        {
            var match = MatchManager.Instance;
            bool eliminated = health.IsDead && match != null && match.State == MatchState.Playing;

            if (!eliminated)
            {
                deadSince = -1f;
                if (cam != null && cam.SpectateTarget != null) cam.SetSpectateTarget(null);
                return;
            }

            if (deadSince < 0f)
            {
                deadSince = Time.time;
                int alive = 0;   // cats still alive right now (our own Health already says dead)
                foreach (var p in FindObjectsByType<NetworkPlayer>())
                    if (p.gameObject != gameObject && !p.Health.IsDead) alive++;
                placement = alive + 1;
                index = 0;
            }
            if (Time.time - deadSince < delayAfterDeath) return;

            if (cam == null) cam = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
            if (cam == null) return;

            RefreshCandidates();
            if (candidates.Count == 0) { cam.SetSpectateTarget(null); return; }

            bool next = GameInput.NextPressed;
            bool previous = GameInput.PreviousPressed;
            if (next) index++;
            if (previous) index--;
            index = ((index % candidates.Count) + candidates.Count) % candidates.Count;

            var target = candidates[index];
            if (cam.SpectateTarget != target.transform) cam.SetSpectateTarget(target.transform);
        }

        void RefreshCandidates()
        {
            // Keep following the same cat while it lives; the list order is stable (by network object id).
            var current = cam.SpectateTarget;
            candidates.Clear();
            foreach (var p in FindObjectsByType<NetworkPlayer>())
                if (p.gameObject != gameObject && !p.Health.IsDead) candidates.Add(p);
            candidates.Sort((a, b) => a.NetworkObjectId.CompareTo(b.NetworkObjectId));
            if (current != null)
            {
                int i = candidates.FindIndex(p => p.transform == current);
                if (i >= 0) index = i;
            }
        }

        void OnGUI()
        {
            if (GameUI.Active) return;
            if (!IsSpectating) return;
            var big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 30, fontStyle = FontStyle.Bold };
            var small = new GUIStyle(big) { fontSize = 18 };
            GUI.Label(new Rect(0, Screen.height * 0.12f, Screen.width, 44), "ELIMINATED  -  you placed #" + placement, big);
            GUI.Label(new Rect(0, Screen.height * 0.12f + 42, Screen.width, 28),
                "Spectating " + (index + 1) + "/" + candidates.Count + "   (Tab / click = next cat, mouse = look around)", small);
        }
    }
}
