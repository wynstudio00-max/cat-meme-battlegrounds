using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// Big centre-screen announcements with a stinger sound (own overlay canvas, client only, auto-created when a
    /// NetworkBootstrap exists). Watches the replicated match / zone state and queues:
    /// THE STORM IS COMING, NEXT ZONE REVEALED, ZONE SHRINKING!, 5 CATS LEFT, TOP 3!, FINAL DUEL!, VICTORY! (with confetti).
    /// Look: a dark military stripe slides in, the title punches in with an elastic bounce, holds, then slides out.
    /// </summary>
    public class Announcer : MonoBehaviour
    {
        static Announcer instance;

        struct Item { public string title, sub, sound; public Color color; public float hold; }
        readonly Queue<Item> queue = new Queue<Item>();
        Item current; float start = -100f; bool showing;

        RectTransform stripe; Image stripeImg, accentImg; Text title, sub;
        Font titleFont;

        // watched state
        bool zoneWasActive, wasShrinking; int lastPhase = -1, lastAlive = 999; MatchState lastState = MatchState.Waiting; bool victoryShown, sawPlaying;

        static readonly Color Accent = new Color(1f, 0.54f, 0.12f), Storm = new Color(0.78f, 0.55f, 1f), Gold = new Color(1f, 0.82f, 0.25f),
            Sand = new Color(0.96f, 0.92f, 0.82f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            instance = new GameObject("Announcer").AddComponent<Announcer>();
        }

        public static void Show(string title, string sub, Color color, string sound = "announce", float hold = 2.4f)
        {
            if (instance == null) return;
            instance.queue.Enqueue(new Item { title = title, sub = sub, color = color, sound = sound, hold = hold });
        }

        void Awake()
        {
            var builtIn = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleFont = Resources.Load<Font>("Fonts/Title") ?? builtIn;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;

            stripe = Node("Stripe", transform);
            stripe.anchoredPosition = new Vector2(0, 150);
            stripe.sizeDelta = new Vector2(1400, 104);
            stripeImg = stripe.gameObject.AddComponent<Image>();
            stripeImg.color = new Color(0.067f, 0.086f, 0.059f, 0.78f);
            stripeImg.raycastTarget = false;
            var accent = Node("Accent", stripe);
            accent.anchorMin = new Vector2(0, 0); accent.anchorMax = new Vector2(1, 0); accent.pivot = new Vector2(0.5f, 0);
            accent.sizeDelta = new Vector2(0, 5); accent.anchoredPosition = Vector2.zero;
            accentImg = accent.gameObject.AddComponent<Image>(); accentImg.raycastTarget = false;

            title = Label("Title", stripe, 58, new Vector2(0, 12));
            sub = Label("Sub", stripe, 22, new Vector2(0, -32));
            stripe.gameObject.SetActive(false);
        }

        RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        Text Label(string name, Transform parent, int size, Vector2 pos)
        {
            var t = Node(name, parent).gameObject.AddComponent<Text>();
            t.font = titleFont; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            t.fontStyle = titleFont.name == "LegacyRuntime" ? FontStyle.Bold : FontStyle.Normal;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.rectTransform.sizeDelta = new Vector2(1200, size + 12);
            t.rectTransform.anchoredPosition = pos;
            var o = t.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.04f, 0.03f, 0.02f, 0.9f); o.effectDistance = new Vector2(2.5f, -2.5f);
            var s = t.gameObject.AddComponent<Shadow>(); s.effectColor = new Color(0, 0, 0, 0.5f); s.effectDistance = new Vector2(4, -5);
            return t;
        }

        void Update()
        {
            Watch();
            Animate();
        }

        // ------------------------------------------------------------------ events from the replicated state
        void Watch()
        {
            var match = MatchManager.Instance;
            if (match == null || !match.IsSpawned) { zoneWasActive = wasShrinking = false; lastPhase = -1; lastAlive = 999; victoryShown = false; return; }
            var state = match.State;
            if (state != MatchState.Playing && state != MatchState.Ended) { zoneWasActive = wasShrinking = false; lastPhase = -1; lastAlive = 999; victoryShown = false; queue.Clear(); }

            var zone = match.Zone;
            if (zone != null && zone.IsSpawned && state == MatchState.Playing)
            {
                if (zone.Active && !zoneWasActive) Show("THE STORM IS COMING", "Stay inside the circle", Storm);
                else if (zone.Active && zone.PhaseNumber != lastPhase && lastPhase > 0 && !zone.Shrinking) Show("NEXT ZONE REVEALED", "Phase " + zone.PhaseNumber + " · check the map", Storm, "announce", 2f);
                if (zone.Active && zone.Shrinking && !wasShrinking) Show("ZONE SHRINKING!", "Get inside the circle", new Color(1f, 0.45f, 0.75f), "announce_alarm");
                zoneWasActive = zone.Active; wasShrinking = zone.Shrinking; lastPhase = zone.PhaseNumber;
            }

            if (state == MatchState.Playing)
            {
                int alive = match.AliveCount;
                if (alive < lastAlive && lastAlive != 999)
                {
                    if (alive == 2) Show("FINAL DUEL!", "Only you and one more cat", Accent);
                    else if (alive == 3) Show("TOP 3!", "Three cats left", Gold);
                    else if (alive == 5) Show("5 CATS LEFT", "Stay sharp", Sand);
                }
                lastAlive = alive;
            }

            if (state == MatchState.Playing) sawPlaying = true;
            // Placement may arrive a moment after the state: keep checking while the results are up
            if (state == MatchState.Ended && sawPlaying && !victoryShown)
            {
                var nm = NetworkManager.Singleton;
                var me = nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>() : null;
                if (me != null && me.Placement == 1)
                {
                    victoryShown = true; sawPlaying = false;
                    queue.Clear(); showing = false;
                    // the banner, fanfare and confetti now come from VictorySequence (after the slow-motion moment, 2026-11-07)
                    if (match.IsZombies) Show("VICTORY!", "TOP CAT", Gold, "victory", 3.5f);
                }
            }
            lastState = state;
        }

        // ------------------------------------------------------------------ animation
        void Animate()
        {
            if (!showing && queue.Count > 0)
            {
                current = queue.Dequeue();
                start = Time.time; showing = true;
                title.text = current.title; sub.text = current.sub;
                title.color = current.color; accentImg.color = current.color;
                stripe.gameObject.SetActive(true);
                if (!string.IsNullOrEmpty(current.sound)) GameAudio.Play2D(current.sound, 0.7f);
            }
            if (!showing) return;

            float t = Time.time - start, end = 0.3f + current.hold;
            if (t > end + 0.4f) { showing = false; stripe.gameObject.SetActive(false); return; }

            // stripe slides in (scale X) and out (slides right + fades)
            float inK = Ease(t / 0.22f);
            float outK = t > end ? Mathf.Clamp01((t - end) / 0.4f) : 0f;
            stripe.localScale = new Vector3(Mathf.Max(0.001f, inK), 1f, 1f);
            stripe.anchoredPosition = new Vector2(outK * outK * 900f, 150f);
            float alpha = 1f - outK;
            var sc = stripeImg.color; sc.a = 0.78f * alpha; stripeImg.color = sc;
            var ac = accentImg.color; ac.a = alpha; accentImg.color = ac;

            // title: punch in with an elastic bounce (counteract the stripe's X scale so the text keeps its shape)
            float tt = Mathf.Max(0f, t - 0.08f);
            float punch = tt <= 0f ? 0f : 1f + 0.9f * Mathf.Exp(-tt * 7f) * Mathf.Cos(tt * 18f);
            title.rectTransform.localScale = new Vector3(punch / Mathf.Max(0.001f, inK), punch, 1f);
            var tc = title.color; tc.a = alpha * (tt > 0f ? 1f : 0f); title.color = tc;
            float st = Mathf.Max(0f, t - 0.25f);
            sub.rectTransform.localScale = new Vector3(1f / Mathf.Max(0.001f, inK), 1f, 1f);
            var sc2 = Sand; sc2.a = alpha * Mathf.Clamp01(st / 0.2f); sub.color = sc2;
        }

        static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }
    }
}
