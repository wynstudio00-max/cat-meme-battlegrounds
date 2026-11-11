using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// The main lobby, built from the user's art ("CMBG FINAL UI", copied to Resources/UI/Lobby by Tools/UI/prepare_lobby_assets.py)
    /// exactly where Lobby_UI_Overlay_transparent.png places every part (Tools/UI/lobby_layout.json, 1920x1080, matched by
    /// Tools/UI/locate_lobby_parts.py). Parts keep their screen corner when the aspect ratio changes. Sample text in the art was
    /// painted out and is drawn live here (player name, coins, chat, pass progress). Centre: the selected cat in 3D
    /// (<see cref="LobbyCharacterPreview"/>), drag to turn it. Shown instead of GameUI's placeholder menu while not connected.
    /// Things that do not exist yet (club, inventory, leaderboard, shop, wallet, duo / squad) answer with a "coming soon" toast.
    /// The menu button opens play options: server address / join, dedicated server, pass card and (editor) test character.
    /// </summary>
    public class LobbyUI : MonoBehaviour
    {
        public static bool Active { get; private set; }
        static LobbyUI instance;

        const float RefW = 1920f, RefH = 1080f;
        Font titleFont, bodyFont;
        RectTransform root, content, optionsPanel;
        Text nameText, coinText, onlineText, passText, toastText, catName, catWeapon;
        Image profileImg, passFill, toastBg;
        readonly List<Text> chatLines = new List<Text>();
        readonly List<string> chat = new List<string>();
        InputField chatInput, ipField;
        RawImage previewImage;
        LobbyCharacterPreview preview;
        float toastTime = -10f;
        string shownCat;
        RectTransform cardList, testList; int builtCards = -1, builtTest = -1;
        Text walletText, walletNote;   // wallet: short address under the WALLET button, card status in the play settings
        Image walletButton; Sprite walletIdleSprite, walletConnectedSprite;   // CONNECT WALLET <-> CONNECTED art
        bool lobbyShown;   // lobby visible last frame (balance refresh on return)
        readonly List<(string id, Image bg, Text label)> cardButtons = new List<(string, Image, Text)>(), testButtons = new List<(string, Image, Text)>();
        GameObject statusRow; Text statusText;
        // profile: one-time rename, rank from owned cards, avatar from the owned character
        RectTransform namePanel, nameBlocker; InputField nameInput; Text nameNote, nameTitle; Image nameSave, nameClose; Image avatarImg;
        bool nameCreating, nameBusy;
        // live name check while typing (2026-11-02) + free suggestions when the name is taken
        readonly Image[] suggestBtns = new Image[3]; readonly Text[] suggestTexts = new Text[3];
        string nameLastTyped = ""; float nameCheckAt = -1f; int nameCheckSeq;
        bool confirmBusy; float confirmTryAt;
        /// <summary>Character whose portrait the profile shows (Resources/UI/Avatars/&lt;id&gt;.png). Later: the cat revealed by the
        /// player's gacha card on the website / backend. Until then the original cat.</summary>
        public static string ProfileCharacterId = "army";
        // game modes: the play settings open from SOLO / DOUBLE / TEAM
        readonly Image[] modeImgs = new Image[3]; int mode; Text modeTitle, modeNote;
        RectTransform champStamp; float champStampTime = -10f;
        static readonly string[] ModeNames = { "SOLO", "DOUBLE", "TEAM" };

        static readonly Color Ink = new Color(0.06f, 0.07f, 0.12f), Sand = new Color(0.96f, 0.93f, 0.86f), Navy = new Color(0.11f, 0.15f, 0.32f, 0.96f),
            Gold = new Color(1f, 0.78f, 0.15f), Green = new Color(0.35f, 0.86f, 0.32f), Pink = new Color(1f, 0.55f, 0.8f), Cyan = new Color(0.45f, 0.8f, 1f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            if (Resources.Load<Sprite>("UI/Lobby/09_play") == null && Resources.Load<Texture2D>("UI/Lobby/09_play") == null) return;   // art not imported
            new GameObject("LobbyUI").AddComponent<LobbyUI>();
        }

        void Awake()
        {
            instance = this; Active = true;
            var builtIn = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleFont = Resources.Load<Font>("Fonts/Title") ?? builtIn;     // Luckiest Guy
            bodyFont = Resources.Load<Font>("Fonts/Numbers") ?? builtIn;    // Lilita One

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5;                                         // GameUI (10) settings / pause stay on top
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.matchWidthOrHeight = 1f;                                  // height decides the scale, extra width goes to the gaps
            gameObject.AddComponent<GraphicRaycaster>();
            root = (RectTransform)transform;
            if (FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
            Build();
            chat.Add("<color=#ffd34d>System:</color> Welcome to Cat Battleground!");
            chat.Add("<color=#7fd0ff>Tip:</color> drag the cat to turn it around.");
            chat.Add("<color=#ff8cc8>Tip:</color> press PLAY to drop in with bots.");
        }

        void OnDestroy()
        {
            if (instance == this) { instance = null; Active = false; }
            if (preview != null) Destroy(preview.gameObject);
        }

        // ------------------------------------------------------------------ layout helpers
        Sprite SpriteOf(string name)
        {
            var s = Resources.Load<Sprite>("UI/Lobby/" + name);
            if (s != null) return s;
            var t = Resources.Load<Texture2D>("UI/Lobby/" + name);
            return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f) : null;
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Places a rect given in 1920x1080 top-left pixels, anchored to the nearest screen side / corner.</summary>
        static void PlaceRef(RectTransform rt, float x, float y, float w, float h)
        {
            float cx = x + w * 0.5f, cy = y + h * 0.5f;
            float ax = cx < RefW / 3f ? 0f : cx > RefW * 2f / 3f ? 1f : 0.5f;
            float ay = cy < RefH / 3f ? 1f : cy > RefH * 2f / 3f ? 0f : 0.5f;
            rt.anchorMin = rt.anchorMax = new Vector2(ax, ay);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(cx - ax * RefW, (RefH - cy) - ay * RefH);
        }

        Image Part(string sprite, float x, float y, float w, float h, Transform parent = null)
        {
            var rt = NewRect(sprite, parent != null ? parent : content);
            PlaceRef(rt, x, y, w, h);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = SpriteOf(sprite);
            img.preserveAspect = true;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>A child rect inside a part, in the part image's own pixels (scaled with the part).</summary>
        static RectTransform Inner(RectTransform part, float x0, float y0, float x1, float y1, float scale)
        {
            var rt = NewRect("Inner", part);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x0 * scale, -y0 * scale);
            rt.sizeDelta = new Vector2((x1 - x0) * scale, (y1 - y0) * scale);
            return rt;
        }

        Text Label(RectTransform rt, string text, Font font, int size, Color color, TextAnchor anchor, float outline = 2.5f)
        {
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.color = color; t.alignment = anchor; t.text = text; t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow; t.raycastTarget = false;
            if (outline > 0f)
            {
                var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.04f, 0.04f, 0.08f, 1f); o.effectDistance = new Vector2(outline, -outline);
                var s = rt.gameObject.AddComponent<Shadow>(); s.effectColor = new Color(0, 0, 0, 0.6f); s.effectDistance = new Vector2(outline * 0.8f, -outline * 1.6f);
            }
            return t;
        }

        void Clickable(Image img, System.Action onClick, bool juicy = true)
        {
            img.raycastTarget = true;
            var j = img.gameObject.AddComponent<LobbyButton>();
            j.onClick = () => { GameAudio.Play2D("ui_click", 0.7f); onClick?.Invoke(); };
            j.juicy = juicy;
        }

        // ------------------------------------------------------------------ build
        void Build()
        {
            // backdrop: a deep navy wash over whatever the scene camera shows, and a warm glow behind the cat
            var bg = NewRect("Backdrop", root); bg.anchorMin = Vector2.zero; bg.anchorMax = Vector2.one; bg.offsetMin = bg.offsetMax = Vector2.zero;
            var bgi = bg.gameObject.AddComponent<RawImage>(); bgi.texture = Gradient(); bgi.raycastTarget = false;
            content = NewRect("Content", root); content.anchorMin = Vector2.zero; content.anchorMax = Vector2.one; content.offsetMin = content.offsetMax = Vector2.zero;

            var glow = NewRect("Glow", content); PlaceRef(glow, 620, 80, 700, 760);
            var gi = glow.gameObject.AddComponent<RawImage>(); gi.texture = Radial(); gi.color = new Color(0.55f, 0.6f, 1f, 0.55f); gi.raycastTarget = false;

            // 3D cat (centre frame of the guide: 702,122 .. 1240,778)
            // 2026-11-07: 560 -> 900 wide (same height / cat size) so long guns (snipers) are not cut at the frame sides while it turns
            var pv = NewRect("CatPreview", content); PlaceRef(pv, 520, 100, 900, 700);
            previewImage = pv.gameObject.AddComponent<RawImage>();
            previewImage.color = new Color(1, 1, 1, 0);   // invisible until the preview camera has a texture (no white box)
            var drag = pv.gameObject.AddComponent<LobbyDrag>(); drag.onDrag = dx => { if (preview != null) preview.Spin(dx); };
            var cn = NewRect("CatName", content); PlaceRef(cn, 690, 700, 560, 60);
            catName = Label(cn, "", titleFont, 46, Sand, TextAnchor.MiddleCenter, 3f);
            var cw = NewRect("CatWeapon", content); PlaceRef(cw, 690, 752, 560, 34);
            catWeapon = Label(cw, "", bodyFont, 24, Gold, TextAnchor.MiddleCenter, 2f);

            // profile (top left) - image by tier, live name
            profileImg = Part("16_profile_normal", 0, 17, 580, 219);
            var nr = Inner(profileImg.rectTransform, 226, 30, 560, 88, 0.96f);
            nameText = Label(nr, "", titleFont, 44, Sand, TextAnchor.MiddleLeft, 3f);
            // portrait slot inside the frame (part pixels 60,46 .. 195,181), rounded so it sits in the frame like the art
            var av = Inner(profileImg.rectTransform, 60, 46, 195, 181, 0.96f);
            var mask = av.gameObject.AddComponent<Image>(); mask.sprite = Rounded(); mask.type = Image.Type.Sliced; mask.raycastTarget = false;
            av.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var avPic = NewRect("Portrait", av); Stretch(avPic);
            avatarImg = avPic.gameObject.AddComponent<Image>(); avatarImg.preserveAspect = false; avatarImg.raycastTarget = false;
            av.gameObject.SetActive(false);
            Clickable(profileImg, OpenNameEditor);

            // top bar
            var coin = Part("01_top_coin", 1072, 14, 278, 102);
            coinText = Label(Inner(coin.rectTransform, 96, 22, 190, 78, 1f), "", titleFont, 40, Sand, TextAnchor.MiddleCenter, 3f);
            // 2026-11-10: coin = the wallet's $CMBG; click opens the Pons launchpad to buy CMBG (new tab)
            Clickable(coin, () => { Toast("OPENING PONS · BUY $CMBG"); WalletBridge.OpenUrl(ChainConfig.BuyCmbgUrl); });
            Clickable(Part("02_top_settings", 1334, 10, 112, 112), () => GameUI.OpenSettings());
            Locked(Part("03_top_menu", 1430, 10, 112, 112), "MENU", Vector2.zero, 70);
            // wallet (2026-11-06): connects the browser wallet; its pass cards (read from the chain) fill PASS CARD in the play settings
            walletButton = Part("04_top_connect_wallet", 1534, 14, 358, 102);
            walletIdleSprite = walletButton.sprite;
            walletConnectedSprite = SpriteOf("04_top_wallet_connected");   // 2026-11-08: CONNECTED art (Tools/UI/make_wallet_connected.py)
            Clickable(walletButton, WalletClicked);
            var wt = NewRect("WalletAddr", content); PlaceRef(wt, 1534, 112, 358, 34);
            walletText = Label(wt, "", bodyFont, 22, Green, TextAnchor.MiddleCenter, 1.5f);

            // left menu
            Locked(Part("12_club", 44, 272, 403, 142), "CLUB", new Vector2(40, 0), 90);
            Locked(Part("13_inventory", 44, 398, 403, 142), "INVENTORY", new Vector2(40, 0), 90);
            Clickable(Part("14_leaderboard", 44, 524, 403, 142), () => LeaderboardPanel.Open());
            Locked(Part("15_shop", 44, 650, 403, 142), "SHOP", new Vector2(40, 0), 90);

            // CMBG pass (bottom left): live progress
            var pass = Part("11_cmbg_pass", 3, 786, 645, 288);
            var track = Inner(pass.rectTransform, 233, 226, 557, 248, 1f);
            var fillRt = NewRect("Fill", track); fillRt.anchorMin = new Vector2(0, 0); fillRt.anchorMax = new Vector2(0.75f, 1); fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
            passFill = fillRt.gameObject.AddComponent<Image>(); passFill.sprite = Rounded(); passFill.type = Image.Type.Sliced; passFill.color = Green;
            passText = Label(NewRect("Xp", track), "", titleFont, 22, Sand, TextAnchor.MiddleCenter, 2f);
            Stretch(passText.rectTransform);
            Locked(pass, "CMBG PASS", new Vector2(70, -10), 110);

            // chat (bottom centre): local only for now
            var chatImg = Part("10_chat_all_player", 632, 815, 628, 258);
            onlineText = Label(Inner(chatImg.rectTransform, 466, 32, 598, 62, 1f), "", bodyFont, 20, Green, TextAnchor.MiddleRight, 1.5f);
            for (int i = 0; i < 3; i++)
            {
                var line = Label(Inner(chatImg.rectTransform, 30, 74 + i * 36, 600, 108 + i * 36, 1f), "", bodyFont, 25, Sand, TextAnchor.MiddleLeft, 1.5f);
                line.horizontalOverflow = HorizontalWrapMode.Wrap;
                chatLines.Add(line);
            }
            chatInput = InputBox(Inner(chatImg.rectTransform, 42, 186, 476, 222, 1f), "Type a message...", 22);
            chatInput.onSubmit.AddListener(_ => SendChat());
            var send = Inner(chatImg.rectTransform, 488, 184, 594, 224, 1f);
            var sendHit = send.gameObject.AddComponent<Image>(); sendHit.color = new Color(1, 1, 1, 0);
            Clickable(sendHit, SendChat, false);

            // right side: champions, modes, play
            // CHAMPIONS is an event, not a play setting: on click a "coming soon" stamp pops up over it and goes away again
            var champ = Part("05_champions", 1296, 511, 602, 273);
            Clickable(champ, () => { champStampTime = Time.unscaledTime; champStamp.gameObject.SetActive(true); });
            var stamp = Inner(champ.rectTransform, 150, 110, 450, 180, 1f);
            champStamp = stamp;
            stamp.localRotation = Quaternion.Euler(0, 0, 8f);
            var sbg = stamp.gameObject.AddComponent<Image>(); sbg.sprite = Rounded(); sbg.type = Image.Type.Sliced; sbg.color = new Color(0.86f, 0.18f, 0.24f, 0.95f); sbg.raycastTarget = false;
            var sol = stamp.gameObject.AddComponent<Outline>(); sol.effectColor = new Color(0.04f, 0.04f, 0.08f, 1f); sol.effectDistance = new Vector2(3, -3);
            var st = Label(NewRect("T", stamp), "COMING SOON", titleFont, 36, Sand, TextAnchor.MiddleCenter, 2.5f); Stretch(st.rectTransform);
            stamp.gameObject.SetActive(false);
            // modes: pick one -> its play settings open
            modeImgs[0] = Part("06_mode_solo_active", 1307, 776, 213, 106);
            modeImgs[1] = Part("07_mode_double", 1493, 776, 213, 106);
            modeImgs[2] = Part("08_mode_team", 1679, 776, 213, 106);
            Clickable(modeImgs[0], () => SelectMode(0));
            // DOUBLE / TEAM are locked for now: a padlock pops over the button, no settings
            Locked(modeImgs[1], "DOUBLE", new Vector2(0, 8), 90);
            Locked(modeImgs[2], "TEAM", new Vector2(0, 8), 90);
            var play = Part("09_play", 1298, 840, 622, 228);
            Clickable(play, Play);
            var pulse = play.gameObject.AddComponent<LobbyPulse>(); pulse.amount = 0.025f;
            BuildGameModeBar();

            // toast + status
            var tr = NewRect("Toast", root); PlaceRef(tr, 660, 790, 600, 64);
            toastBg = tr.gameObject.AddComponent<Image>(); toastBg.sprite = Rounded(); toastBg.type = Image.Type.Sliced; toastBg.color = new Color(0.05f, 0.06f, 0.12f, 0.9f); toastBg.raycastTarget = false;
            toastText = Label(NewRect("T", tr), "", titleFont, 30, Gold, TextAnchor.MiddleCenter, 2f); Stretch(toastText.rectTransform);
            tr.gameObject.SetActive(false);
            var sr = NewRect("Status", root); PlaceRef(sr, 660, 20, 600, 40);
            statusRow = sr.gameObject;
            statusText = Label(sr, "", bodyFont, 24, Cyan, TextAnchor.MiddleCenter, 2f);

            BuildOptions();
        }

        // game mode (right column, above CHAMPIONS): BATTLE ROYALE | ZOMBIE SURVIVAL, code-drawn until there is art for it
        readonly Image[] gameModeBtns = new Image[2];
        readonly Text[] gameModeLabels = new Text[2];
        static readonly string[] GameModeNames = { "BATTLE ROYALE", "ZOMBIE SURVIVAL" };

        void BuildGameModeBar()
        {
            var head = NewRect("GameModeHead", content); PlaceRef(head, 1310, 392, 580, 34);
            Label(head, "GAME MODE", titleFont, 26, Cyan, TextAnchor.MiddleLeft, 2f);
            for (int i = 0; i < 2; i++)
            {
                int k = i;
                var rt = NewRect("GameMode" + i, content); PlaceRef(rt, 1310 + i * 296, 430, 284, 66);
                var img = rt.gameObject.AddComponent<Image>(); img.sprite = Rounded(); img.type = Image.Type.Sliced;
                var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.02f, 0.02f, 0.05f, 1f); o.effectDistance = new Vector2(3, -3);
                var t = Label(NewRect("T", rt), GameModeNames[i], titleFont, 26, Sand, TextAnchor.MiddleCenter, 0f); Stretch(t.rectTransform);
                gameModeBtns[i] = img; gameModeLabels[i] = t;
                Clickable(img, () => SetGameMode((MatchMode)k));
            }
            RefreshGameMode();
        }

        void SetGameMode(MatchMode m)
        {
            var nb = NetworkBootstrap.Instance; if (nb == null) return;
            nb.SelectedMode = m;
            RefreshGameMode();
            Toast(m == MatchMode.Zombies ? "ZOMBIE SURVIVAL · HOLD THE VILLAGE" : "BATTLE ROYALE · LAST CAT STANDING");
        }

        void RefreshGameMode()
        {
            var nb = NetworkBootstrap.Instance;
            int active = nb != null ? (int)nb.SelectedMode : 0;
            for (int i = 0; i < 2; i++)
            {
                bool on = i == active;
                gameModeBtns[i].color = on ? (i == 1 ? Green : Gold) : Navy;
                gameModeLabels[i].color = on ? Ink : Sand;
            }
        }

        void BuildOptions()
        {
            optionsPanel = NewRect("PlayOptions", root); PlaceRef(optionsPanel, 1250, 130, 640, 640);
            var bg = optionsPanel.gameObject.AddComponent<Image>(); bg.sprite = Rounded(); bg.type = Image.Type.Sliced; bg.color = Navy;
            var o = optionsPanel.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.02f, 0.02f, 0.05f, 1f); o.effectDistance = new Vector2(4, -4);
            float y = 24;
            modeTitle = Head("SOLO · PLAY SETTINGS", ref y, 40);
            modeNote = Head("", ref y, 22, Gold);
            // server address / join / dedicated server: testing tools, editor and development builds only.
            // A release client connects to the official server (WebGL: ?host=... in the page URL, see NetworkBootstrap).
            if (NetworkBootstrap.TestPickerAllowed)
            {
                Head("SERVER ADDRESS (dev only)", ref y, 26, Cyan);
                ipField = InputBox(Row(ref y, 52, 24, 380), NetworkBootstrap.Instance != null ? NetworkBootstrap.Instance.Address : "127.0.0.1", 26, true);
                ipField.onEndEdit.AddListener(v => { if (NetworkBootstrap.Instance != null) NetworkBootstrap.Instance.Address = v; });
                SmallButton(optionsPanel, "JOIN", new Rect(420, y - 58, 196, 52), Green, () =>
                {
                    var nb = NetworkBootstrap.Instance; if (nb == null) return; nb.Address = ipField.text; nb.UiJoin();
                });
                if (NetworkBootstrap.CanListenHere)
                {
                    SmallButton(optionsPanel, "DEDICATED SERVER", new Rect(24, y + 4, 300, 44), new Color(0.3f, 0.36f, 0.6f), () => NetworkBootstrap.Instance?.UiServer());
                    y += 58;
                }
            }
            Head("PASS CARD", ref y, 26, Cyan);
            walletNote = Head("", ref y, 20, Sand);
            walletNote.horizontalOverflow = HorizontalWrapMode.Wrap;
            cardList = Row(ref y, 96, 24, 592);
            // the TEST CHARACTER picker was removed (2026-11-06, user): cats come from the pass cards only
            SmallButton(optionsPanel, "CLOSE", new Rect(440, 20, 176, 44), new Color(0.6f, 0.2f, 0.25f), () => optionsPanel.gameObject.SetActive(false));
            optionsPanel.gameObject.SetActive(false);
        }

        Text Head(string text, ref float y, int size, Color? color = null)
        {
            var rt = NewRect(text, optionsPanel); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(24, -y); rt.sizeDelta = new Vector2(590, size + 10);
            var t = Label(rt, text, titleFont, size, color ?? Sand, TextAnchor.MiddleLeft, 2f);
            y += size + 16;
            return t;
        }

        RectTransform Row(ref float y, float h, float x, float w)
        {
            var rt = NewRect("Row", optionsPanel); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
            y += h + 12;
            return rt;
        }

        Image SmallButton(RectTransform parent, string label, Rect r, Color color, System.Action onClick, int size = 24)
        {
            var rt = NewRect(label, parent); rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(r.x, -r.y); rt.sizeDelta = new Vector2(r.width, r.height);
            var img = rt.gameObject.AddComponent<Image>(); img.sprite = Rounded(); img.type = Image.Type.Sliced; img.color = color;
            var t = Label(NewRect("L", rt), label, titleFont, size, Sand, TextAnchor.MiddleCenter, 2f); Stretch(t.rectTransform);
            Clickable(img, onClick);
            return img;
        }

        InputField InputBox(RectTransform rt, string text, int size, bool framed = false)
        {
            if (framed) { var bg = rt.gameObject.AddComponent<Image>(); bg.sprite = Rounded(); bg.type = Image.Type.Sliced; bg.color = new Color(0.03f, 0.04f, 0.1f, 1f); }
            else { var hit = rt.gameObject.AddComponent<Image>(); hit.color = new Color(0, 0, 0, 0); }
            var field = rt.gameObject.AddComponent<InputField>();
            var tr = NewRect("Text", rt); Stretch(tr); tr.offsetMin = new Vector2(14, 0); tr.offsetMax = new Vector2(-10, 0);
            var t = Label(tr, "", bodyFont, size, Sand, TextAnchor.MiddleLeft, 0f); t.supportRichText = false;
            var pr = NewRect("Placeholder", rt); Stretch(pr); pr.offsetMin = new Vector2(14, 0); pr.offsetMax = new Vector2(-10, 0);
            var p = Label(pr, framed ? "" : text, bodyFont, size, new Color(0.55f, 0.6f, 0.78f), TextAnchor.MiddleLeft, 0f);
            field.textComponent = t; field.placeholder = p;
            if (framed) field.text = text;
            return field;
        }

        static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }

        // ------------------------------------------------------------------ actions
        void Play()
        {
            var nb = NetworkBootstrap.Instance; if (nb == null) return;
            if (!NameCreated) { OpenNameCreator(); return; }
            if (mode != 0) { Toast(ModeNames[mode] + " · COMING SOON - PICK SOLO"); return; }
            if (NetworkBootstrap.CanHostHere) nb.UiHost();
            else { if (ipField != null) nb.Address = ipField.text; nb.UiJoin(); }
        }

        void WalletClicked()
        {
            var nb = NetworkBootstrap.Instance;
            if (nb != null && nb.MockCards) { Toast("MOCK CARDS ON (NetworkBootstrap)"); return; }
            if (!WalletSession.Connected) { Toast("CONNECTING WALLET..."); WalletSession.Connect(); return; }
            WalletSession.Refresh();
            SelectMode(mode);   // open the play settings: the cards are listed there
            Toast("WALLET " + WalletSession.Short.ToUpperInvariant() + " · CHECKING CARDS");
        }

        /// <summary>12345678 -> "12.3M", 850000 -> "850K", 999 -> "999" (fits the coin box).</summary>
        static string ShortAmount(double v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (v >= 1e9) return (v / 1e9).ToString(v >= 1e10 ? "0" : "0.#", inv) + "B";
            if (v >= 1e6) return (v / 1e6).ToString(v >= 1e8 ? "0" : "0.#", inv) + "M";
            if (v >= 1e3) return (v / 1e3).ToString(v >= 1e5 ? "0" : "0.#", inv) + "K";
            return System.Math.Floor(v).ToString("0", inv);
        }

        static string CardLabel(PassCard c) =>
            c.Playable ? "#" + c.tokenId + " " + CatName(c.characterId) : "#" + c.tokenId + " " + c.tier.ToString().ToUpperInvariant() + " · SEALED";

        static string CatName(string id)
        {
            var nb = NetworkBootstrap.Instance;
            if (nb != null) foreach (var e in nb.Characters) if (e != null && e.id == id) return (string.IsNullOrEmpty(e.displayName) ? id : e.displayName).ToUpperInvariant();
            return (id ?? "").ToUpperInvariant();
        }

        void CardClicked(NetworkBootstrap nb, PassCard card)
        {
            if (card.Playable) { nb.SelectedCard = card.tokenId; Toast("CARD #" + card.tokenId + " · " + CatName(card.characterId)); return; }
            Toast("CARD #" + card.tokenId + " IS SEALED · OPEN IT ON THE WEBSITE");
            WalletBridge.OpenUrl(ChainConfig.CardsPage);
        }

        /// <summary>A feature that is not open yet: click = padlock pops over it (button shakes) + "&lt;name&gt; · COMING SOON".</summary>
        void Locked(Image img, string feature, Vector2 lockOffset, float lockSize)
        {
            var lk = NewRect("Lock", img.rectTransform);
            lk.anchorMin = lk.anchorMax = new Vector2(0.5f, 0.5f); lk.sizeDelta = new Vector2(lockSize, lockSize); lk.anchoredPosition = lockOffset;
            var li = lk.gameObject.AddComponent<Image>(); li.sprite = LockSprite(); li.raycastTarget = false;
            var ls = lk.gameObject.AddComponent<Shadow>(); ls.effectColor = new Color(0, 0, 0, 0.6f); ls.effectDistance = new Vector2(3, -5);
            var anim = img.gameObject.AddComponent<LobbyLock>(); anim.icon = lk;
            lk.gameObject.SetActive(false);
            Clickable(img, () => { anim.Play(); Toast(feature + " · COMING SOON"); });
        }

        void SelectMode(int m)
        {
            mode = m;
            modeImgs[0].sprite = SpriteOf(m == 0 ? "06_mode_solo_active" : "06b_mode_solo_inactive");
            modeImgs[1].sprite = SpriteOf(m == 1 ? "07b_mode_double_active" : "07_mode_double");
            modeImgs[2].sprite = SpriteOf(m == 2 ? "08b_mode_team_active" : "08_mode_team");
            modeTitle.text = ModeNames[m] + " · PLAY SETTINGS";
            modeNote.text = m == 0 ? "10 CATS · LAST ONE STANDING" : ModeNames[m] + " IS COMING SOON · SOLO ONLY FOR NOW";
            optionsPanel.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------------ profile name
        // 2026-10-31 (user): every player must CREATE a name before playing (no wallet / X needed for the leaderboard).
        // Names are unique on the leaderboard server (LeaderboardApi.ClaimName). PlayerPrefs playerName / nameCreated; after that
        // ONE change (nameChanged) from the profile.
        // 2026-11-02: a name saved while offline (or from before the rule) is only "pending" (nameConfirmed 0); the lobby claims it
        // on the server in the background and, if another player owns it, asks for a new one (nameRejected 1).
        static bool NameCreated
        {
            get
            {
                if (PlayerPrefs.GetInt("nameRejected", 0) == 1) return false;
                if (PlayerPrefs.GetInt("nameCreated", 0) == 1) return true;
                // players from before this rule who already picked their own name keep it
                if (PlayerPrefs.HasKey("playerName") && PlayerName() != "GUEST CAT") { PlayerPrefs.SetInt("nameCreated", 1); return true; }
                return false;
            }
        }

        void OpenNameCreator()
        {
            if (namePanel == null) BuildNameEditor();
            nameCreating = true;
            nameTitle.text = "CREATE YOUR NAME";
            bool rejected = PlayerPrefs.GetInt("nameRejected", 0) == 1;
            nameInput.text = PlayerName() == "GUEST CAT" ? "" : PlayerName();
            nameInput.interactable = true;
            nameSave.gameObject.SetActive(true);
            nameClose.gameObject.SetActive(false);   // no way round it
            nameNote.text = rejected ? PlayerName() + " is already used by another player. Pick a new name."
                                     : "Your name on the leaderboard. 3-16 letters or numbers.";
            nameNote.color = rejected ? new Color(1f, 0.5f, 0.45f) : Gold;
            ShowSuggestions(rejected ? LeaderboardApi.LastSuggestions : null);
            // a rejected name already shows its suggestions; otherwise check what is in the box (also finds suggestions)
            bool haveSuggestions = rejected && LeaderboardApi.LastSuggestions.Length > 0;
            nameLastTyped = nameInput.text; nameCheckAt = haveSuggestions ? -1f : Time.unscaledTime + 0.1f;
            nameBlocker.gameObject.SetActive(true);
            namePanel.gameObject.SetActive(true);
            nameInput.ActivateInputField();
        }

        void CloseNamePanel()
        {
            if (namePanel == null) return;
            namePanel.gameObject.SetActive(false);
            nameBlocker.gameObject.SetActive(false);
        }

        void OpenNameEditor()
        {
            if (namePanel == null) BuildNameEditor();
            nameCreating = false;
            nameTitle.text = "EDIT NAME";
            nameClose.gameObject.SetActive(true);
            nameBlocker.gameObject.SetActive(true);
            bool used = PlayerPrefs.GetInt("nameChanged", 0) == 1;
            nameInput.text = PlayerName();
            nameInput.interactable = !used;
            nameSave.gameObject.SetActive(!used);
            nameNote.text = used ? "You already used your one name change." : "You can change your name ONE time. 3-16 letters or numbers.";
            nameNote.color = used ? new Color(1f, 0.5f, 0.45f) : Gold;
            ShowSuggestions(null);
            nameLastTyped = nameInput.text; nameCheckAt = -1f;
            namePanel.gameObject.SetActive(true);
            if (!used) nameInput.ActivateInputField();
        }

        void BuildNameEditor()
        {
            nameBlocker = NewRect("NameBlocker", root);   // dark full-screen layer: nothing behind the popup can be clicked
            nameBlocker.anchorMin = Vector2.zero; nameBlocker.anchorMax = Vector2.one; nameBlocker.offsetMin = nameBlocker.offsetMax = Vector2.zero;
            var blk = nameBlocker.gameObject.AddComponent<Image>(); blk.color = new Color(0.02f, 0.03f, 0.07f, 0.72f); blk.raycastTarget = true;
            namePanel = NewRect("NameEditor", root);
            PlaceRef(namePanel, 560, 280, 800, 440);
            var bg = namePanel.gameObject.AddComponent<Image>(); bg.sprite = Rounded(); bg.type = Image.Type.Sliced; bg.color = Navy;
            var o = namePanel.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.02f, 0.02f, 0.05f, 1f); o.effectDistance = new Vector2(4, -4);
            var title = NewRect("Title", namePanel); title.anchorMin = title.anchorMax = new Vector2(0.5f, 1); title.pivot = new Vector2(0.5f, 1);
            title.anchoredPosition = new Vector2(0, -28); title.sizeDelta = new Vector2(740, 60);
            nameTitle = Label(title, "EDIT NAME", titleFont, 52, Sand, TextAnchor.MiddleCenter, 3f);
            var box = NewRect("Input", namePanel); box.anchorMin = box.anchorMax = new Vector2(0.5f, 1); box.pivot = new Vector2(0.5f, 1);
            box.anchoredPosition = new Vector2(0, -110); box.sizeDelta = new Vector2(640, 70);
            nameInput = InputBox(box, "", 38, true);
            nameInput.characterLimit = 16;
            nameInput.onValidateInput = (text, index, c) => char.IsLetterOrDigit(c) || c == ' ' || c == '_' ? char.ToUpperInvariant(c) : '\0';
            var note = NewRect("Note", namePanel); note.anchorMin = note.anchorMax = new Vector2(0.5f, 1); note.pivot = new Vector2(0.5f, 1);
            note.anchoredPosition = new Vector2(0, -196); note.sizeDelta = new Vector2(740, 40);
            nameNote = Label(note, "", bodyFont, 26, Gold, TextAnchor.MiddleCenter, 2f);
            nameInput.onSubmit.AddListener(_ => SaveName());   // Enter / the phone keyboard's done key
            for (int i = 0; i < suggestBtns.Length; i++)
            {
                int k = i;
                suggestBtns[i] = SmallButton(namePanel, "TRY", new Rect(40 + i * 245, 248, 230, 48), new Color(0.2f, 0.27f, 0.5f), () =>
                {
                    if (!nameInput.interactable) return;
                    nameInput.text = suggestTexts[k].text; nameInput.MoveTextEnd(false);
                }, 22);
                suggestTexts[i] = suggestBtns[i].GetComponentInChildren<Text>();
                suggestBtns[i].gameObject.SetActive(false);
            }
            nameSave = SmallButton(namePanel, "SAVE", new Rect(190, 336, 200, 66), Green, SaveName, 32);
            nameClose = SmallButton(namePanel, "CLOSE", new Rect(410, 336, 200, 66), new Color(0.6f, 0.2f, 0.25f), CloseNamePanel, 32);
        }

        void ShowSuggestions(string[] names)
        {
            if (namePanel == null) return;
            for (int i = 0; i < suggestBtns.Length; i++)
            {
                bool on = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]);
                suggestBtns[i].gameObject.SetActive(on);
                if (on) suggestTexts[i].text = names[i];
            }
        }

        static string CleanTyped(string s)
        {
            var n = (s ?? "").Trim();
            while (n.Contains("  ")) n = n.Replace("  ", " ");
            return n;
        }

        // Live check: 0.5 s after the last key, ask the server whether the name is free (look-alikes count as the same name).
        void UpdateNameCheck()
        {
            if (namePanel == null || !namePanel.gameObject.activeSelf || !nameInput.interactable || nameBusy) return;
            if (nameInput.text != nameLastTyped) { nameLastTyped = nameInput.text; nameCheckAt = Time.unscaledTime + 0.5f; }
            if (nameCheckAt < 0f || Time.unscaledTime < nameCheckAt) return;
            nameCheckAt = -1f;
            var n = CleanTyped(nameInput.text);
            var bad = new Color(1f, 0.5f, 0.45f);
            if (n.Length < 3) { nameNote.text = "Your name on the leaderboard. 3-16 letters or numbers."; nameNote.color = Gold; ShowSuggestions(null); return; }
            if (!nameCreating && n == PlayerName()) { nameNote.text = "That is your name now."; nameNote.color = Gold; ShowSuggestions(null); return; }
            int seq = ++nameCheckSeq;
            nameNote.text = "Checking " + n + "..."; nameNote.color = Gold;
            LeaderboardApi.CheckName(n, r =>
            {
                if (this == null || seq != nameCheckSeq || CleanTyped(nameInput.text) != n) return;   // typed on meanwhile
                if (r == null) { nameNote.text = "Can't check right now - you can still save it."; nameNote.color = Gold; ShowSuggestions(null); return; }
                if (r.available) { nameNote.text = n + " is free!"; nameNote.color = Green; ShowSuggestions(null); return; }
                nameNote.text = r.reason == "invalid" ? "Only letters, numbers, space or _ (3-16)."
                              : r.reason == "reserved" ? "Pick your own name." : n + " is taken. Try one of these:";
                nameNote.color = bad;
                ShowSuggestions(r.suggestions);
            });
        }

        // A pending name (saved offline / from before the rule): claim it on the server now and then, quietly.
        void UpdateNameConfirm()
        {
            if (confirmBusy || !NameCreated || PlayerPrefs.GetInt("nameConfirmed", 0) == 1 || Time.unscaledTime < confirmTryAt) return;
            if (namePanel != null && namePanel.gameObject.activeSelf) return;
            confirmBusy = true; confirmTryAt = Time.unscaledTime + 30f;
            var n = PlayerName();
            LeaderboardApi.ClaimName(n, result =>
            {
                confirmBusy = false;
                if (result == "offline" || PlayerName() != n) return;   // try again later
                if (result == "ok") { PlayerPrefs.SetInt("nameConfirmed", 1); PlayerPrefs.Save(); return; }
                PlayerPrefs.SetInt("nameRejected", 1);                  // taken / invalid: must pick a new one
                PlayerPrefs.Save();
                if (this != null) OpenNameCreator();
            });
        }

        void SaveName()
        {
            var n = CleanTyped(nameInput.text);
            var bad = new Color(1f, 0.5f, 0.45f);
            if (nameBusy) return;
            if (n.Length < 3) { nameNote.text = "At least 3 letters."; nameNote.color = bad; return; }
            if (n == "GUEST CAT") { nameNote.text = "Pick your own name."; nameNote.color = bad; return; }
            if (!nameCreating && n == PlayerName()) { CloseNamePanel(); return; }   // unchanged: keep the one change
            nameBusy = true;
            nameNote.text = "Checking the name..."; nameNote.color = Gold;
            LeaderboardApi.ClaimName(n, result =>
            {
                nameBusy = false;
                if (this == null) return;
                if (result == "taken")
                {
                    nameNote.text = n + " is taken. Try one of these:"; nameNote.color = bad;
                    ShowSuggestions(LeaderboardApi.LastSuggestions);
                    return;
                }
                if (result == "invalid") { nameNote.text = "Only letters, numbers, space or _ (3-16)."; nameNote.color = bad; return; }
                // "ok" = reserved on the server; "offline" = kept on this device as pending, claimed later (UpdateNameConfirm)
                PlayerPrefs.SetString("playerName", n);
                PlayerPrefs.SetInt(nameCreating ? "nameCreated" : "nameChanged", 1);
                PlayerPrefs.SetInt("nameConfirmed", result == "ok" ? 1 : 0);
                PlayerPrefs.DeleteKey("nameRejected");
                PlayerPrefs.Save();
                confirmTryAt = Time.unscaledTime + 30f;
                CloseNamePanel();
                Toast((nameCreating ? "WELCOME, " : "NAME CHANGED · ") + n + (result == "ok" ? "" : " · CHECKED WHEN ONLINE"));
            });
        }

        /// <summary>Profile rank = the best pass card the wallet owns (NORMAL without cards).</summary>
        static string RankVariant(NetworkBootstrap nb)
        {
            var best = -1;
            if (nb != null)
                foreach (var c in nb.Cards)
                {
                    int r = c.tier == CharacterTier.Legendary ? 3 : c.tier == CharacterTier.Rare ? 2 : c.tier == CharacterTier.Common ? 1 : 0;
                    if (r > best) best = r;
                }
            return best == 3 ? "legendary" : best == 2 ? "rare" : best == 1 ? "common" : "normal";
        }

        void SendChat()
        {
            var msg = chatInput.text.Trim();
            if (msg.Length == 0) return;
            chat.Add("<color=#ffd34d>" + PlayerName() + ":</color> " + msg.Replace("<", "‹"));
            chatInput.text = "";
            chatInput.ActivateInputField();
        }

        void Toast(string text)
        {
            toastText.text = text; toastTime = Time.unscaledTime;
            toastBg.gameObject.SetActive(true);
        }

        static string PlayerName() => PlayerPrefs.GetString("playerName", "GUEST CAT");

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool connected = nm != null && (nm.IsClient || nm.IsServer);
            bool show = !connected && !GameUI.SettingsOpen;
            if (content.gameObject.activeSelf != show)
            {
                content.gameObject.SetActive(show);
                root.GetChild(0).gameObject.SetActive(show);    // backdrop
                if (!show) optionsPanel.gameObject.SetActive(false);
            }
            Active = !connected;
            if (preview != null) preview.gameObject.SetActive(show);
            if (show && !lobbyShown) WalletSession.RefreshCmbg();   // back in the lobby (e.g. after a match or a buy): fresh balance
            lobbyShown = show;
            if (!show) { statusRow.SetActive(false); CloseNamePanel(); return; }
            if (!NameCreated && (namePanel == null || !namePanel.gameObject.activeSelf)) OpenNameCreator();
            UpdateNameCheck();
            UpdateNameConfirm();

            var nb = NetworkBootstrap.Instance;
            UpdateCat(nb);
            walletText.text = WalletSession.Connected
                ? WalletSession.Short + (WalletSession.Loading ? " · …" : " · " + WalletSession.Cards.Count + (WalletSession.Cards.Count == 1 ? " CARD" : " CARDS"))
                : "";
            if (walletButton != null && walletConnectedSprite != null)
            {
                var want = WalletSession.Connected ? walletConnectedSprite : walletIdleSprite;
                if (walletButton.sprite != want) walletButton.sprite = want;
            }
            nameText.text = PlayerName();
            UpdateProfile(nb);
            // 2026-11-10: the coin shows the connected wallet's real $CMBG (no wallet / not read yet = "BUY")
            double cmbg = WalletSession.Connected ? WalletSession.CmbgBalance : -1;
            coinText.text = cmbg >= 0 ? ShortAmount(cmbg) : WalletSession.Connected ? "…" : "BUY";
            onlineText.text = "DEMO";
            int xp = PlayerPrefs.GetInt("passXp", 150), xpMax = 200;
            passFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01((float)xp / xpMax), 1f);
            passText.text = xp + "/" + xpMax;
            for (int i = 0; i < chatLines.Count; i++)
            {
                int k = chat.Count - chatLines.Count + i;
                chatLines[i].text = k >= 0 ? chat[k] : "";
            }
            statusRow.SetActive(nb != null && !string.IsNullOrEmpty(nb.Status));
            if (statusRow.activeSelf) statusText.text = nb.Status;

            if (optionsPanel.gameObject.activeSelf && nb != null) UpdateOptions(nb);

            // CHAMPIONS "coming soon" stamp: pop in, hold, gone after 1.6 s
            if (champStamp.gameObject.activeSelf)
            {
                float ct = Time.unscaledTime - champStampTime;
                if (ct > 1.6f) champStamp.gameObject.SetActive(false);
                else
                {
                    float s = ct < 0.12f ? Mathf.Lerp(0.3f, 1.15f, ct / 0.12f) : ct < 0.22f ? Mathf.Lerp(1.15f, 1f, (ct - 0.12f) / 0.1f)
                            : ct > 1.35f ? Mathf.Lerp(1f, 0f, (ct - 1.35f) / 0.25f) : 1f;
                    champStamp.localScale = Vector3.one * s;
                }
            }

            // toast pop
            float ta = Time.unscaledTime - toastTime;
            bool toastOn = ta < 1.8f;
            if (toastBg.gameObject.activeSelf != toastOn) toastBg.gameObject.SetActive(toastOn);
            if (toastOn)
            {
                float s = ta < 0.15f ? Mathf.Lerp(0.6f, 1.08f, ta / 0.15f) : ta < 0.25f ? Mathf.Lerp(1.08f, 1f, (ta - 0.15f) / 0.1f) : 1f;
                toastBg.rectTransform.localScale = Vector3.one * s;
                var c = toastBg.color; c.a = 0.9f * Mathf.Clamp01((1.8f - ta) / 0.3f); toastBg.color = c;
                var tc = toastText.color; tc.a = Mathf.Clamp01((1.8f - ta) / 0.3f); toastText.color = tc;
            }
        }

        string shownRank, shownAvatar;
        void UpdateProfile(NetworkBootstrap nb)
        {
            var rank = RankVariant(nb);
            if (rank != shownRank) { shownRank = rank; profileImg.sprite = SpriteOf("16_profile_" + rank); }
            if (ProfileCharacterId != shownAvatar)
            {
                shownAvatar = ProfileCharacterId;
                // own portrait per character when it exists, otherwise the portrait painted into the profile art stays
                var pic = string.IsNullOrEmpty(shownAvatar) ? null : Resources.Load<Sprite>("UI/Avatars/" + shownAvatar);
                if (pic == null && !string.IsNullOrEmpty(shownAvatar))
                {
                    var tex = Resources.Load<Texture2D>("UI/Avatars/" + shownAvatar);
                    if (tex != null) pic = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
                avatarImg.sprite = pic;
                avatarImg.transform.parent.gameObject.SetActive(pic != null);
            }
        }

        void UpdateCat(NetworkBootstrap nb)
        {
            if (nb == null) return;
            // shown cat: command-line test pick (-cat) > the selected (opened) pass card > the original Army cat
            string id = !string.IsNullOrEmpty(nb.TestCharacter) ? nb.TestCharacter
                      : nb.TryGetSelected(out var sel) && sel.Playable ? sel.characterId : "army";
            ProfileCharacterId = id;
            if (id == shownCat && preview != null) return;
            shownCat = id;
            CharacterCatalog.Entry entry = null;
            foreach (var e in nb.Characters) if (e != null && e.id == id) entry = e;
            if (entry == null) foreach (var e in nb.Characters) if (e != null && e.tier == CharacterTier.Original) entry = e;
            if (preview == null) preview = LobbyCharacterPreview.Create();
            preview.Show(entry != null ? entry.playerPrefab : null);
            previewImage.texture = preview.Texture;
            previewImage.color = Color.white;
            catName.text = entry != null ? (entry.displayName ?? entry.id).ToUpperInvariant() : "";
            catWeapon.text = entry != null ? Weapons.Get(Weapons.ForCharacter(entry.id)).name.ToUpperInvariant() : "";

        }

        void UpdateOptions(NetworkBootstrap nb)
        {
            walletNote.text = nb.MockCards ? "MOCK CARDS (testing)"
                : !WalletSession.Connected ? "CONNECT YOUR WALLET (TOP RIGHT) TO PLAY YOUR CARDS"
                : WalletSession.Loading ? "READING CARDS OF " + WalletSession.Short.ToUpperInvariant() + " ..."
                : !string.IsNullOrEmpty(WalletSession.Error) ? WalletSession.Error.ToUpperInvariant()
                : WalletSession.Cards.Count == 0 ? "NO PASS CARDS IN " + WalletSession.Short.ToUpperInvariant()
                : WalletSession.Short.ToUpperInvariant() + " · SEALED CARDS OPEN ON THE WEBSITE";
            int version = nb.MockCards ? nb.Cards.Count : WalletSession.Version;
            if (builtCards != version)
            {
                foreach (var b in cardButtons) Destroy(b.bg.gameObject);
                cardButtons.Clear();
                var options = new List<(string token, string label, PassCard card)> { ("", "ORIGINAL", default) };
                foreach (var card in nb.Cards) options.Add((card.tokenId, CardLabel(card), card));
                for (int i = 0; i < options.Count && i < 6; i++)
                {
                    var (token, label, card) = options[i];
                    var img = SmallButton(cardList, label, new Rect((i % 3) * 198, (i / 3) * 48, 190, 42), new Color(0.2f, 0.27f, 0.5f),
                        () => { if (token == "") nb.SelectedCard = ""; else CardClicked(nb, card); }, label.Length > 14 ? 15 : 18);
                    cardButtons.Add((token, img, img.GetComponentInChildren<Text>()));
                }
                builtCards = version;
            }
            foreach (var (token, bg, label) in cardButtons) bg.color = token == nb.SelectedCard ? Gold : new Color(0.2f, 0.27f, 0.5f);

            if (testList == null) return;
            if (builtTest != nb.Characters.Count)
            {
                foreach (var b in testButtons) Destroy(b.bg.gameObject);
                testButtons.Clear();
                var options = new List<(string, string)> { ("", "CARD") };
                foreach (var e in nb.Characters) if (e != null && e.playerPrefab != null) options.Add((e.id, (string.IsNullOrEmpty(e.displayName) ? e.id : e.displayName).ToUpperInvariant()));
                for (int i = 0; i < options.Count && i < 15; i++)
                {
                    var (id, label) = options[i];
                    var img = SmallButton(testList, label, new Rect((i % 5) * 118, (i / 5) * 46, 112, 40), new Color(0.2f, 0.27f, 0.5f), () => nb.TestCharacter = id, 18);
                    testButtons.Add((id, img, img.GetComponentInChildren<Text>()));
                }
                builtTest = nb.Characters.Count;
            }
            foreach (var (id, bg, label) in testButtons) bg.color = id == nb.TestCharacter ? Gold : new Color(0.2f, 0.27f, 0.5f);
        }

        // ------------------------------------------------------------------ generated textures
        static Texture2D gradient, radial; static Sprite rounded;

        static Texture2D Gradient()
        {
            if (gradient != null) return gradient;
            gradient = new Texture2D(4, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 64; y++)
            {
                float t = y / 63f;   // 0 bottom .. 1 top
                var c = Color.Lerp(new Color(0.04f, 0.03f, 0.1f, 0.96f), new Color(0.09f, 0.12f, 0.28f, 0.9f), t);
                for (int x = 0; x < 4; x++) gradient.SetPixel(x, y, c);
            }
            gradient.Apply();
            return gradient;
        }

        static Texture2D Radial()
        {
            if (radial != null) return radial;
            const int n = 128;
            radial = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = new Vector2((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f).magnitude * 2f;
                    float a = Mathf.Pow(Mathf.Clamp01(1f - r), 1.8f);
                    radial.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            radial.Apply();
            return radial;
        }

        static Sprite lockSprite;
        /// <summary>A cartoon golden padlock (drawn in code): steel shackle, gold body with a dark outline and a keyhole.</summary>
        static Sprite LockSprite()
        {
            if (lockSprite != null) return lockSprite;
            const int n = 128;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var clear = new Color(0, 0, 0, 0);
            var outline = new Color(0.08f, 0.06f, 0.1f, 1f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    Color c = clear;
                    // shackle: half ring above the body + two legs
                    float dx = px - 64f, dy = py - 78f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    bool upper = py >= 78f && r <= 34f && r >= 18f;
                    bool legs = py < 78f && py > 50f && (Mathf.Abs(px - 38f) <= 8f || Mathf.Abs(px - 90f) <= 8f);
                    bool upperO = py >= 78f && r <= 39f && r >= 13f;
                    bool legsO = py < 78f && py > 46f && (Mathf.Abs(px - 38f) <= 13f || Mathf.Abs(px - 90f) <= 13f);
                    if (upperO || legsO) c = outline;
                    if (upper || legs) c = Color.Lerp(new Color(0.62f, 0.66f, 0.74f), new Color(0.9f, 0.93f, 0.98f), Mathf.Clamp01((px - 30f) / 70f));
                    // body: rounded box with outline, vertical gold gradient, a keyhole
                    float bx = Mathf.Max(0f, Mathf.Abs(px - 64f) - 36f), by = Mathf.Max(0f, Mathf.Abs(py - 36f) - 22f);
                    float bd = Mathf.Sqrt(bx * bx + by * by);
                    if (bd <= 12f) c = outline;
                    if (bd <= 7f)
                    {
                        c = Color.Lerp(new Color(0.93f, 0.55f, 0.08f), new Color(1f, 0.86f, 0.25f), Mathf.Clamp01((py - 10f) / 55f));
                        if (py > 52f && py < 60f && Mathf.Abs(px - 64f) < 30f) c = Color.Lerp(c, Color.white, 0.35f);   // shine
                        float kx = px - 64f, ky = py - 40f;
                        if (kx * kx + ky * ky < 64f || (Mathf.Abs(kx) < 3.5f && py < 40f && py > 22f)) c = outline;    // keyhole
                    }
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            lockSprite = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            return lockSprite;
        }

        static Sprite Rounded()
        {
            if (rounded != null) return rounded;
            const int n = 64, r = 18;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(0, Mathf.Max(r - x, x - (n - 1 - r))), dy = Mathf.Max(0, Mathf.Max(r - y, y - (n - 1 - r)));
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    t.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            t.Apply();
            rounded = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            return rounded;
        }
    }

    /// <summary>Hover grows, press squashes, release springs back - the cartoon button feel of the lobby art.</summary>
    public class LobbyButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public System.Action onClick;
        public bool juicy = true;
        bool over, down;
        float scale = 1f;

        public void OnPointerEnter(PointerEventData e) { over = true; }
        public void OnPointerExit(PointerEventData e) { over = false; down = false; }
        public void OnPointerDown(PointerEventData e) { down = true; }
        public void OnPointerUp(PointerEventData e) { down = false; }
        public void OnPointerClick(PointerEventData e) { onClick?.Invoke(); scale = 1.12f; }

        void Update()
        {
            if (!juicy) return;
            float target = down ? 0.93f : over ? 1.05f : 1f;
            scale = Mathf.Lerp(scale, target, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
            transform.localScale = Vector3.one * scale;
        }
    }

    /// <summary>Padlock pop on a locked lobby button: the lock scales in with a wobble, the button shakes, gone after 1.4 s.</summary>
    public class LobbyLock : MonoBehaviour
    {
        public RectTransform icon;
        float start = -10f;

        public void Play() { start = Time.unscaledTime; icon.gameObject.SetActive(true); }

        void Update()
        {
            if (icon == null || !icon.gameObject.activeSelf) return;
            float t = Time.unscaledTime - start;
            if (t > 1.4f) { icon.gameObject.SetActive(false); transform.localRotation = Quaternion.identity; return; }
            float s = t < 0.12f ? Mathf.Lerp(0.2f, 1.25f, t / 0.12f) : t < 0.24f ? Mathf.Lerp(1.25f, 1f, (t - 0.12f) / 0.12f)
                    : t > 1.15f ? Mathf.Lerp(1f, 0f, (t - 1.15f) / 0.25f) : 1f;
            icon.localScale = Vector3.one * s;
            icon.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 30f) * 14f * Mathf.Exp(-t * 5f));
            transform.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 45f) * 4f * Mathf.Exp(-t * 7f));
        }
    }

    /// <summary>Slow breathing scale for the PLAY button.</summary>
    public class LobbyPulse : MonoBehaviour
    {
        public float amount = 0.03f;
        // LobbyButton sets the scale in Update; this multiplies the breathing on top in LateUpdate
        void LateUpdate() => transform.localScale *= 1f + Mathf.Sin(Time.unscaledTime * 3f) * amount;
    }

    /// <summary>Drag horizontally to turn the lobby cat.</summary>
    public class LobbyDrag : MonoBehaviour, IDragHandler
    {
        public System.Action<float> onDrag;
        public void OnDrag(PointerEventData e) => onDrag?.Invoke(e.delta.x);
    }
}
