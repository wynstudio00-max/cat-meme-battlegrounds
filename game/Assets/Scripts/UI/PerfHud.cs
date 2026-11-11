using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Frame-rate readout for the WebGL measuring pass (2026-10-28). F3 toggles it; in the browser it also starts on with "?perf=1"
    /// in the page URL. Shows the average FPS (last 0.5 s), the worst FPS of the last 5 s, frame time, how many zombies / players are
    /// around and the screen size, and while it is on writes a "PERF ..." line to the console (browser dev tools) every 10 s with the
    /// average and the 1% low of that stretch - easy to copy into a message.
    /// </summary>
    public class PerfHud : MonoBehaviour
    {
        static PerfHud instance;
        bool shown;
        float acc, accTime, shownFps, shownMs;
        readonly Queue<(float time, float dt)> recent = new Queue<(float, float)>();
        readonly List<float> window = new List<float>();
        float windowStart, nextCount;
        int zombies, players;   // counted once a second (FindObjectsByType every frame would cost frame time itself)
        GUIStyle style;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            instance = new GameObject("PerfHud").AddComponent<PerfHud>();
            DontDestroyOnLoad(instance.gameObject);
            instance.shown = Application.absoluteURL.Contains("perf=1");
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f3Key.wasPressedThisFrame) { shown = !shown; window.Clear(); windowStart = Time.unscaledTime; }
            float dt = Time.unscaledDeltaTime;
            acc += dt; accTime += 1f;
            if (acc >= 0.5f) { shownFps = accTime / acc; shownMs = acc / accTime * 1000f; acc = 0f; accTime = 0f; }
            recent.Enqueue((Time.unscaledTime, dt));
            while (recent.Count > 0 && Time.unscaledTime - recent.Peek().time > 5f) recent.Dequeue();
            if (!shown) return;
            if (Time.unscaledTime >= nextCount) { nextCount = Time.unscaledTime + 1f; zombies = Count<Zombie>(); players = Count<NetworkPlayer>(); }

            window.Add(dt);
            if (Time.unscaledTime - windowStart >= 10f && window.Count > 10)
            {
                window.Sort();
                float sum = 0f; foreach (var d in window) sum += d;
                float avg = window.Count / sum;
                float low1 = 1f / window[Mathf.Clamp(Mathf.CeilToInt(window.Count * 0.99f) - 1, 0, window.Count - 1)];
                Debug.Log($"PERF fps avg {avg:0.0} | 1% low {low1:0.0} | {zombies} zombies | {players} players | {Screen.width}x{Screen.height} | quality {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
                window.Clear(); windowStart = Time.unscaledTime;
            }
        }

        static int Count<T>() where T : Object => FindObjectsByType<T>(FindObjectsSortMode.None).Length;

        void OnGUI()
        {
            if (!shown) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            float worst = 0f; foreach (var r in recent) worst = Mathf.Max(worst, r.dt);
            var text = $"FPS {shownFps:0}  ({shownMs:0.0} ms)   worst 5s: {(worst > 0 ? 1f / worst : 0):0}\n" +
                       $"zombies {zombies}  players {players}   {Screen.width}x{Screen.height}   F3 hides";
            var rect = new Rect(10, Screen.height - 58, 520, 48);
            GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(new Rect(rect.x - 4, rect.y - 2, rect.width, rect.height + 4), Texture2D.whiteTexture);
            GUI.color = shownFps >= 50 ? new Color(0.7f, 1f, 0.3f) : shownFps >= 30 ? new Color(1f, 0.85f, 0.3f) : new Color(1f, 0.4f, 0.35f);
            GUI.Label(rect, text, style);
            GUI.color = Color.white;
        }
    }
}
