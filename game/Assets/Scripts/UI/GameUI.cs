using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// Temporary uGUI version of the UI mockups (claude.ai canvas "Cat Battleground UI Mockups"): main menu, matchmaking, boarding
    /// countdown, drop / parachute prompts, in-game HUD, eliminated + spectate, results and pause / settings.
    /// Built from code at runtime (no prefab), one Screen Space Overlay canvas at 1280x720 reference size. Replaces the IMGUI
    /// placeholders (NetworkBootstrap, SimpleHud, MatchHud, SpectateController check <see cref="Active"/>); the minimap stays IMGUI.
    /// Fonts: drop TTFs at Assets/Resources/Fonts/Title.ttf (e.g. Lilita One) and Fonts/Numbers.ttf (e.g. Barlow Condensed Bold)
    /// to replace the built-in font.
    /// </summary>
    public class GameUI : MonoBehaviour
    {
        public static bool Active { get; private set; }
        /// <summary>True while a menu needs the mouse: clicks must not re-lock the cursor for the game.</summary>
        public static bool BlocksGameInput => instance != null && instance.needsCursor;
        /// <summary>Settings opened from the lobby (LobbyUI's gear button).</summary>
        public static void OpenSettings() { if (instance != null) instance.settingsFromMenu = true; }
        public static bool SettingsOpen => instance != null && instance.settingsFromMenu;
        static GameUI instance;

        // palette from the mockup
        static readonly Color Ink = Hex("11160F"), PanelC = new Color(0.067f, 0.086f, 0.059f, 0.86f), CardC = Hex("2A3320"), Olive = Hex("4E5B31"),
            Sand = Hex("F4EBD2"), Tan = Hex("E8D9B0"), Muted = Hex("CFC3A0"), Accent = Hex("CCFF00"), AccentDark = Hex("5E7500"),
            HpC = Hex("8BD450"), ArmorC = Hex("4AA8FF"), GoldC = Hex("F2C230"), Danger = new Color(0.86f, 0.24f, 0.2f, 0.85f),
            DarkBg = Hex("1B2316"), Shade = new Color(0.067f, 0.086f, 0.059f, 0.55f), Locked = Hex("8E8670");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;   // offline test scene keeps its IMGUI HUD
            new GameObject("GameUI").AddComponent<GameUI>();
        }

        Font bodyFont, titleFont, numFont;
        Sprite round;
        Canvas canvas;
        RectTransform root;
        bool needsCursor;
        bool pauseOpen;   // only ESC (or RESUME / LEAVE) opens and closes the pause menu

        // screens
        GameObject menu, serverPanel, matchmaking, countdown, hud, drop, eliminated, spectateBar, results, pause;
        // menu
        Text menuStatus, menuCatLine; InputField ipField; RectTransform cardList; int builtCards = -1; readonly List<(string token, Image bg, Text label)> cardButtons = new List<(string, Image, Text)>();
        // matchmaking / countdown
        Text mmTimer, mmPlayers, cdNumber; GameObject startNow;
        // HUD
        Text aliveText, killsText, zoneTitle, zoneTime, zoneDistance, weaponName, ammoText, magText, reserveText, hpText, armorText, bannerText, warningText;
        RectTransform hpFill, armorFill, zonePanel, feedPanel, damagePivot; Image hitA, hitB, damageBar;
        readonly List<Text> compassLabels = new List<Text>(); RectTransform compassStrip;
        readonly List<Text> feedLines = new List<Text>(); readonly List<(string text, float time)> feed = new List<(string, float)>();
        // drop
        Text dropTitle, dropKey, dropAction, altText; RectTransform altFill; GameObject dropPrompt;
        // eliminated / spectate / results
        Text elPlace, elBy, elKills, elDamage, elTime, specText, resTitle, resPlace, resKills, resDamage, resTime, resNext, resLine, resHeads, resPoints;
        float resultsShownAt = -1f; int lastTick = -1;
        // winner's card on the results screen (2026-11-07): the cat's own pass-card art, Resources/UI/Victory/card_<id>
        Image winCard, winGlow; Text winCaption; RectTransform winRoot; string winCardFor;
        NetworkPlayer cachedWinner; float nextWinnerSearch;
        bool spectating;
        // pause
        Slider sensSlider, volSlider; bool settingsFromMenu; GameObject pauseResume, pauseLeave;

        Health subscribedHealth;
        RectTransform vitalsPanel;
        readonly List<GameObject> artHudReplaces = new List<GameObject>();   // alive/kills, compass, weapon: hidden while GameHud runs
        RectTransform testPanel; int builtTest = -1; readonly List<(string id, Image bg, Text label)> testButtons = new List<(string id, Image bg, Text label)>();
        int lastCountdown = -1; MatchState lastState = MatchState.Waiting;   // countdown beeps, GO horn
        readonly List<RectTransform> crossBars = new List<RectTransform>();   // up, down, left, right
        Image vignette; float hitFlash;   // red screen edges when hit / low health
        float damageTime = -10f;

        // ------------------------------------------------------------------ setup
        void Awake()
        {
            instance = this; Active = true;
            bodyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleFont = Resources.Load<Font>("Fonts/Title") ?? bodyFont;
            numFont = Resources.Load<Font>("Fonts/Numbers") ?? bodyFont;
            round = MakeRounded(64, 18);

            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            root = (RectTransform)transform;

            if (FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            BuildVignette();
            BuildMenu(); BuildServerPanel(); BuildMatchmaking(); BuildCountdown(); BuildHud(); BuildDrop();
            BuildEliminated(); BuildResults(); BuildPause();
            MatchManager.KillFeed += OnKillFeed;
        }

        void OnDestroy()
        {
            MatchManager.KillFeed -= OnKillFeed;
            if (subscribedHealth != null) subscribedHealth.Damaged -= OnLocalDamaged;
            if (instance == this) { instance = null; Active = false; }
        }

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool connected = nm != null && (nm.IsClient || nm.IsServer);
            bool dedicated = nm != null && nm.IsServer && !nm.IsClient;
            var match = MatchManager.Instance;
            var localObj = nm != null && nm.IsClient && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
            var me = localObj != null ? localObj.GetComponent<NetworkPlayer>() : null;
            var state = match != null ? match.State : MatchState.Waiting;
            bool inGame = connected && !dedicated && me != null && match != null;
            bool dead = me != null && me.Health.IsDead;
            bool playing = inGame && state == MatchState.Playing;
            var dropComp = me != null ? me.GetComponent<PlayerDrop>() : null;
            var dropState = dropComp != null ? dropComp.State : DropState.Ground;

            if (!playing || !dead) spectating = false;
            bool showElim = playing && dead && !spectating;
            bool showResults = inGame && state == MatchState.Ended && !VictorySequence.Running;   // the winner's moment plays first
            if (!inGame || showElim || showResults) pauseOpen = false;
            else if (GameInput.PausePressed)
            {
                pauseOpen = !pauseOpen;
                if (!GameInput.TouchMode)
                {
                    Cursor.lockState = pauseOpen ? CursorLockMode.None : CursorLockMode.Locked;
                    Cursor.visible = pauseOpen;
                }
            }
            bool showPause = (inGame && pauseOpen) || (!connected && settingsFromMenu);

            menu.SetActive(!connected && !settingsFromMenu && !LobbyUI.Active);   // the art lobby (LobbyUI) replaces this placeholder menu
            serverPanel.SetActive(dedicated);
            matchmaking.SetActive(inGame && state == MatchState.Waiting);
            countdown.SetActive(inGame && state == MatchState.Countdown);
            hud.SetActive(inGame && (state == MatchState.Waiting || state == MatchState.Playing) && !dead);
            drop.SetActive(inGame && (state == MatchState.Countdown || state == MatchState.Playing) && !dead && dropState != DropState.Ground);
            eliminated.SetActive(showElim);
            spectateBar.SetActive(playing && dead && spectating);
            results.SetActive(showResults);
            pause.SetActive(showPause);
            pauseResume.SetActive(inGame); pauseLeave.SetActive(inGame);

            needsCursor = !connected || showElim || showResults || showPause;
            if ((showElim || showResults || !connected) && Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }

            if (!connected) UpdateMenu();
            if (matchmaking.activeSelf) UpdateMatchmaking(match);
            if (countdown.activeSelf)
            {
                int n = Mathf.CeilToInt(match.Timer);
                cdNumber.text = n.ToString();
                if (n != lastCountdown && n > 0 && n <= 5) GameAudio.Play2D("ui_beep", 0.6f);
                lastCountdown = n;
            }
            if (inGame && state == MatchState.Playing && lastState == MatchState.Countdown) GameAudio.Play2D("ui_go", 0.6f);
            lastState = state;
            if (hud.activeSelf) UpdateHud(me, match);
            if (drop.activeSelf) UpdateDrop(match, dropComp, dropState, state);
            if (showElim || spectateBar.activeSelf) UpdateEliminated(me, match);
            if (showResults) UpdateResults(me, match); else resultsShownAt = -1f;

            // red vignette: a flash on every hit (stronger for big hits), a slow pulse below 30% health
            hitFlash = Mathf.MoveTowards(hitFlash, 0f, Time.deltaTime * 1.6f);
            float vig = 0f;
            if (hud.activeSelf && me != null)
            {
                float hpRatio = me.Health.Max > 0f ? me.Health.Current / me.Health.Max : 1f;
                if (hpRatio < 0.3f) vig = Mathf.Lerp(0.45f, 0.2f, hpRatio / 0.3f) * (0.75f + 0.25f * Mathf.Sin(Time.time * 4f));
                vig = Mathf.Max(vig, hitFlash);
            }
            var vc = vignette.color; vc.a = Mathf.Clamp01(vig); vignette.color = vc;
            vignette.enabled = vc.a > 0.005f;

            if (me != null && me.Health != subscribedHealth)
            {
                if (subscribedHealth != null) subscribedHealth.Damaged -= OnLocalDamaged;
                subscribedHealth = me.Health;
                subscribedHealth.Damaged += OnLocalDamaged;
            }
        }

        void UpdateMenu()
        {
            var nb = NetworkBootstrap.Instance;
            if (nb == null) return;
            menuStatus.text = nb.Status;
            if (builtCards != (nb.MockCards ? nb.Cards.Count : WalletSession.Version)) BuildCardButtons(nb);
            if (testPanel != null)
            {
                if (builtTest != nb.Characters.Count) BuildTestButtons(nb);
                foreach (var t in testButtons)
                {
                    bool on = t.id == nb.TestCharacter;
                    t.bg.color = on ? Accent : CardC; t.label.color = on ? Ink : Sand;
                }
            }
            foreach (var (token, bg, label) in cardButtons)
            {
                bool on = token == nb.SelectedCard;
                bg.color = on ? Sand : CardC;
                label.color = on ? Ink : Sand;
            }
            menuCatLine.text = !string.IsNullOrEmpty(nb.TestCharacter) ? nb.TestCharacter.ToUpperInvariant() + " · test pick (no card)"
                             : nb.TryGetSelected(out var sel) && sel.Playable ? "Card #" + sel.tokenId + " · " + sel.characterId.ToUpperInvariant()
                             : "ARMY · Original cat";
        }

        void UpdateMatchmaking(MatchManager match)
        {
            int s = Mathf.FloorToInt(match.Timer);
            mmTimer.text = (s / 60) + ":" + (s % 60).ToString("00");
            mmPlayers.text = "Players " + match.HumanCount + " / " + match.MatchSize;
            startNow.SetActive(match.IsServer);
        }

        void UpdateHud(NetworkPlayer me, MatchManager match)
        {
            if (vitalsPanel.gameObject.activeSelf == VitalsHud.Active) vitalsPanel.gameObject.SetActive(!VitalsHud.Active);
            foreach (var part in artHudReplaces)
                if (part.activeSelf == GameHud.Active) part.SetActive(!GameHud.Active);
            var h = me.Health;
            float hp = h.Max > 0f ? h.Current / h.Max : 0f;
            hpFill.anchorMax = new Vector2(Mathf.Clamp01(hp), 1f);
            armorFill.anchorMax = new Vector2(Mathf.Clamp01(h.Armor / Health.MaxArmor), 1f);
            hpText.text = Mathf.CeilToInt(h.Current).ToString();
            armorText.text = Mathf.CeilToInt(h.Armor).ToString();

            var shooter = me.GetComponent<PlayerShooter>();
            if (shooter != null)
            {
                weaponName.text = shooter.Weapon.name.ToUpperInvariant();
                ammoText.text = shooter.IsReloading ? "--" : shooter.Ammo.ToString();
                magText.text = "/ " + shooter.MagazineSize;
                reserveText.text = shooter.Reserve.ToString();
                // hit marker, damage numbers and the kill banner live in CombatFeedback (server-confirmed hits)
            }

            // crosshair opens up with every shot (CombatFeedback.Bloom) and closes again
            float gap = 10f + CombatFeedback.Bloom * 16f;
            bool scoped = ScopeOverlay.Visible;   // the scope draws its own reticle
            for (int i = 0; i < crossBars.Count; i++)
            {
                crossBars[i].anchoredPosition = i == 0 ? new Vector2(0, gap) : i == 1 ? new Vector2(0, -gap) : i == 2 ? new Vector2(-gap, 0) : new Vector2(gap, 0);
                if (crossBars[i].gameObject.activeSelf == scoped) crossBars[i].gameObject.SetActive(!scoped);
            }

            bool inMatch = match.State == MatchState.Playing;
            aliveText.text = inMatch ? match.AliveCount.ToString() : match.HumanCount.ToString();
            killsText.text = me.Kills.ToString();

            var zone = match.Zone;
            bool zoneOn = zone != null && zone.Active;
            zonePanel.gameObject.SetActive(zoneOn);
            float minimapPx = GameHud.Active ? GameHud.MinimapBottomPx : 14f + 190f + 22f;   // the round art minimap, else the IMGUI one
            float minimapBottom = minimapPx / Mathf.Max(canvas.scaleFactor, 0.01f);
            zonePanel.anchoredPosition = new Vector2(-24f, -minimapBottom);
            feedPanel.anchoredPosition = new Vector2(-24f, -minimapBottom - (zoneOn ? 70f : 0f));
            if (zoneOn)
            {
                zoneTitle.text = zone.Shrinking ? "ZONE CLOSING" : "ZONE " + zone.PhaseNumber + " SHRINKS";
                int t = Mathf.CeilToInt(zone.SecondsLeft);
                zoneTime.text = (t / 60) + ":" + (t % 60).ToString("00");
                var flat = me.transform.position - zone.NextCenter; flat.y = 0f;
                float outside = flat.magnitude - zone.NextRadius;
                zoneDistance.text = outside > 0f ? "Safe zone " + Mathf.CeilToInt(outside) + " m" : "Inside the next zone";
            }
            warningText.gameObject.SetActive(zoneOn && zone.IsOutside(me.transform.position));

            bannerText.gameObject.SetActive(Time.time < me.BannerUntil);
            if (bannerText.gameObject.activeSelf) bannerText.text = me.Banner;

            // kill feed
            for (int i = feed.Count - 1; i >= 0; i--) if (Time.time - feed[i].time > 6f) feed.RemoveAt(i);
            for (int i = 0; i < feedLines.Count; i++)
            {
                bool on = i < feed.Count;
                feedLines[i].transform.parent.gameObject.SetActive(on);
                if (on) feedLines[i].text = feed[feed.Count - 1 - i].text;
            }

            // compass
            var cam = Camera.main;
            float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            int start = Mathf.FloorToInt((yaw - 60f) / 15f) * 15;
            for (int i = 0; i < compassLabels.Count; i++)
            {
                int a = start + i * 15;
                float x = Mathf.DeltaAngle(yaw, a) * 3.2f;
                var label = compassLabels[i];
                bool visible = Mathf.Abs(x) < 190f;
                label.gameObject.SetActive(visible);
                if (!visible) continue;
                ((RectTransform)label.transform).anchoredPosition = new Vector2(x, 0f);
                int n = ((a % 360) + 360) % 360;
                label.text = n == 0 ? "N" : n == 90 ? "E" : n == 180 ? "S" : n == 270 ? "W" : n == 45 ? "NE" : n == 135 ? "SE" : n == 225 ? "SW" : n == 315 ? "NW" : n.ToString("000");
                bool major = n % 45 == 0;
                label.fontSize = major ? 22 : 16;
                label.color = major ? Sand : Muted;
            }

            // damage direction
            float dt = Time.time - damageTime;
            var dc = Danger; dc.a = Mathf.Clamp01(1f - dt / 1.2f) * 0.85f;
            damageBar.color = dc;
        }

        void UpdateDrop(MatchManager match, PlayerDrop dropComp, DropState ds, MatchState state)
        {
            altText.text = Mathf.Max(0, Mathf.RoundToInt(dropComp.Altitude)) + " m";
            altFill.anchorMax = new Vector2(1f, Mathf.Clamp01(dropComp.Altitude / MatchManager.ShipAltitude));
            string title; string key = null; string action = null;
            if (ds == DropState.OnShip)
            {
                if (state == MatchState.Countdown) title = "BOARDING";
                else if (match.CanJump)
                {
                    int t = Mathf.CeilToInt(Mathf.Max(0f, match.JumpTimeLeft));
                    title = "JUMP ZONE OPEN · " + (t / 60) + ":" + (t % 60).ToString("00");
                    key = "SPACE"; action = "JUMP";
                }
                else if (match.TimeUntilJumpZone > 0f) title = "APPROACHING THE ISLAND · " + Mathf.CeilToInt(match.TimeUntilJumpZone) + "s";
                else title = "LEAVING THE ISLAND";
            }
            else if (ds == DropState.Freefall) { title = "FREE FALL"; key = "SPACE"; action = "OPEN PARACHUTE"; }
            else { title = "PARACHUTE OPEN"; key = "W A S D"; action = "STEER"; }
            dropTitle.text = title;
            dropPrompt.SetActive(key != null && !GameInput.TouchMode);   // PC: SPACE / JUMP prompt back (user 2026-11-01); phones use the big JUMP button
            if (key != null) { dropKey.text = key; dropAction.text = action; }
        }

        void UpdateEliminated(NetworkPlayer me, MatchManager match)
        {
            if (match.IsZombies)
            {
                elPlace.text = "WAVE " + Mathf.Max(1, match.Waves.Wave);
                elBy.text = "by the zombies";
            }
            else elPlace.text = me.Placement > 0 ? "#" + me.Placement : "#-";
            if (!match.IsZombies) elBy.text = me.KilledBy > 0 ? "by " + NameOf(me.KilledBy, me) + (me.KillerWeapon >= 0 ? " · " + Weapons.Get(me.KillerWeapon).name : "")
                      : me.KilledBy == 0 ? "by the zone" : "";
            if (LeaderboardReporter.LastText.Length > 0) elBy.text += "\n" + LeaderboardReporter.LastText;
            elKills.text = me.Kills.ToString();
            elDamage.text = me.Damage.ToString();
            elTime.text = Clock(me.Survived);
            var spect = me.GetComponent<SpectateController>();
            var target = spect != null ? spect.Target : null;
            specText.text = target != null ? "Spectating " + NameOf(target.Number, me) + " · " + match.AliveCount + " alive   (Tab / arrows to switch, Esc for menu)"
                                           : "Waiting for the camera…";
        }

        void UpdateResults(NetworkPlayer me, MatchManager match)
        {
            bool won = me.Placement == 1;
            resTitle.text = match.IsZombies ? "GAME OVER" : won ? "LAST CAT STANDING!" : "MATCH OVER";
            resTitle.color = won ? GoldC : Sand;
            resPlace.text = match.IsZombies ? "WAVE " + Mathf.Max(1, match.Waves.Wave)
                          : me.Placement > 0 ? "#" + me.Placement + " of " + match.MatchSize : "";
            resLine.text = match.ResultText;
            // numbers count up when the card appears (2026-11-07)
            if (resultsShownAt < 0f) { resultsShownAt = Time.unscaledTime; lastTick = -1; }
            float k = Mathf.Clamp01((Time.unscaledTime - resultsShownAt - 0.25f) / 1.3f);
            k = 1f - (1f - k) * (1f - k) * (1f - k);
            int tick = Mathf.FloorToInt(k * 14f);
            if (tick != lastTick && k < 1f && k > 0f) { lastTick = tick; GameAudio.Play2D("ui_beep", 0.12f, 1.6f + k * 0.6f); }
            resKills.text = Mathf.RoundToInt(me.Kills * k).ToString();
            resDamage.text = Mathf.RoundToInt(me.Damage * k).ToString();
            resTime.text = Clock(me.Survived * k);
            resHeads.text = Mathf.RoundToInt(me.Headshots * k).ToString();
            UpdateWinnerCard(me, match, Time.unscaledTime - resultsShownAt);
            resPoints.text = LeaderboardReporter.LastText;   // "+130 PTS · WEEK RANK #3" pops in after the count-up
            float pop = k >= 1f ? Mathf.Max(0f, 1f - (Time.unscaledTime - resultsShownAt - 1.55f) * 3f) : 0f;
            resPoints.transform.localScale = Vector3.one * (k >= 1f ? 1f + 0.25f * pop : 0.001f);
            resNext.text = "Next match in " + Mathf.CeilToInt(match.Timer) + "s";
        }

        static string Clock(float seconds) { int s = Mathf.Max(0, Mathf.FloorToInt(seconds)); return (s / 60) + ":" + (s % 60).ToString("00"); }

        static string NameOf(int number, NetworkPlayer me) => me != null && number == me.Number ? "You" : number == 0 ? "The zone"
            : PlayerNames.Of(number, MatchManager.Instance != null ? MatchManager.Instance.NameSeed : 1);

        void OnKillFeed(int killer, int victim, int weapon)
        {
            var nm = NetworkManager.Singleton;
            var me = nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>() : null;
            string text = killer == 0 ? NameOf(victim, me) + " was caught by the zone"
                                      : NameOf(killer, me) + "  eliminated  " + NameOf(victim, me) + (weapon >= 0 ? "  · " + Weapons.Get(weapon).name : "");
            feed.Add((text, Time.time));
            if (feed.Count > 8) feed.RemoveAt(0);
        }

        void OnLocalDamaged(float amount, GameObject attacker)
        {
            damageTime = Time.time;
            hitFlash = Mathf.Min(0.9f, Mathf.Max(hitFlash, 0.35f + amount / 45f));
            var cam = Camera.main;
            float angle = 0f;
            if (attacker != null && cam != null)
            {
                var to = attacker.transform.position - cam.transform.position; to.y = 0f;
                var fwd = cam.transform.forward; fwd.y = 0f;
                angle = Vector3.SignedAngle(fwd, to, Vector3.up);
            }
            damagePivot.localRotation = Quaternion.Euler(0f, 0f, -angle);
        }

        // ------------------------------------------------------------------ screens
        void BuildVignette()
        {
            const int n = 256;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.414f;          // 0 centre .. 1 corner
                    float a = Mathf.SmoothStep(0.32f, 0.95f, r);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply();
            vignette = Node("HitVignette", root).gameObject.AddComponent<Image>();
            vignette.sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
            vignette.color = new Color(0.75f, 0.02f, 0.02f, 0f);
            vignette.raycastTarget = false;
            Fill(vignette.rectTransform);
        }

        void BuildMenu()
        {
            menu = Node("Menu", root).gameObject; Fill((RectTransform)menu.transform);
            var shade = Box(menu.transform, "Shade", Shade, false); Fill(shade.rectTransform);

            var title = Label(menu.transform, "CAT BATTLEGROUND", 46, Accent, TextAnchor.UpperLeft, titleFont, true);
            Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -24), new Vector2(600, 56));
            var sub = Label(menu.transform, "LAST CAT STANDING", 16, Tan, TextAnchor.UpperLeft, numFont, false);
            Place(sub.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(34, -80), new Vector2(400, 24));

            var wallet = Btn(menu.transform, "CONNECT WALLET (SOON)", PanelC, Muted, 16, null, numFont);
            Place((RectTransform)wallet.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-32, -28), new Vector2(250, 48));
            wallet.interactable = false;
            var name = Panel(menu.transform, "Player");
            Place(name, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-298, -28), new Vector2(200, 48));
            var nameText = Label(name, "GUEST CAT", 18, Sand, TextAnchor.MiddleCenter, numFont, false); Fill(nameText.rectTransform);

            // your cat / pass cards (left)
            var catPanel = Panel(menu.transform, "YourCat");
            Place(catPanel, new Vector2(0, 0), new Vector2(0, 0), new Vector2(32, 32), new Vector2(340, 400));
            var catHead = Label(catPanel, "YOUR CAT", 14, Muted, TextAnchor.UpperLeft, numFont, false);
            Place(catHead.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -14), new Vector2(300, 20));
            menuCatLine = Label(catPanel, "", 20, Sand, TextAnchor.UpperLeft, titleFont, false);
            Place(menuCatLine.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -38), new Vector2(310, 52));
            menuCatLine.horizontalOverflow = HorizontalWrapMode.Wrap;
            var cardHead = Label(catPanel, "PASS CARD (the card draws its cat on first use)", 13, Muted, TextAnchor.UpperLeft, bodyFont, false);
            Place(cardHead.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -96), new Vector2(310, 20));
            cardList = Node("Cards", catPanel);
            Place(cardList, new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -122), new Vector2(312, 260));

            // (TEST CHARACTER picker removed 2026-11-06: cats come from the pass cards only; testPanel stays null)

            // join / dev row (bottom centre)
            var joinPanel = Panel(menu.transform, "Join");
            Place(joinPanel, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-40, 32), new Vector2(420, 120));
            var ipLabel = Label(joinPanel, "SERVER ADDRESS", 13, Muted, TextAnchor.UpperLeft, numFont, false);
            Place(ipLabel.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -12), new Vector2(200, 18));
            ipField = Input(joinPanel, NetworkBootstrap.Instance != null ? NetworkBootstrap.Instance.Address : "127.0.0.1");
            Place((RectTransform)ipField.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -34), new Vector2(250, 40));
            ipField.onEndEdit.AddListener(v => { if (NetworkBootstrap.Instance != null) NetworkBootstrap.Instance.Address = v; });
            var join = Btn(joinPanel, "JOIN", Sand, Ink, 18, () => { var nb = NetworkBootstrap.Instance; if (nb == null) return; nb.Address = ipField.text; nb.UiJoin(); }, numFont);
            Place((RectTransform)join.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(276, -34), new Vector2(128, 40));
            if (NetworkBootstrap.CanListenHere)
            {
                var server = Btn(joinPanel, "DEDICATED SERVER", CardC, Sand, 14, () => NetworkBootstrap.Instance?.UiServer(), numFont);
                Place((RectTransform)server.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -82), new Vector2(180, 30));
            }
            menuStatus = Label(joinPanel, "", 13, Tan, TextAnchor.MiddleLeft, bodyFont, false);
            Place(menuStatus.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(206, -82), new Vector2(200, 30));
            menuStatus.horizontalOverflow = HorizontalWrapMode.Wrap;

            // mode + play (bottom right)
            var modes = Panel(menu.transform, "Modes");
            Place(modes, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-32, 136), new Vector2(360, 56));
            string[] names = { "SOLO", "DUO", "SQUAD" };
            for (int i = 0; i < 3; i++)
            {
                var m = Btn(modes, i == 0 ? "SOLO" : names[i] + " (LOCKED)", i == 0 ? Sand : new Color(0, 0, 0, 0), i == 0 ? Ink : Locked, i == 0 ? 18 : 13, null, numFont);
                Place((RectTransform)m.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(6 + i * 118, 0), new Vector2(112, 44));
                m.interactable = i == 0;
            }
            var play = Btn(menu.transform, "PLAY", Accent, Ink, 44, () =>
            {
                var nb = NetworkBootstrap.Instance; if (nb == null) return;
                if (NetworkBootstrap.CanHostHere) nb.UiHost(); else { nb.Address = ipField.text; nb.UiJoin(); }
            }, titleFont);
            Place((RectTransform)play.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-32, 32), new Vector2(360, 92));
            Shadowed(play.gameObject, AccentDark, 6);
            var playSub = Label(play.transform, NetworkBootstrap.OfflineHere ? "SOLO · VS BOTS" : NetworkBootstrap.CanHostHere ? "SOLO · HOST + BOTS" : "SOLO · 10 PLAYERS", 14, Ink, TextAnchor.LowerCenter, numFont, false);
            Fill(playSub.rectTransform); playSub.rectTransform.offsetMin = new Vector2(0, 8);

            var settings = Btn(menu.transform, "SETTINGS", PanelC, Sand, 20, () => settingsFromMenu = true, titleFont);
            Place((RectTransform)settings.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-32, 204), new Vector2(170, 48));

            // game mode toggle (battle royale / zombie survival)
            string ModeLabel() => NetworkBootstrap.Instance != null && NetworkBootstrap.Instance.SelectedMode == MatchMode.Zombies ? "MODE: ZOMBIES" : "MODE: BATTLE ROYALE";
            var gameMode = Btn(menu.transform, ModeLabel(), PanelC, Sand, 16, null, numFont);
            Place((RectTransform)gameMode.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-212, 204), new Vector2(180, 48));
            gameMode.onClick.AddListener(() =>
            {
                var nb = NetworkBootstrap.Instance; if (nb == null) return;
                nb.SelectedMode = nb.SelectedMode == MatchMode.Zombies ? MatchMode.BattleRoyale : MatchMode.Zombies;
                gameMode.GetComponentInChildren<Text>().text = ModeLabel();
            });
        }

        void BuildTestButtons(NetworkBootstrap nb)
        {
            if (testPanel == null) return;
            foreach (var t in testButtons) if (t.bg != null) Destroy(t.bg.gameObject);
            testButtons.Clear();
            var options = new List<(string id, string label)> { ("", "CARD") };
            foreach (var e in nb.Characters) if (e != null && e.playerPrefab != null) options.Add((e.id, (string.IsNullOrEmpty(e.displayName) ? e.id : e.displayName).ToUpperInvariant()));
            for (int i = 0; i < options.Count && i < 15; i++)
            {
                var (id, label) = options[i];
                var b = Btn(testPanel, label, CardC, Sand, 12, () => nb.TestCharacter = id, numFont);
                Place((RectTransform)b.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12 + (i % 5) * 63, -34 - (i / 5) * 36), new Vector2(59, 30));
                testButtons.Add((id, b.GetComponent<Image>(), b.GetComponentInChildren<Text>()));
            }
            builtTest = nb.Characters.Count;
        }

        void BuildCardButtons(NetworkBootstrap nb)
        {
            foreach (Transform c in cardList) Destroy(c.gameObject);
            cardButtons.Clear();
            var options = new List<(string token, string label)> { ("", "ORIGINAL CAT (no card)") };
            foreach (var card in nb.Cards)
                options.Add((card.tokenId, "CARD #" + card.tokenId + "  ·  " + (card.Playable ? card.characterId.ToUpperInvariant() : card.tier.ToString().ToUpperInvariant() + " (SEALED)")));
            for (int i = 0; i < options.Count && i < 5; i++)
            {
                var (token, label) = options[i];
                bool playable = token == "";
                foreach (var c in nb.Cards) if (c.tokenId == token && c.Playable) playable = true;
                var b = Btn(cardList, label, CardC, Sand, 16, () => { if (playable) nb.SelectedCard = token; else WalletBridge.OpenUrl(ChainConfig.CardsPage); }, numFont);
                Place((RectTransform)b.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -i * 50), new Vector2(312, 44));
                cardButtons.Add((token, b.GetComponent<Image>(), b.GetComponentInChildren<Text>()));
            }
            builtCards = nb.MockCards ? nb.Cards.Count : WalletSession.Version;
        }

        void BuildServerPanel()
        {
            var p = Panel(root, "Server");
            serverPanel = p.gameObject;
            Place(p, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -24), new Vector2(320, 110));
            var t = Label(p, "DEDICATED SERVER RUNNING", 18, Sand, TextAnchor.UpperLeft, numFont, false);
            Place(t.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -14), new Vector2(300, 24));
            var stop = Btn(p, "STOP", Sand, Ink, 18, () => NetworkBootstrap.Instance?.UiDisconnect(), numFont);
            Place((RectTransform)stop.transform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(16, 14), new Vector2(120, 40));
        }

        void BuildMatchmaking()
        {
            var p = Panel(root, "Matchmaking");
            matchmaking = p.gameObject;
            Place(p, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -72), new Vector2(440, 150));
            var head = Label(p, "FINDING PLAYERS", 30, Accent, TextAnchor.UpperCenter, titleFont, true);
            Place(head.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -12), new Vector2(420, 40));
            mmTimer = Label(p, "0:00", 44, Sand, TextAnchor.UpperCenter, numFont, false);
            Place(mmTimer.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -52), new Vector2(200, 52));
            mmPlayers = Label(p, "", 15, Tan, TextAnchor.LowerCenter, bodyFont, false);
            Place(mmPlayers.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 12), new Vector2(420, 22));
            var cancel = Btn(p, "CANCEL", new Color(0, 0, 0, 0), Sand, 16, () => NetworkBootstrap.Instance?.UiDisconnect(), numFont);
            Place((RectTransform)cancel.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10, -10), new Vector2(96, 34));
            Outline(cancel.gameObject, Tan);
            var start = Btn(p, "START NOW", Accent, Ink, 14, () => MatchManager.Instance?.StartNow(), numFont);
            Place((RectTransform)start.transform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(10, 10), new Vector2(110, 30));
            startNow = start.gameObject;
            var tip = Label(p, "Esc frees the mouse", 12, Muted, TextAnchor.UpperLeft, bodyFont, false);
            Place(tip.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -12), new Vector2(140, 18));
        }

        void BuildCountdown()
        {
            countdown = Node("Countdown", root).gameObject; Fill((RectTransform)countdown.transform);
            var head = Label(countdown.transform, "BOARDING THE DROP SHIP", 28, Sand, TextAnchor.UpperCenter, titleFont, true);
            Place(head.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -28), new Vector2(800, 40));
            cdNumber = Label(countdown.transform, "3", 120, Accent, TextAnchor.MiddleCenter, titleFont, true);
            Place(cdNumber.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(300, 150));
        }

        void BuildHud()
        {
            hud = Node("HUD", root).gameObject; Fill((RectTransform)hud.transform);

            // top left: alive / kills
            var alive = Panel(hud.transform, "Alive");
            Place(alive, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -20), new Vector2(120, 40));
            aliveText = Label(alive, "10", 24, Sand, TextAnchor.MiddleLeft, numFont, false);
            Place(aliveText.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), new Vector2(40, 34));
            var al = Label(alive, "ALIVE", 14, Muted, TextAnchor.MiddleLeft, numFont, false);
            Place(al.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(56, 0), new Vector2(60, 30));
            var kills = Panel(hud.transform, "Kills");
            Place(kills, new Vector2(0, 1), new Vector2(0, 1), new Vector2(154, -20), new Vector2(120, 40));
            killsText = Label(kills, "0", 24, Accent, TextAnchor.MiddleLeft, numFont, false);
            Place(killsText.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), new Vector2(40, 34));
            var kl = Label(kills, "KILLS", 14, Muted, TextAnchor.MiddleLeft, numFont, false);
            Place(kl.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(56, 0), new Vector2(60, 30));

            // top centre: compass
            var compass = Box(hud.transform, "Compass", new Color(0.067f, 0.086f, 0.059f, 0.7f), true);
            Place(compass.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -18), new Vector2(400, 40));
            compass.gameObject.AddComponent<RectMask2D>();
            compassStrip = Node("Strip", compass.transform);
            Place(compassStrip, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10, 40));
            for (int i = 0; i < 10; i++)
            {
                var t = Label(compassStrip, "", 16, Muted, TextAnchor.MiddleCenter, numFont, false);
                Place(t.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(60, 36));
                compassLabels.Add(t);
            }
            var tick = Box(hud.transform, "Tick", Accent, false);
            Place(tick.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -58), new Vector2(4, 10));
            artHudReplaces.Add(alive.gameObject); artHudReplaces.Add(kills.gameObject);
            artHudReplaces.Add(compass.gameObject); artHudReplaces.Add(tick.gameObject);

            bannerText = Label(hud.transform, "", 22, Sand, TextAnchor.UpperCenter, titleFont, true);
            Place(bannerText.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -80), new Vector2(900, 32));

            // right: zone + kill feed (positioned under the IMGUI minimap every frame)
            zonePanel = Panel(hud.transform, "Zone");
            Place(zonePanel, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -240), new Vector2(210, 60));
            zoneTitle = Label(zonePanel, "ZONE", 17, Sand, TextAnchor.UpperLeft, numFont, false);
            Place(zoneTitle.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -8), new Vector2(140, 22));
            zoneTime = Label(zonePanel, "0:00", 17, Accent, TextAnchor.UpperRight, numFont, false);
            Place(zoneTime.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-12, -8), new Vector2(60, 22));
            zoneDistance = Label(zonePanel, "", 13, Muted, TextAnchor.UpperLeft, bodyFont, false);
            Place(zoneDistance.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -32), new Vector2(190, 20));
            feedPanel = Node("Feed", hud.transform);
            Place(feedPanel, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-24, -310), new Vector2(420, 140));
            for (int i = 0; i < 4; i++)
            {
                var row = Box(feedPanel, "Line" + i, new Color(0.067f, 0.086f, 0.059f, 0.72f), true);
                Place(row.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0, -i * 32), new Vector2(420, 28));
                var t = Label(row.transform, "", 14, Sand, TextAnchor.MiddleRight, bodyFont, false);
                Fill(t.rectTransform); t.rectTransform.offsetMax = new Vector2(-10, 0);
                feedLines.Add(t);
                row.gameObject.SetActive(false);
            }

            // centre: crosshair, hit marker, damage direction, zone warning
            for (int i = 0; i < 4; i++)
            {
                var bar = Box(hud.transform, "Cross" + i, Color.white, false);
                crossBars.Add(bar.rectTransform);
                bool vertical = i < 2;
                Place(bar.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    vertical ? new Vector2(0, i == 0 ? 10 : -10) : new Vector2(i == 2 ? -10 : 10, 0), vertical ? new Vector2(2, 8) : new Vector2(8, 2));
            }
            hitA = Box(hud.transform, "HitA", new Color(0, 0, 0, 0), false);
            Place(hitA.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26, 3));
            hitA.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            hitB = Box(hud.transform, "HitB", new Color(0, 0, 0, 0), false);
            Place(hitB.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26, 3));
            hitB.rectTransform.localRotation = Quaternion.Euler(0, 0, -45);
            damagePivot = Node("DamagePivot", hud.transform);
            Place(damagePivot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            damageBar = Box(damagePivot, "DamageBar", new Color(0, 0, 0, 0), true);
            Place(damageBar.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 150), new Vector2(170, 14));
            warningText = Label(hud.transform, "OUTSIDE THE ZONE - MOVE INSIDE!", 26, new Color(1f, 0.42f, 0.35f), TextAnchor.MiddleCenter, titleFont, true);
            Place(warningText.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 170), new Vector2(800, 40));

            // bottom left: armor + health
            var vitals = vitalsPanel = Panel(hud.transform, "Vitals");   // hidden while the art HUD (VitalsHud) runs
            Place(vitals, new Vector2(0, 0), new Vector2(0, 0), new Vector2(24, 24), new Vector2(360, 86));
            armorFill = Bar(vitals, new Vector2(48, -18), new Vector2(250, 12), ArmorC);
            var armorIcon = Label(vitals, "ARM", 13, ArmorC, TextAnchor.MiddleLeft, numFont, false);
            Place(armorIcon.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -12), new Vector2(40, 24));
            armorText = Label(vitals, "0", 20, Sand, TextAnchor.MiddleRight, numFont, false);
            Place(armorText.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-12, -12), new Vector2(50, 24));
            hpFill = Bar(vitals, new Vector2(48, -50), new Vector2(250, 22), HpC);
            var hpIcon = Label(vitals, "HP", 15, HpC, TextAnchor.MiddleLeft, numFont, false);
            Place(hpIcon.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -48), new Vector2(40, 26));
            hpText = Label(vitals, "100", 26, Sand, TextAnchor.MiddleRight, numFont, false);
            Place(hpText.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-12, -46), new Vector2(54, 30));

            // bottom right: weapon
            var weapon = Panel(hud.transform, "Weapon");
            Place(weapon, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 24), new Vector2(300, 98));
            artHudReplaces.Add(weapon.gameObject);
            weaponName = Label(weapon, "WEAPON", 17, Tan, TextAnchor.UpperLeft, numFont, false);
            Place(weaponName.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, -10), new Vector2(200, 22));
            var reloadHint = Label(weapon, "R reload", 12, Muted, TextAnchor.UpperRight, bodyFont, false);
            Place(reloadHint.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-14, -12), new Vector2(80, 18));
            ammoText = Label(weapon, "30", 52, Sand, TextAnchor.LowerLeft, numFont, false);
            Place(ammoText.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(16, 6), new Vector2(90, 60));
            magText = Label(weapon, "/ 30", 24, Muted, TextAnchor.LowerLeft, numFont, false);
            Place(magText.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(104, 12), new Vector2(80, 32));
            reserveText = Label(weapon, "90", 24, Accent, TextAnchor.LowerRight, numFont, false);
            Place(reserveText.rectTransform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-16, 12), new Vector2(80, 32));
        }

        void BuildDrop()
        {
            drop = Node("Drop", root).gameObject; Fill((RectTransform)drop.transform);
            var top = Panel(drop.transform, "Title");
            Place(top, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -70), new Vector2(480, 40));
            dropTitle = Label(top, "", 20, Sand, TextAnchor.MiddleCenter, numFont, false); Fill(dropTitle.rectTransform);

            dropPrompt = Panel(drop.transform, "Prompt").gameObject;
            var pr = (RectTransform)dropPrompt.transform;
            Place(pr, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 150), new Vector2(420, 64));
            Outline(dropPrompt, Accent);
            var key = Box(pr, "Key", Sand, true);
            Place(key.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(110, 42));
            dropKey = Label(key.transform, "SPACE", 18, Ink, TextAnchor.MiddleCenter, numFont, false); Fill(dropKey.rectTransform);
            dropAction = Label(pr, "JUMP", 26, Sand, TextAnchor.MiddleLeft, titleFont, false);
            Place(dropAction.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(140, 0), new Vector2(270, 40));

            var meter = Node("Altimeter", drop.transform);
            Place(meter, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-40, 0), new Vector2(90, 320));
            var head = Label(meter, "ALTITUDE", 14, Sand, TextAnchor.UpperCenter, numFont, true);
            Place(head.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(90, 20));
            var track = Box(meter, "Track", new Color(0.067f, 0.086f, 0.059f, 0.7f), true);
            Place(track.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -26), new Vector2(22, 240));
            var fill = Box(track.transform, "Fill", Tan, true);
            altFill = fill.rectTransform; altFill.anchorMin = Vector2.zero; altFill.anchorMax = new Vector2(1, 0.5f); altFill.offsetMin = altFill.offsetMax = Vector2.zero;
            var altBox = Panel(meter, "Value");
            Place(altBox, new Vector2(0.5f, 0), new Vector2(0.5f, 0), Vector2.zero, new Vector2(90, 40));
            altText = Label(altBox, "0 m", 22, Sand, TextAnchor.MiddleCenter, numFont, false); Fill(altText.rectTransform);
        }

        void BuildEliminated()
        {
            eliminated = Node("Eliminated", root).gameObject; Fill((RectTransform)eliminated.transform);
            var tint = Box(eliminated.transform, "Tint", new Color(0.16f, 0.05f, 0.04f, 0.72f), false); Fill(tint.rectTransform);
            var placed = Label(eliminated.transform, "YOU PLACED", 18, Tan, TextAnchor.UpperCenter, numFont, false);
            Place(placed.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -70), new Vector2(400, 24));
            elPlace = Label(eliminated.transform, "#-", 110, Accent, TextAnchor.UpperCenter, titleFont, true);
            Place(elPlace.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -96), new Vector2(400, 120));
            var title = Label(eliminated.transform, "ELIMINATED", 42, Sand, TextAnchor.UpperCenter, titleFont, true);
            Place(title.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -222), new Vector2(600, 50));
            elBy = Label(eliminated.transform, "", 20, Tan, TextAnchor.UpperCenter, bodyFont, false);
            Place(elBy.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -278), new Vector2(800, 28));
            elKills = Stat(eliminated.transform, "KILLS", new Vector2(-204, -320));
            elDamage = Stat(eliminated.transform, "DAMAGE", new Vector2(0, -320));
            elTime = Stat(eliminated.transform, "SURVIVED", new Vector2(204, -320));
            var spec = Btn(eliminated.transform, "SPECTATE", Accent, Ink, 26, () =>
            {
                spectating = true; GameInput.LockCursor(true);
            }, titleFont);
            Place((RectTransform)spec.transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-128, -450), new Vector2(240, 60));
            Shadowed(spec.gameObject, AccentDark, 5);
            var lobby = Btn(eliminated.transform, "BACK TO LOBBY", new Color(0, 0, 0, 0), Sand, 22, () => NetworkBootstrap.Instance?.UiDisconnect(), titleFont);
            Place((RectTransform)lobby.transform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(128, -450), new Vector2(240, 60));
            Outline(lobby.gameObject, Tan);

            var bar = Panel(root, "SpectateBar");
            spectateBar = bar.gameObject;
            Place(bar, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 28), new Vector2(640, 52));
            specText = Label(bar, "", 17, Sand, TextAnchor.MiddleCenter, bodyFont, false); Fill(specText.rectTransform);
        }

        void BuildResults()
        {
            results = Node("Results", root).gameObject; Fill((RectTransform)results.transform);
            // the card covers the left 64%: the winner (VictorySequence keeps circling it) stays visible on the right
            var bg = Box(results.transform, "Bg", new Color(0.106f, 0.137f, 0.086f, 0.94f), false); Fill(bg.rectTransform);
            bg.rectTransform.anchorMax = new Vector2(0.64f, 1f);
            var fade = Box(results.transform, "BgFade", new Color(0.106f, 0.137f, 0.086f, 0.35f), false); Fill(fade.rectTransform);
            fade.rectTransform.anchorMin = new Vector2(0.64f, 0f);
            var mode = Label(results.transform, "SOLO", 18, Tan, TextAnchor.UpperLeft, numFont, false);
            Place(mode.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(140, -80), new Vector2(500, 24));
            resTitle = Label(results.transform, "LAST CAT STANDING!", 64, GoldC, TextAnchor.UpperLeft, titleFont, true);
            Place(resTitle.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(140, -104), new Vector2(900, 96));
            resPlace = Label(results.transform, "", 32, Sand, TextAnchor.UpperLeft, titleFont, false);
            Place(resPlace.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(140, -190), new Vector2(500, 40));
            resLine = Label(results.transform, "", 18, Tan, TextAnchor.UpperLeft, bodyFont, false);
            Place(resLine.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(140, -238), new Vector2(700, 24));
            resKills = Stat(results.transform, "KILLS", new Vector2(140 + 85, -290), true);
            resDamage = Stat(results.transform, "DAMAGE", new Vector2(320 + 85, -290), true);
            resTime = Stat(results.transform, "SURVIVED", new Vector2(500 + 85, -290), true);
            resHeads = Stat(results.transform, "HEADSHOTS", new Vector2(680 + 85, -290), true);
            resPoints = Label(results.transform, "", 30, GoldC, TextAnchor.MiddleLeft, titleFont, true);   // leaderboard points
            Place(resPoints.rectTransform, new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(140, -412), new Vector2(760, 44));
            resNext = Label(results.transform, "", 18, Tan, TextAnchor.UpperLeft, bodyFont, false);
            Place(resNext.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(140, -446), new Vector2(500, 24));
            var lobby = Btn(results.transform, "LEAVE TO LOBBY", new Color(0, 0, 0, 0), Sand, 22, () => NetworkBootstrap.Instance?.UiDisconnect(), titleFont);
            Place((RectTransform)lobby.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(140, -484), new Vector2(260, 60));
            Outline(lobby.gameObject, Tan);

            // the winner's card, right side (over the faded world)
            winRoot = Node("WinnerCard", results.transform);
            Place(winRoot, new Vector2(0.82f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 10), new Vector2(380, 580));
            winGlow = Box(winRoot, "Glow", new Color(1f, 0.82f, 0.3f, 0.55f), false);
            winGlow.sprite = GlowSprite(); winGlow.rectTransform.sizeDelta = new Vector2(760, 900);
            winGlow.rectTransform.anchorMin = winGlow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            winGlow.rectTransform.anchoredPosition = Vector2.zero;
            winCard = Box(winRoot, "Card", Color.white, false); Fill(winCard.rectTransform);
            winCard.preserveAspect = true;
            winCaption = Label(winRoot, "WINNER", 26, GoldC, TextAnchor.MiddleCenter, titleFont, true);
            Place(winCaption.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0, -6), new Vector2(520, 40));
        }

        static Sprite glowSprite;
        static Sprite GlowSprite()
        {
            if (glowSprite != null) return glowSprite;
            const int N = 128;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - N * 0.5f) / (N * 0.5f), dy = (y - N * 0.5f) / (N * 0.5f);
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)); a = a * a;
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            return glowSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
        }

        static Sprite CardSprite(string id)
        {
            var sp = Resources.Load<Sprite>("UI/Victory/card_" + id);
            if (sp != null) return sp;
            var t = Resources.Load<Texture2D>("UI/Victory/card_" + id);
            return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f)) : null;
        }

        void UpdateWinnerCard(NetworkPlayer me, MatchManager match, float shownFor)
        {
            NetworkPlayer winner = cachedWinner != null && cachedWinner.Placement == 1 ? cachedWinner : null;
            if (winner == null && !match.IsZombies && Time.unscaledTime >= nextWinnerSearch)
            {
                nextWinnerSearch = Time.unscaledTime + 0.5f;
                foreach (var p in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
                    if (p.Placement == 1) { winner = p; break; }
                cachedWinner = winner;
            }
            if (winner == null) { winRoot.gameObject.SetActive(false); return; }
            string id = winner.CharacterId;
            if (id != winCardFor)
            {
                winCardFor = id;
                var sp = CardSprite(id);
                winCard.sprite = sp;
                winCard.color = sp != null ? Color.white : new Color(1, 1, 1, 0.08f);
                var tierCol = winner.Tier == CharacterTier.Legendary ? new Color(1f, 0.8f, 0.25f, 0.6f)
                            : winner.Tier == CharacterTier.Rare ? new Color(0.65f, 0.45f, 1f, 0.55f)
                            : winner.Tier == CharacterTier.Common ? new Color(0.55f, 0.9f, 1f, 0.5f) : new Color(1f, 1f, 1f, 0.4f);
                winGlow.color = tierCol;
            }
            winRoot.gameObject.SetActive(true);
            winCaption.text = winner == me ? "YOUR CAT WINS!" : "WINNER · " + NameOf(winner.Number, me).ToUpperInvariant();
            // pops in after 0.2 s with an overshoot, then floats and tilts gently; the glow breathes
            float a = Mathf.Clamp01((shownFor - 0.2f) / 0.45f);
            float pop = a <= 0f ? 0f : 1f + 0.12f * Mathf.Sin(a * Mathf.PI) * (1f - a) * 2f;
            float scale = a <= 0f ? 0.001f : Mathf.Lerp(0.6f, 1f, a) * pop;
            winRoot.localScale = Vector3.one * scale;
            winRoot.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(shownFor * 1.1f) * 2.5f);
            winRoot.anchoredPosition = new Vector2(0f, 10f + Mathf.Sin(shownFor * 1.6f) * 8f);
            var gc = winGlow.color; gc.a = (0.4f + 0.2f * Mathf.Sin(shownFor * 2.4f)) * a; winGlow.color = gc;
        }

        void BuildPause()
        {
            pause = Node("Pause", root).gameObject; Fill((RectTransform)pause.transform);
            var shade = Box(pause.transform, "Shade", new Color(0.067f, 0.086f, 0.059f, 0.78f), false); Fill(shade.rectTransform);
            var head = Label(pause.transform, "PAUSED", 44, Sand, TextAnchor.UpperLeft, titleFont, true);
            Place(head.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -32), new Vector2(300, 56));
            var resume = Btn(pause.transform, "RESUME", Accent, Ink, 24, () => { settingsFromMenu = false; pauseOpen = false; GameInput.LockCursor(true); }, titleFont);
            Place((RectTransform)resume.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -110), new Vector2(260, 60));
            Shadowed(resume.gameObject, AccentDark, 5);
            pauseResume = resume.gameObject;
            var leave = Btn(pause.transform, "LEAVE MATCH", new Color(0, 0, 0, 0), Sand, 22, () => { pauseOpen = false; NetworkBootstrap.Instance?.UiDisconnect(); }, titleFont);
            Place((RectTransform)leave.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -186), new Vector2(260, 56));
            Outline(leave.gameObject, Tan);
            pauseLeave = leave.gameObject;
            var back = Btn(pause.transform, "BACK", new Color(0, 0, 0, 0), Sand, 22, () => settingsFromMenu = false, titleFont);
            Place((RectTransform)back.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(32, -258), new Vector2(260, 56));
            Outline(back.gameObject, Olive);

            var panel = Box(pause.transform, "Settings", DarkBg, true);
            var pr = panel.rectTransform;
            pr.anchorMin = new Vector2(0, 0); pr.anchorMax = new Vector2(1, 1); pr.offsetMin = new Vector2(340, 32); pr.offsetMax = new Vector2(-32, -32);
            Outline(panel.gameObject, Olive);
            var tabs = Label(panel.transform, "SETTINGS", 18, Sand, TextAnchor.UpperLeft, numFont, false);
            Place(tabs.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, -24), new Vector2(400, 24));

            float sens = PlayerPrefs.GetFloat("mouseSensitivity", 0.12f);
            sensSlider = SliderRow(panel.transform, "Mouse sensitivity", -80, 0.02f, 0.4f, sens, v =>
            {
                PlayerPrefs.SetFloat("mouseSensitivity", v);
                var nm = NetworkManager.Singleton;
                var pc = nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject.GetComponent<PlayerController>() : null;
                if (pc != null) pc.MouseSensitivity = v;
            });
            volSlider = SliderRow(panel.transform, "Master volume", -140, 0f, 1f, AudioListener.volume, v => AudioListener.volume = v);

            BuildControlsGuide(panel.transform, -205);
        }

        // Controls guide in the settings (2026-10-31, user): key caps + what they do, two columns. Phones get the touch layout instead.
        void BuildControlsGuide(Transform panel, float top)
        {
            bool touch = GameInput.TouchMode;
            var head = Label(panel, touch ? "TOUCH CONTROLS" : "KEYBOARD CONTROLS", 20, Accent, TextAnchor.UpperLeft, titleFont, false);
            Place(head.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, top), new Vector2(500, 28));
            string[,] left = touch ? new string[,] {
                    { "LEFT THUMB", "Move (pull far out = sprint)" }, { "RIGHT THUMB", "Drag to look around" },
                    { "JUMP", "Jump / leave the plane" }, { "CHUTE", "Open the parachute" }, { "CROUCH", "Crouch / stand up" } }
                : new string[,] {
                    { "W A S D", "Move" }, { "SHIFT", "Sprint (hold)" }, { "SPACE", "Jump / leave plane / parachute" },
                    { "C", "Crouch / stand up" }, { "MOUSE", "Look around" }, { "M", "Big map" }, { "F3", "FPS meter" } };
            string[,] right = touch ? new string[,] {
                    { "FIRE", "Shoot (drag to turn)" }, { "AIM", "Aim on / off (sniper: scope)" }, { "RELOAD", "Reload" },
                    { "BOMB", "Hold + drag to aim, let go to throw" }, { "X", "Cancel the bomb" }, { "MINIMAP", "Tap for the big map" } }
                : new string[,] {
                    { "LEFT CLICK", "Shoot" }, { "RIGHT CLICK", "Aim (hold) - sniper: scope" }, { "R", "Reload" },
                    { "G", "Hold to aim a bomb, let go to throw" }, { "RIGHT CLICK", "Cancel the bomb (while holding G)" },
                    { "ESC", "Pause / settings" }, { "TAB", "Next cat when spectating" } };
            KeyColumn(panel, left, 28, top - 40);
            KeyColumn(panel, right, 450, top - 40);
            if (touch)
            {
                // the player's own button places / sizes (TouchControls.Layout.cs); the menu closes while editing
                var custom = Btn(panel, "CUSTOMIZE CONTROLS", Accent, Ink, 20, () =>
                {
                    settingsFromMenu = false; pauseOpen = false;
                    TouchControls.OpenEditor();
                }, titleFont);
                Place((RectTransform)custom.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, top - 40 - 6 * 38 - 16), new Vector2(320, 52));
                Shadowed(custom.gameObject, AccentDark, 4);
            }
        }

        void KeyColumn(Transform panel, string[,] rows, float x, float y)
        {
            for (int i = 0; i < rows.GetLength(0); i++, y -= 38)
            {
                string key = rows[i, 0];
                float w = Mathf.Max(36f, 14f + key.Length * 10f);
                var cap = Box(panel, "Key", Sand, true);
                Place(cap.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, y), new Vector2(w, 30));
                Shadowed(cap.gameObject, Tan, 3);
                var kt = Label(cap.transform, key, 14, Ink, TextAnchor.MiddleCenter, numFont, false);
                Fill(kt.rectTransform);
                var at = Label(panel, rows[i, 1], 15, Sand, TextAnchor.MiddleLeft, bodyFont, false);
                Place(at.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x + w + 12f, y), new Vector2(330, 30));
            }
        }

        // ------------------------------------------------------------------ widgets
        RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size;
        }

        static void Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        Image Box(Transform parent, string name, Color color, bool rounded)
        {
            var img = Node(name, parent).gameObject.AddComponent<Image>();
            img.color = color;
            if (rounded) { img.sprite = round; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1.4f; }
            img.raycastTarget = false;
            return img;
        }

        RectTransform Panel(Transform parent, string name) => Box(parent, name, PanelC, true).rectTransform;

        Text Label(Transform parent, string text, int size, Color color, TextAnchor anchor, Font font, bool shadow)
        {
            var t = Node("Text", parent).gameObject.AddComponent<Text>();
            t.text = text; t.font = font; t.fontSize = size; t.color = color; t.alignment = anchor;
            t.fontStyle = font == bodyFont && size >= 16 ? FontStyle.Bold : FontStyle.Normal;
            if (font == bodyFont && (anchor == TextAnchor.UpperCenter || size >= 20)) t.fontStyle = FontStyle.Bold;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            if (shadow) Shadowed(t.gameObject, Ink, 3);
            return t;
        }

        Button Btn(Transform parent, string label, Color bg, Color fg, int size, UnityAction onClick, Font font)
        {
            var img = Box(parent, label, bg, true);
            img.raycastTarget = true;
            var b = img.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f); colors.pressedColor = new Color(0.85f, 0.85f, 0.85f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.8f);
            b.colors = colors;
            if (onClick != null) b.onClick.AddListener(onClick);
            b.onClick.AddListener(() => GameAudio.Play2D("ui_click", 0.6f));
            var t = Label(img.transform, label, size, fg, TextAnchor.MiddleCenter, font, false);
            Fill(t.rectTransform);
            t.fontStyle = FontStyle.Bold;
            return b;
        }

        static void Shadowed(GameObject go, Color color, float distance)
        {
            var s = go.AddComponent<Shadow>(); s.effectColor = color; s.effectDistance = new Vector2(0, -distance);
        }

        static void Outline(GameObject go, Color color)
        {
            var o = go.AddComponent<Outline>(); o.effectColor = color; o.effectDistance = new Vector2(2, -2);
        }

        RectTransform Bar(RectTransform parent, Vector2 pos, Vector2 size, Color color)
        {
            var bg = Box(parent, "Bar", CardC, true);
            Place(bg.rectTransform, new Vector2(0, 1), new Vector2(0, 1), pos, size);
            var fill = Box(bg.transform, "Fill", color, true);
            var rt = fill.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        Text Stat(Transform parent, string caption, Vector2 pos, bool fromLeft = false)
        {
            var box = Panel(parent, caption);
            Place(box, fromLeft ? new Vector2(0f, 1) : new Vector2(0.5f, 1), new Vector2(0.5f, 1), pos, new Vector2(170, 92));
            var value = Label(box, "0", 40, Sand, TextAnchor.UpperCenter, numFont, false);
            Place(value.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -10), new Vector2(180, 48));
            var cap = Label(box, caption, 14, Muted, TextAnchor.LowerCenter, numFont, false);
            Place(cap.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 10), new Vector2(180, 20));
            return value;
        }

        InputField Input(RectTransform parent, string value)
        {
            var bg = Box(parent, "Input", CardC, true);
            bg.raycastTarget = true;
            var field = bg.gameObject.AddComponent<InputField>();
            var text = Label(bg.transform, "", 18, Sand, TextAnchor.MiddleLeft, numFont, false);
            Fill(text.rectTransform); text.rectTransform.offsetMin = new Vector2(12, 0); text.rectTransform.offsetMax = new Vector2(-12, 0);
            text.supportRichText = false;
            var ph = Label(bg.transform, "127.0.0.1", 18, Locked, TextAnchor.MiddleLeft, numFont, false);
            Fill(ph.rectTransform); ph.rectTransform.offsetMin = new Vector2(12, 0);
            field.textComponent = text; field.placeholder = ph; field.targetGraphic = bg;
            field.text = value;
            return field;
        }

        Slider SliderRow(Transform parent, string caption, float y, float min, float max, float value, UnityAction<float> onChange)
        {
            var label = Label(parent, caption, 18, Sand, TextAnchor.MiddleLeft, bodyFont, false);
            Place(label.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, y), new Vector2(240, 40));
            var rootRt = Node(caption, parent);
            Place(rootRt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(280, y - 10), new Vector2(480, 20));
            var bg = Box(rootRt, "Background", CardC, true); Fill(bg.rectTransform);
            var area = Node("Fill Area", rootRt); Fill(area);
            var fill = Box(area, "Fill", Accent, true); Fill(fill.rectTransform);
            var handleArea = Node("Handle Area", rootRt); Fill(handleArea); handleArea.offsetMin = new Vector2(10, 0); handleArea.offsetMax = new Vector2(-10, 0);
            var handle = Box(handleArea, "Handle", Sand, true);
            handle.rectTransform.sizeDelta = new Vector2(24, 32);
            handle.raycastTarget = true;
            var s = rootRt.gameObject.AddComponent<Slider>();
            s.fillRect = fill.rectTransform; s.handleRect = handle.rectTransform; s.targetGraphic = handle;
            s.direction = Slider.Direction.LeftToRight; s.minValue = min; s.maxValue = max; s.value = value;
            s.onValueChanged.AddListener(onChange);
            bg.raycastTarget = true;
            return s;
        }

        static Sprite MakeRounded(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    byte a = (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f);
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            return c;
        }
    }
}
