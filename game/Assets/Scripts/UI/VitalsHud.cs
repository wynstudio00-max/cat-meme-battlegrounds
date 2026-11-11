using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// The HP / armor panel from the user's art ("CMBG FINAL UI/CMBG_HUD_HP_FINAL", README_Unity.txt), bottom left, replacing
    /// GameUI's placeholder vitals. All layers are 1120x320 sprites stacked on one rect (Resources/UI/HUD, copied by
    /// Tools/UI/prepare_ingame_assets.py), bottom to top: avatar frame (colour), avatar head, HP track, white damage ghost
    /// (lags behind, then drains), HP fill (green &gt; 50, yellow 20-50, red &lt; 20 - frame follows), armor track, armor fill;
    /// live text: HP number (Anton, italic), "HP" and "ARM xx" (Barlow Condensed ExtraBold Italic).
    /// The bars are horizontal fills mapped onto the painted bar span (the sprites are wider than the bars).
    /// Head: Resources/UI/HUD/02_avatar_head_&lt;characterId&gt;.png when it exists (same 1120x320 layout), else the white cat.
    /// </summary>
    public class VitalsHud : MonoBehaviour
    {
        public static bool Active { get; private set; }
        static VitalsHud instance;

        // painted bar spans in the 1120 px wide sprites (from the part bounds)
        const float W = 1120f, HpX0 = 264f, HpX1 = 1043f, ArmX0 = 264f, ArmX1 = 843f;

        RectTransform panel;
        Image frame, head, ghost, hpFill, armFill;
        Text hpNumber, hpLabel, armLabel;
        Sprite[] frames = new Sprite[3], fills = new Sprite[3];
        float shownHp = 1f, ghostHp = 1f, ghostHold, shake;
        int colorIdx = -1;
        string headFor;
        Health watched;

        static readonly Color[] LabelColors = { new Color(0.35f, 1f, 0.6f), new Color(1f, 0.85f, 0.25f), new Color(1f, 0.35f, 0.32f) };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            if (Load("04_hp_empty") == null) return;   // art not imported yet: GameUI keeps its own vitals
            instance = new GameObject("VitalsHud").AddComponent<VitalsHud>();
        }

        static Sprite Load(string name)
        {
            var s = Resources.Load<Sprite>("UI/HUD/" + name);
            if (s != null) return s;
            var t = Resources.Load<Texture2D>("UI/HUD/" + name);
            return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f)) : null;
        }

        void Awake()
        {
            instance = this;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 11;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            panel = new GameObject("Vitals", typeof(RectTransform)).GetComponent<RectTransform>();
            panel.SetParent(transform, false);
            panel.anchorMin = panel.anchorMax = new Vector2(0, 0); panel.pivot = new Vector2(0, 0);
            panel.anchoredPosition = new Vector2(18, 14); panel.sizeDelta = new Vector2(560, 160);   // the 1x size, 2x sprites

            string[] c = { "green", "yellow", "red" };
            for (int i = 0; i < 3; i++) { frames[i] = Load("01_avatar_frame_" + c[i]); fills[i] = Load("03_hp_fill_" + c[i]); }
            frame = Layer("Frame", frames[0]);
            head = Layer("Head", Load("02_avatar_head_white_cat"));
            Layer("HpEmpty", Load("04_hp_empty"));
            ghost = Layer("Ghost", Load("05_hp_damage_ghost_white"), true);
            hpFill = Layer("HpFill", fills[0], true);
            Layer("ArmEmpty", Load("07_arm_empty"));
            armFill = Layer("ArmFill", Load("06_arm_fill"), true);

            var anton = Resources.Load<Font>("Fonts/Anton") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var barlow = Resources.Load<Font>("Fonts/Barlow") ?? anton;
            // text places measured on HUD_HP073_ARM060 (2x px / 2)
            hpNumber = Label("HpNumber", anton, 48, new Rect(466, 8, 78, 52), TextAnchor.MiddleRight, FontStyle.Italic, Color.white);
            hpLabel = Label("HpLabel", barlow, 22, new Rect(438, 30, 34, 28), TextAnchor.MiddleRight, FontStyle.Normal, LabelColors[0]);
            armLabel = Label("ArmLabel", barlow, 18, new Rect(430, 102, 100, 22), TextAnchor.MiddleLeft, FontStyle.Normal, new Color(0.45f, 0.82f, 1f));
            hpLabel.text = "HP";
            panel.gameObject.SetActive(false);
        }

        void OnDestroy() { if (instance == this) { instance = null; Active = false; } if (watched != null) watched.Damaged -= OnDamaged; }

        Image Layer(string name, Sprite sprite, bool filled = false)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(panel, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite; img.raycastTarget = false;
            if (filled) { img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Horizontal; img.fillOrigin = 0; }
            return img;
        }

        Text Label(string name, Font font, int size, Rect r, TextAnchor anchor, FontStyle style, Color color)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(panel, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(r.x, -r.y); rt.sizeDelta = new Vector2(r.width, r.height);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.fontStyle = style; t.alignment = anchor; t.color = color; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.03f, 0.04f, 0.08f, 0.95f); o.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        static float Span(float x0, float x1, float v) => v <= 0.001f ? 0f : (x0 + Mathf.Clamp01(v) * (x1 - x0)) / W;

        void OnDamaged(float amount, GameObject attacker) { shake = 1f; ghostHold = 0.45f; }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            var me = nm != null && nm.IsClient && nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>() : null;
            var match = MatchManager.Instance;
            bool show = me != null && match != null && !me.Health.IsDead && !GameUI.SettingsOpen
                        && (match.State == MatchState.Waiting || match.State == MatchState.Playing);
            Active = me != null;   // GameUI hides its placeholder vitals while this exists for the local cat
            if (panel.gameObject.activeSelf != show) panel.gameObject.SetActive(show);
            if (!show) return;

            var h = me.Health;
            if (h != watched) { if (watched != null) watched.Damaged -= OnDamaged; watched = h; watched.Damaged += OnDamaged; ghostHp = shownHp = h.Max > 0 ? h.Current / h.Max : 0; }
            float hp = h.Max > 0f ? Mathf.Clamp01(h.Current / h.Max) : 0f;
            float arm = Mathf.Clamp01(h.Armor / Health.MaxArmor);

            // the fill follows quickly, the white ghost waits a moment and then drains
            shownHp = Mathf.MoveTowards(shownHp, hp, Time.deltaTime * 2.5f);
            if (hp > ghostHp) ghostHp = hp;
            if (ghostHold > 0f) ghostHold -= Time.deltaTime; else ghostHp = Mathf.MoveTowards(ghostHp, hp, Time.deltaTime * 0.6f);
            hpFill.fillAmount = Span(HpX0, HpX1, shownHp);
            ghost.fillAmount = Span(HpX0, HpX1, ghostHp);
            armFill.fillAmount = Span(ArmX0, ArmX1, arm);

            int hpValue = Mathf.CeilToInt(h.Current);
            int idx = hpValue > 50 ? 0 : hpValue >= 20 ? 1 : 2;
            if (idx != colorIdx)
            {
                colorIdx = idx;
                if (frames[idx] != null) frame.sprite = frames[idx];
                if (fills[idx] != null) hpFill.sprite = fills[idx];
                hpLabel.color = LabelColors[idx];
            }
            hpNumber.text = hpValue.ToString();
            armLabel.text = "ARM " + Mathf.CeilToInt(h.Armor);

            // per-character head (optional art), else the white cat
            var id = me.CharacterId;
            if (id != headFor)
            {
                headFor = id;
                var s = string.IsNullOrEmpty(id) ? null : Load("02_avatar_head_" + id.ToLowerInvariant());
                if (s != null) head.sprite = s;
            }

            // hit: the panel jolts and the number flashes red; low HP pulses
            shake = Mathf.MoveTowards(shake, 0f, Time.deltaTime * 4f);
            panel.anchoredPosition = new Vector2(18, 14) + (shake > 0f ? Random.insideUnitCircle * 7f * shake : Vector2.zero);
            float pulse = idx == 2 ? 0.5f + 0.5f * Mathf.Sin(Time.time * 8f) : 0f;
            hpNumber.color = Color.Lerp(Color.white, new Color(1f, 0.3f, 0.28f), Mathf.Max(shake, pulse * 0.8f));
            hpNumber.rectTransform.localScale = Vector3.one * (1f + 0.25f * shake);
        }
    }
}
