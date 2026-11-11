using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// The in-match HUD from the user's mock-up ("CMBG FINAL UI/Gameplay_HUD_layout.png"; pieces cut out by
    /// Tools/UI/extract_gameplay_hud.py into Resources/UI/GameHUD, positions in 1920x1080 px from Tools/UI/gameplay_hud_layout.json):
    ///  * top left: ALIVE and KILLS cards, PLAYERS ALIVE list (portrait, name, kills, health bar; you highlighted in green);
    ///  * top centre: compass strip (5 deg ticks, numbers every 15, green N/E/S/W, green marker);
    ///  * top right: round minimap (north up, map scrolls under you, zone circles, your arrow + view cone, green arc, N, "[M] BIG MAP");
    ///  * bottom right: weapon slots (WEAPON / BOMB with its count; KNIFE hidden until knives exist) and the weapon panel.
    /// The HP / armor panel is <see cref="VitalsHud"/>. GameUI hides its own placeholders for these while this runs.
    /// Other players get stable cat names from their match number (bots stay anonymous among them).
    /// </summary>
    public class GameHud : MonoBehaviour
    {
        public static bool Active { get; private set; }
        /// <summary>Bottom of the minimap in screen pixels (GameUI stacks the zone panel and kill feed under it).</summary>
        public static float MinimapBottomPx { get; private set; }
        /// <summary>The round minimap (TouchControls: tapping it opens the big map on phones).</summary>
        public static RectTransform MinimapRect { get; private set; }
        static GameHud instance;

        const float RefW = 1920f, RefH = 1080f;
        static readonly Color Lime = new Color(0.66f, 0.92f, 0.18f), Cyan = new Color(0.3f, 0.78f, 1f), Pink = new Color(1f, 0.33f, 0.45f),
            White = new Color(0.97f, 0.97f, 0.97f), Dim = new Color(0.72f, 0.75f, 0.8f), Bar = new Color(0.18f, 0.88f, 0.55f),
            Yellow = new Color(1f, 0.8f, 0.2f), Red = new Color(1f, 0.32f, 0.3f), Track = new Color(0.35f, 0.38f, 0.44f, 0.9f);

        Font anton, barlow;
        RectTransform root, content;
        Canvas canvas;
        Text aliveNum, killsNum, weaponName, ammoText, reserveText, bombCount;
        Image bombSlot;

        // players list
        class Row { public RectTransform rt; public Image bg, mark, avatar, hp; public Text name, kills; }
        readonly List<Row> rows = new List<Row>();
        readonly List<NetworkPlayer> players = new List<NetworkPlayer>();
        float nextScan;

        // compass
        RectTransform compassStrip;
        readonly List<RectTransform> ticks = new List<RectTransform>();
        readonly List<Text> tickLabels = new List<Text>();
        const float PxPerDeg = 4.67f, CompassHalfDeg = 58f;

        // minimap
        RawImage mapImg; RectTransform mapRt, arrowRt, coneRt;
        readonly List<Image> zoneNow = new List<Image>(), zoneNext = new List<Image>();
        const float MapRadiusPx = 108f, MapViewMetres = 70f;
        const int ZoneSegments = 48;
        MinimapHud minimap;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            if (Load("card_alive") == null) return;   // art not imported yet
            instance = new GameObject("GameHud").AddComponent<GameHud>();
        }

        static Sprite Load(string name)
        {
            var s = Resources.Load<Sprite>("UI/GameHUD/" + name);
            if (s != null) return s;
            var t = Resources.Load<Texture2D>("UI/GameHUD/" + name);
            return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f)) : null;
        }

        // ------------------------------------------------------------------ layout helpers (1920x1080 top-left px)
        static RectTransform NewRect(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

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

        /// <summary>A child placed in the parent's own coordinates, given in mock-up pixels relative to the parent's mock-up top-left.</summary>
        static RectTransform Local(RectTransform parent, float x, float y, float w, float h)
        {
            var rt = NewRect("L", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        Image Piece(string sprite, float x, float y, float w, float h, Transform parent = null)
        {
            var rt = NewRect(sprite, parent != null ? parent : content);
            PlaceRef(rt, x, y, w, h);
            var img = rt.gameObject.AddComponent<Image>(); img.sprite = Load(sprite); img.raycastTarget = false;
            return img;
        }

        Text Label(RectTransform rt, Font font, int size, Color color, TextAnchor anchor, FontStyle style = FontStyle.Italic, float outline = 2f)
        {
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.color = color; t.alignment = anchor; t.fontStyle = style; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            if (outline > 0f) { var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.02f, 0.03f, 0.06f, 0.9f); o.effectDistance = new Vector2(outline, -outline); }
            return t;
        }

        static Image Rect(Transform parent, Color c)
        {
            var img = NewRect("R", parent).gameObject.AddComponent<Image>();
            img.color = c; img.raycastTarget = false;
            return img;
        }

        // ------------------------------------------------------------------ build
        void Awake()
        {
            instance = this;
            var builtIn = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            anton = Resources.Load<Font>("Fonts/Anton") ?? builtIn;
            barlow = Resources.Load<Font>("Fonts/Barlow") ?? anton;
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 11;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.matchWidthOrHeight = 1f;
            root = (RectTransform)transform;
            content = NewRect("Content", root); content.anchorMin = Vector2.zero; content.anchorMax = Vector2.one; content.offsetMin = content.offsetMax = Vector2.zero;

            // ALIVE / KILLS cards (numbers live)
            var alive = Piece("card_alive", 27, 23, 220, 79);
            aliveNum = Label(Local(alive.rectTransform, 97, 26, 90, 44), anton, 40, White, TextAnchor.MiddleLeft);
            var kills = Piece("card_kills", 247, 23, 220, 79);
            killsNum = Label(Local(kills.rectTransform, 96, 26, 90, 44), anton, 40, White, TextAnchor.MiddleLeft);

            // PLAYERS ALIVE
            var head = NewRect("PlayersAlive", content); PlaceRef(head, 28, 124, 220, 20);
            Label(head, barlow, 18, Cyan, TextAnchor.MiddleLeft, FontStyle.Italic, 1.5f).text = "PLAYERS ALIVE";
            for (int i = 0; i < 10; i++) rows.Add(MakeRow(i));

            BuildCompass();
            BuildMinimap();

            // weapon slots + weapon panel
            Piece("slot_weapon", 1520, 810, 131, 122);
            // no knife yet (2026-10-28): the KNIFE slot is left out and BOMB takes its place next to the weapon
            bombSlot = Piece("slot_bomb", 1664, 829, 95, 98);
            bombCount = Label(Local(bombSlot.rectTransform, 52, 58, 40, 34), anton, 30, Lime, TextAnchor.MiddleRight);
            var wp = Piece("weapon_panel", 1483, 951, 410, 109);
            weaponName = Label(Local(wp.rectTransform, 140, 9, 240, 30), barlow, 28, Lime, TextAnchor.MiddleLeft);
            ammoText = Label(Local(wp.rectTransform, 137, 36, 170, 62), anton, 54, White, TextAnchor.MiddleLeft);
            reserveText = Label(Local(wp.rectTransform, 300, 46, 76, 46), anton, 38, Lime, TextAnchor.MiddleLeft);

            content.gameObject.SetActive(false);
        }

        void OnDestroy() { if (instance == this) { instance = null; Active = false; } }

        Row MakeRow(int i)
        {
            var r = new Row();
            float y = 152f + i * 56.1f;
            r.rt = NewRect("Row" + i, content); PlaceRef(r.rt, 18, y, 222, 52);
            r.bg = r.rt.gameObject.AddComponent<Image>(); r.bg.color = new Color(0.1f, 0.12f, 0.16f, 0.45f); r.bg.raycastTarget = false;
            r.mark = Rect(r.rt, Lime); Place(r.mark.rectTransform, 0, 2, 4, 48);
            var av = Local(r.rt, 7, 1, 55, 55);
            r.avatar = av.gameObject.AddComponent<Image>(); r.avatar.raycastTarget = false;
            r.name = Label(Local(r.rt, 72, 4, 120, 26), barlow, 22, White, TextAnchor.MiddleLeft, FontStyle.Italic, 1.5f);
            var skull = Local(r.rt, 183, 13, 18, 19).gameObject.AddComponent<Image>(); skull.sprite = Load("row_skull"); skull.raycastTarget = false;
            r.kills = Label(Local(r.rt, 202, 10, 22, 26), barlow, 20, White, TextAnchor.MiddleLeft, FontStyle.Italic, 1.5f);
            var track = Rect(r.rt, Track); Place(track.rectTransform, 70, 34, 112, 5);
            r.hp = Rect(r.rt, Bar); Place(r.hp.rectTransform, 70, 34, 112, 5);
            r.hp.rectTransform.pivot = new Vector2(0, 1);
            r.rt.gameObject.SetActive(false);
            return r;
        }

        static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }

        void BuildCompass()
        {
            var bar = NewRect("Compass", content); PlaceRef(bar, 680, 20, 560, 36);
            var bg = bar.gameObject.AddComponent<Image>(); bg.sprite = Rounded(); bg.type = Image.Type.Sliced; bg.color = new Color(0.13f, 0.15f, 0.19f, 0.92f); bg.raycastTarget = false;
            bar.gameObject.AddComponent<RectMask2D>();
            compassStrip = NewRect("Strip", bar); compassStrip.anchorMin = compassStrip.anchorMax = new Vector2(0.5f, 0.5f); compassStrip.sizeDelta = Vector2.zero;
            int n = Mathf.CeilToInt(CompassHalfDeg * 2f / 5f) + 2;
            for (int i = 0; i < n; i++)
            {
                var t = Rect(compassStrip, Dim).rectTransform; t.anchorMin = t.anchorMax = new Vector2(0.5f, 0.5f); ticks.Add(t);
                var lr = NewRect("Label", compassStrip); lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 0.5f); lr.sizeDelta = new Vector2(40, 16);
                tickLabels.Add(Label(lr, barlow, 13, White, TextAnchor.MiddleCenter, FontStyle.Italic, 1f));
            }
            var marker = NewRect("Marker", content); PlaceRef(marker, 953, 51, 14, 10);
            var mi = marker.gameObject.AddComponent<Image>(); mi.sprite = Triangle(); mi.color = Lime; mi.raycastTarget = false;
        }

        void BuildMinimap()
        {
            minimap = FindAnyObjectByType<MinimapHud>();
            var holder = NewRect("Minimap", content); PlaceRef(holder, 1655, 24, 234, 234);
            MinimapRect = holder;
            // green arc (decorative, around the top) and the dark ring
            var arc = NewRect("Arc", holder); arc.anchorMin = Vector2.zero; arc.anchorMax = Vector2.one; arc.offsetMin = arc.offsetMax = Vector2.zero;
            var ai = arc.gameObject.AddComponent<Image>(); ai.sprite = Ring(0.92f, 0.96f); ai.color = Lime; ai.raycastTarget = false;
            ai.type = Image.Type.Filled; ai.fillMethod = Image.FillMethod.Radial360; ai.fillOrigin = (int)Image.Origin360.Top; ai.fillAmount = 0.34f; ai.fillClockwise = true;
            arc.localRotation = Quaternion.Euler(0, 0, 0.34f * 180f);   // centre the arc on the top
            var mask = NewRect("Mask", holder); PlaceIn(mask, 117, 117, MapRadiusPx * 2f);
            var mimg = mask.gameObject.AddComponent<Image>(); mimg.sprite = Disc(); mimg.raycastTarget = false;
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            mimg.color = new Color(0.12f, 0.15f, 0.18f, 1f);
            mapRt = NewRect("Map", mask); mapRt.anchorMin = Vector2.zero; mapRt.anchorMax = Vector2.one; mapRt.offsetMin = mapRt.offsetMax = Vector2.zero;
            mapImg = mapRt.gameObject.AddComponent<RawImage>(); mapImg.raycastTarget = false; mapImg.color = new Color(0.78f, 0.82f, 0.86f, 1f);
            for (int i = 0; i < ZoneSegments; i++)
            {
                zoneNow.Add(Rect(mask, new Color(0.35f, 0.65f, 1f, 1f)));
                zoneNext.Add(Rect(mask, new Color(1f, 1f, 1f, 0.9f)));
            }
            coneRt = NewRect("Cone", mask); coneRt.anchorMin = coneRt.anchorMax = new Vector2(0.5f, 0.5f); coneRt.sizeDelta = new Vector2(90, 90);
            var ci = coneRt.gameObject.AddComponent<Image>(); ci.sprite = Cone(); ci.color = new Color(0.66f, 0.92f, 0.18f, 0.35f); ci.raycastTarget = false;
            arrowRt = NewRect("Arrow", mask); arrowRt.anchorMin = arrowRt.anchorMax = new Vector2(0.5f, 0.5f); arrowRt.sizeDelta = new Vector2(20, 24);
            var ar = arrowRt.gameObject.AddComponent<Image>(); ar.sprite = Triangle(); ar.color = Lime; ar.raycastTarget = false;
            var ring = NewRect("Ring", holder); PlaceIn(ring, 117, 117, MapRadiusPx * 2f + 8f);
            var ri = ring.gameObject.AddComponent<Image>(); ri.sprite = Ring(0.93f, 1f); ri.color = new Color(0.45f, 0.48f, 0.53f, 1f); ri.raycastTarget = false;
            var nn = NewRect("N", holder); PlaceIn(nn, 117, -8, 30); Label(nn, barlow, 20, White, TextAnchor.MiddleCenter, FontStyle.Italic, 1.5f).text = "N";
            var big = NewRect("BigMap", holder); PlaceIn(big, 117, 241, 140); big.sizeDelta = new Vector2(160, 20);
            Label(big, barlow, 15, Dim, TextAnchor.MiddleCenter, FontStyle.Italic, 1f).text = "[M] BIG MAP";
        }

        static void PlaceIn(RectTransform rt, float cx, float cy, float size)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(cx, -cy); rt.sizeDelta = new Vector2(size, size);
        }

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            var nm = NetworkManager.Singleton;
            var me = nm != null && nm.IsClient && nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>() : null;
            var match = MatchManager.Instance;
            var drop = me != null ? me.GetComponent<PlayerDrop>() : null;
            bool show = me != null && match != null && !me.Health.IsDead && !GameUI.SettingsOpen
                        && (match.State == MatchState.Waiting || match.State == MatchState.Playing);
            Active = me != null;
            if (content.gameObject.activeSelf != show) content.gameObject.SetActive(show);
            MinimapBottomPx = (24f + 234f + 30f) * canvas.scaleFactor;
            if (!show) return;

            bool inMatch = match.State == MatchState.Playing;
            aliveNum.text = (inMatch ? match.AliveCount : match.HumanCount).ToString();
            killsNum.text = me.Kills.ToString();

            UpdatePlayers(me);
            UpdateCompass();
            UpdateMinimap(me, match);

            var thrower = me.GetComponent<BombThrower>();
            if (thrower != null)
            {
                int n = thrower.Bombs;
                bombCount.text = "x" + n;
                bombCount.color = n > 0 ? Lime : new Color(1f, 0.45f, 0.4f);
                bombSlot.color = n > 0 ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            }

            var shooter = me.GetComponent<PlayerShooter>();
            if (shooter != null)
            {
                weaponName.text = shooter.Weapon.name.ToUpperInvariant();
                ammoText.text = (shooter.IsReloading ? "--" : shooter.Ammo.ToString()) + " / " + shooter.MagazineSize;
                bool low = !shooter.IsReloading && shooter.Ammo <= Mathf.Max(1, shooter.MagazineSize / 4);
                ammoText.color = low ? new Color(1f, 0.45f, 0.4f) : White;
                reserveText.text = shooter.Reserve.ToString();
            }
        }

        void UpdatePlayers(NetworkPlayer me)
        {
            if (Time.time >= nextScan)
            {
                nextScan = Time.time + 0.5f;
                players.Clear();
                foreach (var p in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
                    if (p != null && p.IsSpawned && p.Health != null && !p.Health.IsDead) players.Add(p);
                players.Sort((a, b) => a.Number.CompareTo(b.Number));
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                bool on = i < players.Count && players[i] != null && !players[i].Health.IsDead;
                if (r.rt.gameObject.activeSelf != on) r.rt.gameObject.SetActive(on);
                if (!on) continue;
                var p = players[i];
                bool mine = p == me;
                r.name.text = mine ? PlayerPrefs.GetString("playerName", "YOU")
                                   : PlayerNames.Of(p.Number, MatchManager.Instance != null ? MatchManager.Instance.NameSeed : 1);
                r.name.color = mine ? Lime : White;
                r.mark.enabled = mine;
                r.kills.text = p.Kills.ToString();
                var av = Load("avatar_" + (string.IsNullOrEmpty(p.CharacterId) ? "army" : p.CharacterId.ToLowerInvariant())) ?? Load("avatar_army");
                if (r.avatar.sprite != av) r.avatar.sprite = av;
                float hp = p.Health.Max > 0 ? Mathf.Clamp01(p.Health.Current / p.Health.Max) : 0f;
                r.hp.rectTransform.sizeDelta = new Vector2(112f * hp, 5f);
                r.hp.color = hp > 0.5f ? Bar : hp >= 0.2f ? Yellow : Red;
            }
        }

        void UpdateCompass()
        {
            var cam = Camera.main;
            float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            int first = Mathf.FloorToInt((yaw - CompassHalfDeg) / 5f) * 5;
            for (int i = 0; i < ticks.Count; i++)
            {
                int a = first + i * 5;
                float x = Mathf.DeltaAngle(yaw, a) * PxPerDeg;
                int n = ((a % 360) + 360) % 360;
                bool major = n % 15 == 0;
                bool cardinal = n % 90 == 0;
                bool inter = n % 45 == 0 && !cardinal;
                var t = ticks[i];
                t.anchoredPosition = new Vector2(x, -10f);
                t.sizeDelta = new Vector2(major ? 2f : 1.5f, major ? 8f : 5f);
                var lbl = tickLabels[i];
                lbl.gameObject.SetActive(major);
                if (!major) continue;
                lbl.rectTransform.anchoredPosition = new Vector2(x, 6f);
                lbl.text = cardinal ? (n == 0 ? "N" : n == 90 ? "E" : n == 180 ? "S" : "W") : inter ? (n == 45 ? "NE" : n == 135 ? "SE" : n == 225 ? "SW" : "NW") : n.ToString("000");
                lbl.color = cardinal || inter ? Lime : White;
                lbl.fontSize = cardinal || inter ? 18 : 13;
            }
        }

        void UpdateMinimap(NetworkPlayer me, MatchManager match)
        {
            if (minimap == null) minimap = FindAnyObjectByType<MinimapHud>();
            if (minimap == null || minimap.Map == null) return;
            if (mapImg.texture != minimap.Map) mapImg.texture = minimap.Map;
            float e = minimap.Extent;
            var pos = me.transform.position - minimap.Centre;   // the zombie town is not at the origin
            float span = MapViewMetres * 2f / (e * 2f);
            mapImg.uvRect = new UnityEngine.Rect((pos.x + e) / (2f * e) - span * 0.5f, (pos.z + e) / (2f * e) - span * 0.5f, span, span);
            float yaw = Camera.main != null ? Camera.main.transform.eulerAngles.y : me.transform.eulerAngles.y;
            arrowRt.localRotation = Quaternion.Euler(0, 0, -me.transform.eulerAngles.y);
            coneRt.localRotation = Quaternion.Euler(0, 0, -yaw);
            float pxPerM = MapRadiusPx / MapViewMetres;
            var zone = match.Zone;
            bool zoneOn = zone != null && zone.Active;
            DrawCircle(zoneNow, zoneOn, zoneOn ? zone.Center : Vector3.zero, zoneOn ? zone.Radius : 0f, pos, pxPerM, 3f, false);
            DrawCircle(zoneNext, zoneOn, zoneOn ? zone.NextCenter : Vector3.zero, zoneOn ? zone.NextRadius : 0f, pos, pxPerM, 2f, true);
        }

        static void DrawCircle(List<Image> segs, bool on, Vector3 centre, float radius, Vector3 me, float pxPerM, float thick, bool dashed)
        {
            var c = new Vector2(centre.x - me.x, centre.z - me.z) * pxPerM;
            float r = radius * pxPerM;
            float segLen = 2f * Mathf.PI * r / segs.Count + 1f;
            for (int i = 0; i < segs.Count; i++)
            {
                bool vis = on && r > 2f && (!dashed || i % 2 == 0);
                segs[i].enabled = vis;
                if (!vis) continue;
                float a = (i + 0.5f) / segs.Count * Mathf.PI * 2f;
                var rt = segs[i].rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                rt.sizeDelta = new Vector2(thick, Mathf.Min(segLen, 400f));
                rt.localRotation = Quaternion.Euler(0, 0, a * Mathf.Rad2Deg);
            }
        }

        // ------------------------------------------------------------------ generated sprites
        static Sprite rounded, triangle, disc, cone;
        static readonly Dictionary<int, Sprite> rings = new Dictionary<int, Sprite>();

        static Sprite Rounded()
        {
            if (rounded != null) return rounded;
            const int n = 32, r = 8;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = Mathf.Max(0, Mathf.Max(r - x, x - (n - 1 - r))), dy = Mathf.Max(0, Mathf.Max(r - y, y - (n - 1 - r)));
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f)));
            }
            t.Apply();
            return rounded = Sprite.Create(t, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        static Sprite Triangle()   // pointing up
        {
            if (triangle != null) return triangle;
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float v = (y + 0.5f) / n, half = 0.5f * (1f - v);
                float d = half - Mathf.Abs((x + 0.5f) / n - 0.5f);
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(d * n + 0.5f)));
            }
            t.Apply();
            return triangle = Sprite.Create(t, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }

        static Sprite Disc()
        {
            if (disc != null) return disc;
            const int n = 256;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float d = n * 0.5f - new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f).magnitude;
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(d)));
            }
            t.Apply();
            return disc = Sprite.Create(t, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }

        static Sprite Ring(float inner, float outer)
        {
            int key = Mathf.RoundToInt(inner * 1000) * 10000 + Mathf.RoundToInt(outer * 1000);
            if (rings.TryGetValue(key, out var s)) return s;
            const int n = 256;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float ri = inner * n * 0.5f, ro = outer * n * 0.5f;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float d = new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f).magnitude;
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(Mathf.Min(d - ri, ro - d) + 0.5f)));
            }
            t.Apply();
            return rings[key] = Sprite.Create(t, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }

        static Sprite Cone()   // 70 degree wedge pointing up from the centre, fading out
        {
            if (cone != null) return cone;
            const int n = 128;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                var p = new Vector2(x + 0.5f - n * 0.5f, y + 0.5f - n * 0.5f);
                float ang = Mathf.Abs(Mathf.Atan2(p.x, p.y)) * Mathf.Rad2Deg;
                float r = p.magnitude / (n * 0.5f);
                float a = ang < 35f && r < 1f ? Mathf.Clamp01((35f - ang) / 4f) * (1f - r * 0.6f) : 0f;
                t.SetPixel(x, y, new Color(1, 1, 1, a));
            }
            t.Apply();
            return cone = Sprite.Create(t, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }
    }
}
