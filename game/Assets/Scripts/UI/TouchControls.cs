using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UI;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace CatBattleground
{
    /// <summary>
    /// On-screen controls for phone browsers in the PUBG Mobile style (2026-10-28), only when <see cref="GameInput.TouchMode"/>.
    /// Own overlay canvas (sorting 25, reference 1920x1080 match height, buttons anchored to their nearest corner). Touches are read
    /// directly (EnhancedTouch, several fingers at once); each finger gets one job when it lands and keeps it until it lifts:
    ///   left 45% of the screen    floating joystick (appears under the thumb; pulled far out in any direction = sprint)
    ///   right side                drag = look; a quick tap = "tap" (spectating: next cat, closes the big map)
    ///   FIRE (right, big)         hold = fire, dragging it also turns the camera; a second FIRE on the left
    ///   AIM                       toggles aim mode          JUMP   jump / leave the plane / open the chute
    ///   CROUCH, RELOAD            one press                 BOMB   hold = aim the arc (drag to look), let go = throw,
    ///                                                              the X that appears meanwhile cancels
    ///   pause button, tap on the round minimap = big map.
    /// Everything is written into GameInput's Touch* fields; the game scripts read GameInput. Also shows a "rotate your phone" screen
    /// in portrait and asks the browser for full screen on the first touch.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public partial class TouchControls : MonoBehaviour
    {
        const float RefW = 1920f, RefH = 1080f;
        const float StickRadius = 110f;    // ref px the knob may travel
        const float SprintPull = 1.6f;     // thumb this many radii from the centre = sprint (inside = walk)
        const float TapTime = 0.25f, TapMove = 30f;

        enum Role { None, Stick, Look, Fire, Aim, Jump, Crouch, Reload, Bomb, CancelBomb, Pause, Map }

        class Btn
        {
            public string key;     // layout id (PlayerPrefs touchLayout, TouchControls.Layout.cs)
            public Layout home;    // default place / size
            public Role role;
            public RectTransform rt;
            public Image img;
            public Text label;
            public float radius;   // ref px
            public bool visible;
        }

        class Finger
        {
            public Role role;
            public Vector2 start;      // screen px
            public float startTime;
            public bool moved;
        }

        static TouchControls instance;
        Canvas canvas;
        RectTransform root;
        GameObject rotateScreen;
        readonly List<Btn> buttons = new List<Btn>();
        readonly Dictionary<int, Finger> fingers = new Dictionary<int, Finger>();
        Btn fire, fireLeft, aim, jump, dropJump, crouch, reload, bomb, cancelBomb, pause;
        RectTransform stickBase, stickKnob;
        Image stickKnobImg;
        Font font;
        bool aimToggled, askedFullscreen;

        static readonly Color ButtonFill = new Color(0.04f, 0.06f, 0.08f, 0.38f);
        static readonly Color ButtonEdge = new Color(1f, 1f, 1f, 0.55f);
        static readonly Color Accent = new Color32(0xCC, 0xFF, 0x00, 0xFF);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!GameInput.TouchMode || Application.isBatchMode || instance != null) return;
            instance = new GameObject("TouchControls").AddComponent<TouchControls>();
            DontDestroyOnLoad(instance.gameObject);
        }

        // ------------------------------------------------------------------ build
        void Awake()
        {
            EnhancedTouchSupport.Enable();
            // ?touch=1 on a PC: the mouse acts as one finger so the touch controls can be tried without a phone
            if (UnityEngine.InputSystem.Mouse.current != null && !Application.isMobilePlatform) TouchSimulation.Enable();
            font = Resources.Load<Font>("Fonts/Title") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 25;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.matchWidthOrHeight = 1f;
            root = (RectTransform)transform;   // no GraphicRaycaster: these images never block the other UIs

            var circle = CircleSprite(128, 0.06f);
            var ring = CircleSprite(128, 0.0f, ringOnly: true);

            // joystick (moves to where the thumb lands)
            stickBase = NewRect("StickBase", root);
            stickBase.anchorMin = stickBase.anchorMax = Vector2.zero; stickBase.sizeDelta = new Vector2(260, 260);
            var baseImg = stickBase.gameObject.AddComponent<Image>(); baseImg.sprite = ring; baseImg.color = new Color(1, 1, 1, 0.45f); baseImg.raycastTarget = false;
            var baseFill = NewRect("Fill", stickBase); Stretch(baseFill);
            var bf = baseFill.gameObject.AddComponent<Image>(); bf.sprite = circle; bf.color = new Color(0, 0, 0, 0.22f); bf.raycastTarget = false;
            stickKnob = NewRect("Knob", stickBase); stickKnob.sizeDelta = new Vector2(120, 120);
            stickKnobImg = stickKnob.gameObject.AddComponent<Image>(); stickKnobImg.sprite = circle; stickKnobImg.color = new Color(1, 1, 1, 0.7f); stickKnobImg.raycastTarget = false;
            StickRest();

            // buttons: centre in 1920x1080 top-left px, diameter
            fire = Add("fire", Role.Fire, "FIRE", 1660, 640, 190, circle, ring);
            fireLeft = Add("fireLeft", Role.Fire, "FIRE", 230, 430, 130, circle, ring);
            aim = Add("aim", Role.Aim, "AIM", 1470, 520, 120, circle, ring);
            jump = Add("jump", Role.Jump, "JUMP", 1835, 520, 115, circle, ring);
            dropJump = Add("dropJump", Role.Jump, "JUMP", 960, 880, 170, circle, ring);   // big centre button while leaving the plane / opening the chute
            crouch = Add("crouch", Role.Crouch, "CROUCH", 1835, 690, 115, circle, ring);
            reload = Add("reload", Role.Reload, "RELOAD", 1480, 700, 105, circle, ring);
            bomb = Add("bomb", Role.Bomb, "BOMB", 1660, 420, 105, circle, ring);
            cancelBomb = Add("cancelBomb", Role.CancelBomb, "X", 1460, 330, 100, circle, ring);
            cancelBomb.img.color = new Color(0.75f, 0.1f, 0.1f, 0.6f);
            pause = Add("pause", Role.Pause, "II", 1590, 64, 76, circle, ring);

            BuildRotateScreen();
            LoadLayout();          // the player's own places / sizes / opacity (TouchControls.Layout.cs)
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }

        Btn Add(string key, Role role, string text, float cx, float cy, float d, Sprite circle, Sprite ring)
        {
            var rt = NewRect(text, root);
            float ax = cx < RefW / 3f ? 0f : cx > RefW * 2f / 3f ? 1f : 0.5f;
            float ay = cy < RefH / 3f ? 1f : cy > RefH * 2f / 3f ? 0f : 0.5f;
            var home = new Layout { key = key, ax = ax, ay = ay, x = cx - ax * RefW, y = (RefH - cy) - ay * RefH, d = d };
            var img = rt.gameObject.AddComponent<Image>(); img.sprite = circle; img.color = ButtonFill; img.raycastTarget = false;
            var edge = NewRect("Edge", rt); Stretch(edge);
            var e = edge.gameObject.AddComponent<Image>(); e.sprite = ring; e.color = ButtonEdge; e.raycastTarget = false;
            var lrt = NewRect("Label", rt); Stretch(lrt);
            var t = lrt.gameObject.AddComponent<Text>();
            t.font = font; t.text = text; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            t.fontSize = Mathf.RoundToInt(Mathf.Clamp(d * 0.22f, 20f, 40f)); t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var o = lrt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0, 0, 0, 0.8f); o.effectDistance = new Vector2(2, -2);
            var b = new Btn { key = key, home = home, role = role, rt = rt, img = img, label = t, radius = d * 0.5f, visible = true };
            buttons.Add(b);
            Place(b, home);
            return b;
        }

        void BuildRotateScreen()
        {
            var go = new GameObject("RotatePhone", typeof(RectTransform));
            var c = go.AddComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 100;
            go.AddComponent<CanvasScaler>();
            go.transform.SetParent(transform, false);
            var bg = NewRect("Bg", go.transform); Stretch(bg);
            var img = bg.gameObject.AddComponent<Image>(); img.color = new Color(0.03f, 0.04f, 0.06f, 0.97f);
            var lrt = NewRect("Text", go.transform); Stretch(lrt);
            var t = lrt.gameObject.AddComponent<Text>();
            t.font = font; t.alignment = TextAnchor.MiddleCenter; t.color = Accent; t.raycastTarget = false;
            t.resizeTextForBestFit = true; t.resizeTextMinSize = 14; t.resizeTextMaxSize = 60;
            t.text = "ROTATE YOUR PHONE\n<size=70%><color=#FFFFFF>Cat Battleground plays in landscape</color></size>";
            go.AddComponent<CanvasGroup>().ignoreParentGroups = true;   // not faded by the player's button opacity
            rotateScreen = go;
            rotateScreen.SetActive(false);
        }

        /// <summary>A soft-edged disc (or ring) drawn in code.</summary>
        static Sprite CircleSprite(int size, float soft, bool ringOnly = false)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size * 0.5f - 1f;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size * 0.5f, size * 0.5f));
                    float a = Mathf.Clamp01((r - d) / 1.5f);                              // anti-aliased outer edge
                    if (ringOnly) a *= Mathf.Clamp01((d - (r - 5f)) / 1.5f);             // 5 px ring
                    else if (soft > 0f) a *= Mathf.Lerp(1f - soft * 4f, 1f, Mathf.Clamp01(d / r));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            bool portrait = Screen.height > Screen.width;
            if (rotateScreen.activeSelf != portrait) rotateScreen.SetActive(portrait);
            if (Editing) { UpdateEditor(); return; }   // customise mode: no game input (TouchControls.Layout.cs)

            // what the local cat can do right now
            var nm = NetworkManager.Singleton;
            var me = nm != null && nm.IsClient && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
            var match = MatchManager.Instance;
            bool inGame = me != null && match != null && !LobbyUI.Active && !GameUI.BlocksGameInput && !portrait;
            var health = me != null ? me.GetComponent<Health>() : null;
            bool alive = inGame && health != null && !health.IsDead && match.State != MatchState.Ended;
            var drop = me != null ? me.GetComponent<PlayerDrop>() : null;
            var dropState = drop != null ? drop.State : DropState.Ground;
            var thrower = me != null ? me.GetComponent<BombThrower>() : null;
            bool ground = alive && dropState == DropState.Ground;

            fire.visible = fireLeft.visible = aim.visible = crouch.visible = reload.visible = ground;
            bomb.visible = ground && thrower != null;
            cancelBomb.visible = ground && thrower != null && thrower.IsHolding;
            jump.visible = ground;
            dropJump.visible = alive && ((dropState == DropState.OnShip && match.CanJump) || dropState == DropState.Freefall);
            dropJump.label.text = dropState == DropState.Freefall ? "CHUTE" : "JUMP";
            pause.visible = inGame;
            if (!ground) aimToggled = false;
            if (bomb.visible) bomb.label.text = thrower.Bombs > 0 ? "BOMB\n<size=70%>x" + thrower.Bombs + "</size>" : "BOMB\n<size=70%>x0</size>";
            aim.img.color = aimToggled ? new Color(0.8f, 1f, 0f, 0.45f) : ButtonFill;

            ReadTouches(inGame, alive && dropState != DropState.OnShip);

            foreach (var b in buttons) if (b.rt.gameObject.activeSelf != b.visible) b.rt.gameObject.SetActive(b.visible);
            GameInput.TouchAim = aimToggled;
        }

        void ReadTouches(bool inGame, bool canMove)
        {
            GameInput.TouchMove = Vector2.zero;
            GameInput.TouchLook = Vector2.zero;
            GameInput.TouchSprint = false;
            GameInput.TouchFire = false;
            bool bombHeld = false, stickShown = false;
            float scale = canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
            float lookScale = 1500f / Mathf.Max(1f, Screen.width) * PlayerPrefs.GetFloat("touchSensitivity", 1f);   // ~180 deg per screen width at the default mouse sensitivity

            var seen = new HashSet<int>();
            foreach (var t in ETouch.activeTouches)
            {
                int id = t.touchId;
                seen.Add(id);
                var pos = t.screenPosition;
                if (t.phase == TouchPhase.Began || !fingers.TryGetValue(id, out var f))
                {
                    f = new Finger { role = PickRole(pos, inGame, canMove), start = pos, startTime = Time.unscaledTime };
                    fingers[id] = f;
                    OnPress(f.role);
                    if (f.role == Role.Stick) PlaceStick(pos, scale);
                }

                bool ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                if ((pos - f.start).magnitude > TapMove * scale) f.moved = true;

                switch (f.role)
                {
                    case Role.Stick:
                    {
                        var off = (pos - (Vector2)stickBase.position) / scale;   // ref px from the base centre
                        float radius = StickRadius * stickScale;                  // the player may resize the joystick
                        var clamped = Vector2.ClampMagnitude(off, radius);
                        stickKnob.anchoredPosition = clamped / stickScale;
                        GameInput.TouchMove = clamped / radius;
                        bool sprint = off.magnitude > radius * SprintPull;   // any direction, pulled well past the ring
                        GameInput.TouchSprint = sprint;
                        stickKnobImg.color = sprint ? Accent : new Color(1, 1, 1, 0.7f);
                        stickShown = !ended;
                        break;
                    }
                    case Role.Look:
                        GameInput.TouchLook += t.delta * lookScale;
                        if (ended && !f.moved && Time.unscaledTime - f.startTime < TapTime) GameInput.TouchTapFrame = Time.frameCount;
                        break;
                    case Role.Fire:
                        GameInput.TouchFire |= !ended;
                        GameInput.TouchLook += t.delta * lookScale;   // fire and turn with the same thumb
                        break;
                    case Role.Bomb:
                        bombHeld = !ended;
                        GameInput.TouchLook += t.delta * lookScale;   // drag to aim the arc
                        break;
                }
                if (ended) fingers.Remove(id);
            }

            // fingers that vanished without an Ended phase (focus loss)
            if (fingers.Count > seen.Count)
            {
                var gone = new List<int>();
                foreach (var kv in fingers) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                foreach (var id in gone) fingers.Remove(id);
            }

            GameInput.TouchBomb = bombHeld;
            stickBase.gameObject.SetActive(inGame && canMove);
            if (!stickShown) StickRest();
        }

        Role PickRole(Vector2 pos, bool inGame, bool canMove)
        {
            if (!askedFullscreen && Application.platform == RuntimePlatform.WebGLPlayer)
            {
                askedFullscreen = true;
                if (!Screen.fullScreen) Screen.fullScreen = true;   // the browser allows this only inside a touch, which this is
            }
            if (!inGame) return Role.None;
            if (OverOtherButton(pos)) return Role.None;   // GameUI / spectate bar buttons keep working
            foreach (var b in buttons)
            {
                if (!b.visible || !b.rt.gameObject.activeSelf) continue;
                float r = b.radius * canvas.scaleFactor * 1.15f;   // a little bigger than drawn: thumbs are fat
                if ((pos - (Vector2)b.rt.position).sqrMagnitude <= r * r) return b.role;
            }
            if (GameHud.MinimapRect != null && GameHud.MinimapRect.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(GameHud.MinimapRect, pos, null)) return Role.Map;
            if (canMove && pos.x < Screen.width * 0.45f) return Role.Stick;
            return Role.Look;
        }

        void OnPress(Role role)
        {
            int frame = Time.frameCount;
            switch (role)
            {
                case Role.Aim: aimToggled = !aimToggled; break;
                case Role.Jump: GameInput.TouchJumpFrame = frame; break;
                case Role.Crouch: GameInput.TouchCrouchFrame = frame; break;
                case Role.Reload: GameInput.TouchReloadFrame = frame; break;
                case Role.Pause: GameInput.TouchPauseFrame = frame; break;
                case Role.Map: GameInput.TouchMapFrame = frame; break;
                case Role.CancelBomb: GameInput.TouchBombCancelFrame = frame; break;
                case Role.Bomb: GameInput.TouchBombDownFrame = frame; GameInput.TouchBomb = true; break;
            }
        }

        static readonly List<RaycastResult> hits = new List<RaycastResult>();
        static bool OverOtherButton(Vector2 pos)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            hits.Clear();
            es.RaycastAll(new PointerEventData(es) { position = pos }, hits);
            foreach (var h in hits)
                if (h.gameObject != null && h.gameObject.GetComponentInParent<Selectable>() != null) return true;
            return false;
        }

        void PlaceStick(Vector2 pos, float scale)
        {
            // keep the whole base on screen
            float half = 130f * scale * stickScale;
            pos.x = Mathf.Clamp(pos.x, half, Screen.width - half);
            pos.y = Mathf.Clamp(pos.y, half, Screen.height - half);
            stickBase.anchoredPosition = pos / scale;
            stickBase.pivot = new Vector2(0.5f, 0.5f);
            stickKnob.anchoredPosition = Vector2.zero;
        }

        void StickRest()
        {
            // resting spot (lower left) so the player sees where to put the thumb
            stickBase.pivot = new Vector2(0.5f, 0.5f);
            stickBase.anchoredPosition = stickRest;
            stickKnob.anchoredPosition = Vector2.zero;
            stickKnobImg.color = new Color(1, 1, 1, 0.7f);
        }

        void OnDisable()
        {
            GameInput.TouchMove = GameInput.TouchLook = Vector2.zero;
            GameInput.TouchFire = GameInput.TouchAim = GameInput.TouchBomb = GameInput.TouchSprint = false;
        }
    }
}
