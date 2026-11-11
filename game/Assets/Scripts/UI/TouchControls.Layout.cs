using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace CatBattleground
{
    /// <summary>
    /// The player's own touch layout (2026-10-31, user): Settings > CUSTOMIZE CONTROLS opens an edit mode where every button and the
    /// joystick can be dragged to a new place, made bigger / smaller, and all controls made more or less see-through.
    /// Saved as JSON in PlayerPrefs "touchLayout": per button the anchor (nearest third of the screen, so it keeps its corner on wider
    /// or narrower phones) + offset + diameter, the joystick's resting place + size, one opacity. RESET goes back to the defaults
    /// written in TouchControls.Build, CANCEL to what was saved.
    /// </summary>
    public partial class TouchControls
    {
        [Serializable] class Layout { public string key; public float ax, ay, x, y, d; }
        [Serializable] class Saved
        {
            public List<Layout> buttons = new List<Layout>();
            public float stickX = 300f, stickY = 300f, stickScale = 1f, alpha = 1f;
        }

        const string LayoutPref = "touchLayout";
        const float MinSize = 60f, MaxSize = 320f, MinStick = 0.6f, MaxStick = 1.6f, MinAlpha = 0.25f;

        /// <summary>Customise mode is open (no game input meanwhile).</summary>
        public static bool Editing { get; private set; }
        /// <summary>Opens customise mode (called by the settings panel). Only on touch devices.</summary>
        public static void OpenEditor() { if (instance != null) instance.StartEditing(); }

        float stickScale = 1f, alpha = 1f;
        Vector2 stickRest = new Vector2(300f, 300f);
        CanvasGroup group;
        RectTransform editShade, editPanel;
        Text editInfo;
        readonly List<KeyValuePair<RectTransform, Action>> editButtons = new List<KeyValuePair<RectTransform, Action>>();
        Btn selected;
        bool stickSelected;
        int dragId = -1;
        Vector2 dragOffset;
        Saved backup;

        // ------------------------------------------------------------------ apply / load / save
        void Place(Btn b, Layout l)
        {
            b.rt.anchorMin = b.rt.anchorMax = new Vector2(l.ax, l.ay);
            b.rt.pivot = new Vector2(0.5f, 0.5f);
            b.rt.sizeDelta = new Vector2(l.d, l.d);
            b.rt.anchoredPosition = new Vector2(l.x, l.y);
            b.radius = l.d * 0.5f;
            b.label.fontSize = Mathf.RoundToInt(Mathf.Clamp(l.d * 0.22f, 20f, 40f));
        }

        static Layout Current(Btn b) => new Layout
        {
            key = b.key, ax = b.rt.anchorMin.x, ay = b.rt.anchorMin.y,
            x = b.rt.anchoredPosition.x, y = b.rt.anchoredPosition.y, d = b.rt.sizeDelta.x,
        };

        Saved Snapshot()
        {
            var s = new Saved { stickX = stickRest.x, stickY = stickRest.y, stickScale = stickScale, alpha = alpha };
            foreach (var b in buttons) s.buttons.Add(Current(b));
            return s;
        }

        void Apply(Saved s)
        {
            foreach (var b in buttons)
            {
                var l = s.buttons.Find(x => x.key == b.key);
                Place(b, l != null && l.d > 0f ? l : b.home);
            }
            stickRest = new Vector2(s.stickX, s.stickY);
            stickScale = Mathf.Clamp(s.stickScale, MinStick, MaxStick);
            alpha = Mathf.Clamp(s.alpha, MinAlpha, 1f);
            if (group != null) group.alpha = alpha;
            stickBase.localScale = Vector3.one * stickScale;
            StickRest();
        }

        void LoadLayout()
        {
            group = gameObject.GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
            group.interactable = false; group.blocksRaycasts = false;
            Saved s = null;
            try { s = JsonUtility.FromJson<Saved>(PlayerPrefs.GetString(LayoutPref, "")); } catch { /* broken or empty: defaults */ }
            Apply(s ?? new Saved());
        }

        // ------------------------------------------------------------------ edit mode
        void StartEditing()
        {
            if (Editing) return;
            backup = Snapshot();
            Editing = true;
            fingers.Clear();
            aimToggled = false;
            OnDisable();   // let go of every game input
            if (editPanel == null) BuildEditor();
            editShade.gameObject.SetActive(true);
            editPanel.gameObject.SetActive(true);
            selected = fire; stickSelected = false; dragId = -1;
        }

        void StopEditing()
        {
            Editing = false;
            dragId = -1; selected = null; stickSelected = false;
            editShade.gameObject.SetActive(false);
            editPanel.gameObject.SetActive(false);
            foreach (var b in buttons) Edge(b).color = ButtonEdge;
            stickBase.GetComponent<Image>().color = new Color(1, 1, 1, 0.45f);
            StickRest();
        }

        static Image Edge(Btn b) => b.rt.GetChild(0).GetComponent<Image>();

        void BuildEditor()
        {
            if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();   // the shade blocks the menus behind

            editShade = NewRect("EditShade", root); Stretch(editShade);
            editShade.SetAsFirstSibling();   // behind the buttons
            var shadeGroup = editShade.gameObject.AddComponent<CanvasGroup>(); shadeGroup.ignoreParentGroups = true;
            var shade = editShade.gameObject.AddComponent<Image>(); shade.color = new Color(0.02f, 0.03f, 0.06f, 0.6f); shade.raycastTarget = true;

            editPanel = NewRect("EditPanel", root);
            editPanel.anchorMin = editPanel.anchorMax = new Vector2(0.5f, 1f); editPanel.pivot = new Vector2(0.5f, 1f);
            editPanel.anchoredPosition = new Vector2(0f, -16f); editPanel.sizeDelta = new Vector2(1240f, 176f);
            var panelGroup = editPanel.gameObject.AddComponent<CanvasGroup>(); panelGroup.ignoreParentGroups = true;
            var bg = editPanel.gameObject.AddComponent<Image>(); bg.color = new Color(0.05f, 0.07f, 0.1f, 0.94f); bg.raycastTarget = true;
            var ol = editPanel.gameObject.AddComponent<Outline>(); ol.effectColor = Accent; ol.effectDistance = new Vector2(3, -3);

            var title = Text(editPanel, "CUSTOMIZE CONTROLS  ·  drag a button to move it", 30, Accent);
            Box(title.rectTransform, 0f, -12f, 1200f, 40f);
            editInfo = Text(editPanel, "", 24, Color.white);
            Box(editInfo.rectTransform, 0f, -52f, 1200f, 34f);

            string[] names = { "SIZE -", "SIZE +", "CLEARER", "SOLID", "RESET", "CANCEL", "SAVE" };
            Action[] acts = { () => Resize(0.9f), () => Resize(1.1f), () => Fade(-0.1f), () => Fade(0.1f), ResetLayout, CancelEdit, SaveEdit };
            float w = 160f, gap = 12f, x0 = -(names.Length * w + (names.Length - 1) * gap) * 0.5f + w * 0.5f;
            for (int i = 0; i < names.Length; i++)
            {
                var rt = NewRect(names[i], editPanel);
                Box(rt, x0 + i * (w + gap), -96f, w, 64f);
                var img = rt.gameObject.AddComponent<Image>(); img.raycastTarget = false;
                img.color = names[i] == "SAVE" ? Accent : names[i] == "CANCEL" ? new Color(0.6f, 0.18f, 0.2f) : new Color(0.16f, 0.2f, 0.27f);
                var t = Text(rt, names[i], 26, names[i] == "SAVE" ? new Color(0.06f, 0.08f, 0.02f) : Color.white);
                Stretch(t.rectTransform);
                editButtons.Add(new KeyValuePair<RectTransform, Action>(rt, acts[i]));
            }
            editShade.gameObject.SetActive(false);
            editPanel.gameObject.SetActive(false);
        }

        Text Text(RectTransform parent, string text, int size, Color color)
        {
            var rt = NewRect("Text", parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.text = text; t.fontSize = size; t.color = color; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        static void Box(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, h);
        }

        void UpdateEditor()
        {
            // everything shows, the selected control is outlined
            foreach (var b in buttons)
            {
                if (!b.rt.gameObject.activeSelf) b.rt.gameObject.SetActive(true);
                Edge(b).color = b == selected && !stickSelected ? Accent : ButtonEdge;
            }
            if (!stickBase.gameObject.activeSelf) stickBase.gameObject.SetActive(true);
            stickBase.GetComponent<Image>().color = stickSelected ? Accent : new Color(1, 1, 1, 0.45f);
            dropJump.label.text = "JUMP";
            editInfo.text = (stickSelected ? "JOYSTICK  ·  size " + Mathf.RoundToInt(stickScale * 100f) + "%"
                : selected != null ? Pretty(selected) + "  ·  size " + Mathf.RoundToInt(selected.rt.sizeDelta.x / selected.home.d * 100f) + "%" : "tap a button")
                + "      opacity " + Mathf.RoundToInt(alpha * 100f) + "%";

            float scale = canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            foreach (var t in ETouch.activeTouches)
            {
                var pos = t.screenPosition;
                if (t.phase == TouchPhase.Began)
                {
                    if (RectTransformUtility.RectangleContainsScreenPoint(editPanel, pos, null))
                    {
                        foreach (var kv in editButtons)
                            if (RectTransformUtility.RectangleContainsScreenPoint(kv.Key, pos, null)) { kv.Value(); GameAudio.Play2D("ui_click", 0.6f); break; }
                        continue;
                    }
                    Btn hit = null;
                    for (int i = buttons.Count - 1; i >= 0 && hit == null; i--)
                    {
                        var b = buttons[i]; float r = b.radius * scale * 1.1f;
                        if ((pos - (Vector2)b.rt.position).sqrMagnitude <= r * r) hit = b;
                    }
                    float sr = 130f * scale * stickScale;
                    if (hit != null) { selected = hit; stickSelected = false; dragId = t.touchId; dragOffset = (Vector2)hit.rt.position - pos; }
                    else if ((pos - (Vector2)stickBase.position).sqrMagnitude <= sr * sr)
                    { stickSelected = true; dragId = t.touchId; dragOffset = (Vector2)stickBase.position - pos; }
                }
                else if (t.touchId == dragId)
                {
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) { dragId = -1; continue; }
                    MoveTo((pos + dragOffset) / scale);
                }
            }
        }

        static string Pretty(Btn b) => b.key == "fireLeft" ? "LEFT FIRE" : b.key == "dropJump" ? "PLANE JUMP / CHUTE" : b.key == "cancelBomb" ? "CANCEL BOMB"
            : b.key == "pause" ? "PAUSE" : b.label.text.ToUpperInvariant();

        /// <summary>p = canvas units from the bottom-left corner.</summary>
        void MoveTo(Vector2 p)
        {
            float cw = root.rect.width, ch = root.rect.height;
            if (stickSelected)
            {
                float half = 130f * stickScale;
                stickRest = new Vector2(Mathf.Clamp(p.x, half, cw - half), Mathf.Clamp(p.y, half, ch - half));
                StickRest();
                return;
            }
            if (selected == null) return;
            float r = selected.radius;
            p.x = Mathf.Clamp(p.x, r, cw - r); p.y = Mathf.Clamp(p.y, r, ch - r);
            float ax = p.x < cw / 3f ? 0f : p.x > cw * 2f / 3f ? 1f : 0.5f;   // keep the nearest corner / edge on other screen shapes
            float ay = p.y < ch / 3f ? 0f : p.y > ch * 2f / 3f ? 1f : 0.5f;
            Place(selected, new Layout { key = selected.key, ax = ax, ay = ay, x = p.x - ax * cw, y = p.y - ay * ch, d = selected.rt.sizeDelta.x });
        }

        void Resize(float f)
        {
            if (stickSelected)
            {
                stickScale = Mathf.Clamp(stickScale * f, MinStick, MaxStick);
                stickBase.localScale = Vector3.one * stickScale;
                return;
            }
            if (selected == null) return;
            var l = Current(selected);
            l.d = Mathf.Clamp(l.d * f, MinSize, MaxSize);
            Place(selected, l);
        }

        void Fade(float delta)
        {
            alpha = Mathf.Clamp(alpha + delta, MinAlpha, 1f);
            group.alpha = alpha;
        }

        void ResetLayout() => Apply(new Saved());

        void CancelEdit() { Apply(backup); StopEditing(); }

        void SaveEdit()
        {
            PlayerPrefs.SetString(LayoutPref, JsonUtility.ToJson(Snapshot()));
            PlayerPrefs.Save();
            StopEditing();
        }
    }
}
