using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// Leaderboard window (2026-10-28), opened from the lobby's LEADERBOARD button. Own canvas (sorting 30, reference 1920x1080).
    /// Tabs BATTLE ROYALE / ZOMBIE SURVIVAL and THIS WEEK / ALL TIME, top 50 with gold / silver / bronze, your own row pinned at the
    /// bottom, time until the weekly reset (Monday 00:00 UTC) and the scoring rules. Data from <see cref="LeaderboardApi"/>.
    /// </summary>
    public class LeaderboardPanel : MonoBehaviour
    {
        const float RefW = 1920f, RefH = 1080f;
        const float PanelW = 1180f, PanelH = 920f, RowH = 50f;

        static LeaderboardPanel instance;
        public static bool IsOpen => instance != null && instance.root.gameObject.activeSelf;

        static readonly Color Ink = new Color(0.06f, 0.08f, 0.11f, 0.97f);
        static readonly Color Line = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color Sand = new Color(0.98f, 0.94f, 0.84f);
        static readonly Color Dim = new Color(0.62f, 0.66f, 0.7f);
        static readonly Color Accent = new Color32(0xCC, 0xFF, 0x00, 0xFF);
        static readonly Color Gold = new Color(1f, 0.82f, 0.25f), Silver = new Color(0.82f, 0.86f, 0.92f), Bronze = new Color(0.86f, 0.55f, 0.3f);

        Font titleFont, numFont;
        RectTransform root, list;
        Text status, info, colScore, colKills, colMatches;
        Button[] modeBtns = new Button[2], periodBtns = new Button[2];
        readonly List<RowView> rows = new List<RowView>();
        RowView meRow;
        string mode = "br", period = "week";
        int requestId;
        double weekEnd, fetchedServerTime;
        float fetchedAt;
        bool hasBoard;

        class RowView { public RectTransform rt; public Image bg; public Text rank, name, score, kills, matches; }

        public static void Open()
        {
            if (instance == null) instance = new GameObject("LeaderboardPanel").AddComponent<LeaderboardPanel>();
            instance.root.gameObject.SetActive(true);
            instance.Refresh();
        }

        public static void Close() { if (instance != null) instance.root.gameObject.SetActive(false); }

        // ------------------------------------------------------------------ build
        void Awake()
        {
            var builtIn = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleFont = Resources.Load<Font>("Fonts/Title") ?? builtIn;
            numFont = Resources.Load<Font>("Fonts/Numbers") ?? titleFont;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.matchWidthOrHeight = 1f;
            gameObject.AddComponent<GraphicRaycaster>();

            root = New("Root", transform); Stretch(root);
            var dim = root.gameObject.AddComponent<Image>(); dim.color = new Color(0f, 0f, 0f, 0.72f);   // blocks clicks to the lobby

            var panel = New("Panel", root);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f); panel.sizeDelta = new Vector2(PanelW, PanelH);
            var pbg = panel.gameObject.AddComponent<Image>(); pbg.color = Ink;
            var edge = panel.gameObject.AddComponent<Outline>(); edge.effectColor = new Color(0.8f, 1f, 0f, 0.55f); edge.effectDistance = new Vector2(3, -3);

            Label(Place(panel, 0, 22, PanelW, 70), "LEADERBOARD", titleFont, 58, Sand, TextAnchor.MiddleCenter);
            MakeButton(Place(panel, PanelW - 92, 22, 70, 70), "X", new Color(0.62f, 0.18f, 0.22f), Close, 34);

            modeBtns[0] = MakeButton(Place(panel, PanelW / 2 - 400, 108, 390, 62), "BATTLE ROYALE", Accent, () => SetMode("br"), 28);
            modeBtns[1] = MakeButton(Place(panel, PanelW / 2 + 10, 108, 390, 62), "ZOMBIE SURVIVAL", Accent, () => SetMode("zombies"), 28);
            periodBtns[0] = MakeButton(Place(panel, PanelW / 2 - 270, 184, 260, 50), "THIS WEEK", Accent, () => SetPeriod("week"), 24);
            periodBtns[1] = MakeButton(Place(panel, PanelW / 2 + 10, 184, 260, 50), "ALL TIME", Accent, () => SetPeriod("all"), 24);

            info = Label(Place(panel, 40, 244, PanelW - 80, 44), "", numFont, 21, Dim, TextAnchor.MiddleCenter);

            // column header
            var head = Place(panel, 40, 296, PanelW - 80, 40);
            Label(Col(head, 0, 110), "RANK", numFont, 20, Dim, TextAnchor.MiddleCenter);
            Label(Col(head, 110, 520), "PLAYER", numFont, 20, Dim, TextAnchor.MiddleLeft);
            colScore = Label(Col(head, 630, 180), "POINTS", numFont, 20, Dim, TextAnchor.MiddleRight);
            colKills = Label(Col(head, 810, 150), "KILLS", numFont, 20, Dim, TextAnchor.MiddleRight);
            colMatches = Label(Col(head, 960, 140), "MATCHES", numFont, 20, Dim, TextAnchor.MiddleRight);

            // scrolling list
            var view = Place(panel, 40, 340, PanelW - 80, 430);
            view.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.25f);
            view.gameObject.AddComponent<RectMask2D>();
            list = New("List", view);
            list.anchorMin = new Vector2(0, 1); list.anchorMax = new Vector2(1, 1); list.pivot = new Vector2(0.5f, 1); list.sizeDelta = new Vector2(0, 0);
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = list; scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 30f;
            for (int i = 0; i < 50; i++) rows.Add(MakeRow(list, i));

            status = Label(Place(panel, 40, 520, PanelW - 80, 60), "", numFont, 28, Sand, TextAnchor.MiddleCenter);

            // you
            Label(Place(panel, 40, 784, 300, 30), "YOU", numFont, 20, Accent, TextAnchor.MiddleLeft);
            meRow = MakeRow(Place(panel, 40, 816, PanelW - 80, RowH + 4), -1);
            meRow.bg.color = new Color(0.8f, 1f, 0f, 0.14f);
            Label(Place(panel, 40, 876, PanelW - 80, 32), "Only matches played with a connected wallet are ranked (one row per wallet). Prizes go to that wallet after a manual check of the top runs.",
                  numFont, 17, new Color(0.5f, 0.54f, 0.58f), TextAnchor.MiddleCenter);
            root.gameObject.SetActive(false);
        }

        RowView MakeRow(RectTransform parent, int index)
        {
            RectTransform rt;
            if (index >= 0)
            {
                rt = New("Row" + index, parent);
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
                rt.sizeDelta = new Vector2(0, RowH); rt.anchoredPosition = new Vector2(0, -index * RowH);
            }
            else rt = parent;
            var v = new RowView { rt = rt };
            v.bg = rt.gameObject.AddComponent<Image>(); v.bg.color = index % 2 == 0 ? Line : Color.clear; v.bg.raycastTarget = index >= 0;
            v.rank = Label(Col(rt, 0, 110), "", titleFont, 30, Sand, TextAnchor.MiddleCenter);
            v.name = Label(Col(rt, 110, 520), "", numFont, 28, Sand, TextAnchor.MiddleLeft);
            v.score = Label(Col(rt, 630, 180), "", numFont, 28, Accent, TextAnchor.MiddleRight);
            v.kills = Label(Col(rt, 810, 150), "", numFont, 26, Sand, TextAnchor.MiddleRight);
            v.matches = Label(Col(rt, 960, 140), "", numFont, 26, Dim, TextAnchor.MiddleRight);
            return v;
        }

        // ------------------------------------------------------------------ data
        void SetMode(string m) { if (mode == m) return; mode = m; Refresh(); }
        void SetPeriod(string p) { if (period == p) return; period = p; Refresh(); }

        void Refresh()
        {
            bool zombies = mode == "zombies";
            colScore.text = zombies ? "WAVE" : "POINTS";
            colKills.text = zombies ? "KILLS" : "KILLS";
            colMatches.text = zombies ? "RUNS" : "MATCHES";
            Highlight(modeBtns, zombies ? 1 : 0);
            Highlight(periodBtns, period == "all" ? 1 : 0);
            info.text = Rules();
            foreach (var r in rows) r.rt.gameObject.SetActive(false);
            Fill(meRow, null);
            status.text = "LOADING…";
            hasBoard = false;
            int id = ++requestId;
            LeaderboardApi.GetBoard(mode, period, b =>
            {
                if (this == null || id != requestId) return;   // closed or another tab was picked meanwhile
                if (b == null) { status.text = "LEADERBOARD OFFLINE\n<size=20>Check your connection and try again.</size>"; return; }
                hasBoard = true;
                weekEnd = b.weekEnd; fetchedServerTime = b.serverTime; fetchedAt = Time.unscaledTime;
                int n = b.rows != null ? b.rows.Length : 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i].rt.gameObject.SetActive(i < n);
                    if (i < n) Fill(rows[i], b.rows[i]);
                }
                list.sizeDelta = new Vector2(0, n * RowH);
                list.anchoredPosition = Vector2.zero;
                status.text = n == 0 ? "NO SCORES YET\n<size=20>Play a match to be the first!</size>" : "";
                Fill(meRow, b.me != null && b.me.rank > 0 ? b.me : null);
                info.text = Rules();
            });
        }

        void Fill(RowView v, LeaderboardApi.Row r)
        {
            if (r == null)
            {
                v.rank.text = "-"; v.name.text = PlayerPrefs.GetString("playerName", "GUEST CAT");
                bool wallet = WalletSession.Connected;
                if (v == meRow && wallet) v.name.text += "  <size=18><color=#7F8890>" + WalletSession.Short + "</color></size>";
                v.score.text = ""; v.kills.text = "";
                v.matches.text = v != meRow ? "" : wallet ? "not ranked yet" : "<color=#CCFF00>connect wallet</color>";
                v.rank.color = Sand;
                return;
            }
            bool zombies = mode == "zombies";
            v.rank.text = r.rank.ToString();
            v.rank.color = r.rank == 1 ? Gold : r.rank == 2 ? Silver : r.rank == 3 ? Bronze : Sand;
            // name + short wallet (one row per wallet, 2026-11-08)
            v.name.text = r.name + (string.IsNullOrEmpty(r.wallet) ? "" : "  <size=18><color=#7F8890>" + r.wallet + "</color></size>");
            v.name.color = r.you ? Accent : Sand;
            v.score.text = zombies ? (r.score / 1000).ToString() : r.score.ToString("N0");
            v.kills.text = zombies ? (r.score % 1000).ToString() : r.kills.ToString();
            v.matches.text = r.matches.ToString();
        }

        string Rules()
        {
            string how = mode == "zombies" ? "Best run counts: highest wave, then most kills."
                       : period == "week" ? "Your best 10 matches of the week count. Win 100 · 2nd 70 · 3rd 55 ... + 10 per kill."
                                          : "All your match points added up. Win 100 · 2nd 70 · 3rd 55 ... + 10 per kill.";
            if (period != "week" || !hasBoard) return how;
            double left = (weekEnd - (fetchedServerTime + (Time.unscaledTime - fetchedAt) * 1000.0)) / 1000.0;
            if (left < 0) left = 0;
            int d = (int)(left / 86400), h = (int)(left % 86400 / 3600), m = (int)(left % 3600 / 60);
            return how + "   <color=#CCFF00>Resets in " + (d > 0 ? d + "d " : "") + h + "h " + m + "m</color>";
        }

        void Update()
        {
            if (!root.gameObject.activeSelf) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }
            if (hasBoard && period == "week" && Time.frameCount % 30 == 0) info.text = Rules();
        }

        // ------------------------------------------------------------------ ui helpers (1180x920 panel px, top-left)
        static RectTransform New(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }

        static RectTransform Place(RectTransform parent, float x, float y, float w, float h)
        {
            var rt = New("R", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static RectTransform Col(RectTransform row, float x, float w)
        {
            var rt = New("C", row);
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0); rt.sizeDelta = new Vector2(w - 12f, 0);
            return rt;
        }

        static Text Label(RectTransform rt, string text, Font font, int size, Color color, TextAnchor anchor)
        {
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.text = text; t.fontSize = size; t.color = color; t.alignment = anchor; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0, 0, 0, 0.7f); o.effectDistance = new Vector2(1.5f, -1.5f);
            return t;
        }

        Button MakeButton(RectTransform rt, string text, Color color, System.Action onClick, int size)
        {
            var img = rt.gameObject.AddComponent<Image>(); img.color = color;
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(() => { GameAudio.Play2D("ui_click", 0.7f); onClick(); });
            var lrt = New("Label", rt); Stretch(lrt);
            var t = lrt.gameObject.AddComponent<Text>();
            t.font = titleFont; t.text = text; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            t.color = color == Accent ? Ink : Sand;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            return b;
        }

        static void Highlight(Button[] group, int active)
        {
            for (int i = 0; i < group.Length; i++)
            {
                var img = (Image)group[i].targetGraphic;
                img.color = i == active ? Accent : new Color(0.22f, 0.26f, 0.3f);
                var label = group[i].GetComponentInChildren<Text>();
                label.color = i == active ? Ink : Sand;
            }
        }
    }
}
