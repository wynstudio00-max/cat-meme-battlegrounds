using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>Placeholder IMGUI match info: state, timers, players alive, zone phase, "outside zone" warning, winner.</summary>
    public class MatchHud : MonoBehaviour
    {
        MatchManager match;
        ZoneController zone;

        void Awake()
        {
            match = GetComponent<MatchManager>();
            zone = GetComponent<ZoneController>();
        }

        void OnGUI()
        {
            if (GameUI.Active) return;
            if (match == null || !match.IsSpawned) return;
            var nm = NetworkManager.Singleton;
            var big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 26, fontStyle = FontStyle.Bold };
            var small = new GUIStyle(big) { fontSize = 18 };
            float w = Screen.width;

            switch (match.State)
            {
                case MatchState.Waiting:
                    string waiting = match.HumanCount == 0
                        ? "Waiting for players"
                        : "Finding players...  " + FormatTime(match.Timer);
                    GUI.Label(new Rect(0, 8, w, 40), waiting, big);
                    GUI.Label(new Rect(0, 44, w, 30), "Players " + match.HumanCount + "/" + match.MatchSize + "   |   Mode: Solo  (Duo / Squad coming soon)", small);
                    if (nm.IsServer && GUI.Button(new Rect(w * 0.5f - 70, 78, 140, 30), "Start now")) match.StartNow();
                    break;

                case MatchState.Countdown:
                    GUI.Label(new Rect(0, Screen.height * 0.3f, w, 80), Mathf.CeilToInt(match.Timer).ToString(), new GUIStyle(big) { fontSize = 72 });
                    GUI.Label(new Rect(0, 8, w, 40), "Boarding the drop ship...", big);
                    break;

                case MatchState.Playing:
                    GUI.Label(new Rect(0, 8, w, 36), "Alive: " + match.AliveCount, big);
                    DrawDropHint(nm, small);
                    if (zone != null && zone.Active)
                    {
                        string z = zone.Shrinking
                            ? "Zone closing  " + Mathf.CeilToInt(zone.SecondsLeft) + "s"
                            : "Zone " + zone.PhaseNumber + " shrinks in " + Mathf.CeilToInt(zone.SecondsLeft) + "s";
                        GUI.Label(new Rect(0, 44, w, 30), z, small);
                        DrawOutsideWarning(nm);
                    }
                    break;

                case MatchState.Ended:
                    GUI.Label(new Rect(0, Screen.height * 0.3f, w, 80), match.ResultText, new GUIStyle(big) { fontSize = 48 });
                    GUI.Label(new Rect(0, Screen.height * 0.3f + 70, w, 30), "New match in " + Mathf.CeilToInt(match.Timer) + "s", small);
                    break;
            }
        }

        static string FormatTime(float seconds)
        {
            int s = Mathf.CeilToInt(seconds);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // Controls reminder for the local player while dropping in.
        void DrawDropHint(NetworkManager nm, GUIStyle style)
        {
            var local = nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
            var drop = local != null ? local.GetComponent<PlayerDrop>() : null;
            if (drop == null) return;
            string hint = null;
            switch (drop.State)
            {
                case DropState.OnShip: hint = match.CanJump ? "Press SPACE to jump!" : "The ship is on its way to the island..."; break;
                case DropState.Freefall: hint = "Free-falling: W to glide forward, SPACE to open the parachute"; break;
                case DropState.Parachute: hint = "Parachute open: W to steer forward, aim with the mouse"; break;
            }
            if (hint != null) GUI.Label(new Rect(0, Screen.height * 0.78f, Screen.width, 36), hint, style);
        }

        void DrawOutsideWarning(NetworkManager nm)
        {
            var local = nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
            if (local == null || !zone.IsOutside(local.transform.position)) return;
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 30, fontStyle = FontStyle.Bold };
            style.normal.textColor = new Color(1f, 0.35f, 0.3f);
            GUI.Label(new Rect(0, Screen.height * 0.72f, Screen.width, 40), "OUTSIDE THE ZONE - move inside!", style);
        }
    }
}
