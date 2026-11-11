using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// Combat "juice" for the local player, on its own overlay canvas above <see cref="GameUI"/>:
    ///  * hit marker: four ticks around the crosshair that pop on every server-confirmed hit (white = health, blue = armor,
    ///    big red = the hit that eliminated);
    ///  * floating damage numbers at the hit point built from the user's digit art (Resources/UI/DamageNumbers: normal = health,
    ///    shield = armor, headshot = orange + starburst + HEADSHOT label); pop, rise and drift, fade; quick hits on the same cat
    ///    add up into one growing number; "!" on a kill or headshot. Falls back to plain text when the art is missing;
    ///  * kill banners from the user's art (Resources/UI/KillBanners): 1 KILL, HEADSHOT!, DOUBLE / TRIPLE / MONSTER KILL,
    ///    LEGENDARY (kills within 7 s), sliding in with the emblem punching in on top;
    ///  * crosshair bloom (<see cref="Bloom"/>, read by GameUI) and the camera side of every shot (recoil, shake, FOV punch)
    ///    plus camera shake when the local cat is hurt or dies.
    /// Hits come from the server (<see cref="PlayerShooter"/> HitConfirmRpc), so numbers never show for shots that missed online.
    /// </summary>
    public class CombatFeedback : MonoBehaviour
    {
        static CombatFeedback instance;
        /// <summary>0..1 crosshair spread from recent shots.</summary>
        public static float Bloom { get; private set; }

        static readonly Color HealthC = Color.white, ArmorC = new Color(0.45f, 0.78f, 1f), BigC = new Color(1f, 0.86f, 0.3f),
            KillC = new Color(1f, 0.32f, 0.2f), Accent = new Color(1f, 0.54f, 0.12f), HeadC = new Color(1f, 0.62f, 0.15f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() { if (!Application.isBatchMode) Ensure(); }

        static CombatFeedback Ensure()
        {
            if (instance == null && !Application.isBatchMode) instance = new GameObject("CombatFeedback").AddComponent<CombatFeedback>();
            return instance;
        }

        Font titleFont, numFont, bangers;
        RectTransform root;
        Canvas canvas;

        // hit marker
        readonly Image[] ticks = new Image[4];
        float markerTime = -10f; int markerKind;   // 0 health, 1 armor, 2 kill, 3 headshot

        // damage numbers
        const int MaxGlyphs = 6;
        const float DigitHeight = 75f, DigitOverlap = 9f;   // art pixels
        class Num
        {
            public RectTransform rt; public Text text; public Outline outline;
            public Image[] glyphs; public Image burst; public Text headLabel;
            public Vector3 world; public Vector2 drift; public float start, popStart, value, life; public ulong target;
            public bool kill, armor, head, active;
            public string layoutKey;
        }
        readonly List<Num> nums = new List<Num>();
        readonly Dictionary<string, Sprite[]> digitSets = new Dictionary<string, Sprite[]>();   // 0-9, 10 = "!"
        Sprite burstSprite;
        bool digitArt;

        // kill banner
        RectTransform bannerRt, emblemRt; Image bannerImg, emblemImg;
        Text killTitle, killSub;     // fallback when the banner art is missing
        float killTime = -10f;
        readonly List<float> recentKills = new List<float>();
        const float BannerScale = 0.45f;   // 864x254 art -> ~389x114 on the 1280x720 canvas (user: a bit smaller than 0.6)
        static readonly string[] BannerNames = { "01_1_KILL", "02_HEADSHOT", "03_DOUBLE_KILL", "04_TRIPLE_KILL", "05_MONSTER_KILL", "06_LEGENDARY" };
        readonly Sprite[] banners = new Sprite[6], emblems = new Sprite[6];

        Health watched;
        float lastTickSound = -10f, lastHurtSound = -10f;

        // ------------------------------------------------------------------ API
        /// <summary>Owner fired a shot: recoil, a little shake, crosshair bloom (bigger guns kick harder).</summary>
        public static void OnShot(WeaponStats w)
        {
            if (Application.isBatchMode) return;
            Ensure();
            float power = w.damage * Mathf.Max(1, w.pellets);                 // 12 (SMG) .. 72 (shotgun / sniper)
            float kick = Mathf.Clamp(0.3f + power / 34f, 0.35f, 2.4f);
            CameraFx.Kick(kick, Random.Range(-0.35f, 0.35f) * kick);
            CameraFx.Shake(Mathf.Clamp(0.05f + power / 420f, 0.06f, 0.24f));
            if (power >= 40f) CameraFx.Punch(Mathf.Clamp(power / 40f, 1f, 2.2f));
            Bloom = Mathf.Clamp01(Bloom + 0.16f + kick * 0.12f);
        }

        /// <summary>The server confirmed one of our shots hit <paramref name="targetId"/> for <paramref name="damage"/> (health + armor).</summary>
        public static void OnHitConfirmed(Vector3 point, float damage, bool armor, bool kill, ulong targetId, bool head = false)
        {
            var self = Ensure();
            if (self != null) self.Hit(point, damage, armor, kill, targetId, head);
        }

        // ------------------------------------------------------------------ setup
        static Sprite LoadSprite(string path)
        {
            var s = Resources.Load<Sprite>(path);
            if (s != null) return s;
            var t = Resources.Load<Texture2D>(path);
            return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f)) : null;
        }

        void Awake()
        {
            instance = this;
            var builtIn = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleFont = Resources.Load<Font>("Fonts/Title") ?? builtIn;
            numFont = Resources.Load<Font>("Fonts/Numbers") ?? titleFont;
            bangers = Resources.Load<Font>("Fonts/Bangers") ?? titleFont;

            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            root = (RectTransform)transform;

            for (int i = 0; i < 4; i++)
            {
                var img = NewNode("Tick" + i, root).gameObject.AddComponent<Image>();
                img.raycastTarget = false;
                img.rectTransform.sizeDelta = new Vector2(3f, 12f);
                img.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f + 90f * i);
                img.color = new Color(1, 1, 1, 0);
                var o = img.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0, 0, 0, 0.6f); o.effectDistance = new Vector2(1, -1);
                ticks[i] = img;
            }

            // digit art
            foreach (var set in new[] { "normal", "shield", "headshot" })
            {
                var arr = new Sprite[11];
                for (int d = 0; d < 10; d++) arr[d] = LoadSprite($"UI/DamageNumbers/{set}/{set}_{d}");
                arr[10] = LoadSprite($"UI/DamageNumbers/{set}/{set}_excl");
                digitSets[set] = arr;
            }
            burstSprite = LoadSprite("UI/DamageNumbers/headshot_burst_bg");
            digitArt = digitSets["normal"][0] != null;

            for (int i = 0; i < 32; i++)
            {
                var n = new Num { rt = NewNode("Dmg" + i, root) };
                n.rt.sizeDelta = new Vector2(220, 60);
                if (digitArt)
                {
                    n.burst = NewNode("Burst", n.rt).gameObject.AddComponent<Image>();
                    n.burst.sprite = burstSprite; n.burst.raycastTarget = false;
                    n.glyphs = new Image[MaxGlyphs];
                    for (int g = 0; g < MaxGlyphs; g++) { n.glyphs[g] = NewNode("G" + g, n.rt).gameObject.AddComponent<Image>(); n.glyphs[g].raycastTarget = false; }
                    n.headLabel = NewText("Head", n.rt, bangers, 20);
                    n.headLabel.text = "HEADSHOT";
                }
                else
                {
                    n.text = n.rt.gameObject.AddComponent<Text>();
                    StyleText(n.text, numFont, 32);
                    n.outline = n.text.GetComponent<Outline>();
                }
                n.rt.gameObject.SetActive(false);
                nums.Add(n);
            }

            // kill banners
            for (int i = 0; i < 6; i++)
            {
                banners[i] = LoadSprite("UI/KillBanners/" + BannerNames[i]);
                emblems[i] = LoadSprite("UI/KillBanners/" + BannerNames[i] + "_emblem");
            }
            if (banners[0] != null)
            {
                bannerRt = NewNode("KillBanner", root);
                bannerRt.anchoredPosition = new Vector2(0, -140);
                bannerRt.sizeDelta = new Vector2(864, 254) * BannerScale;
                bannerImg = bannerRt.gameObject.AddComponent<Image>(); bannerImg.raycastTarget = false;
                emblemRt = NewNode("Emblem", bannerRt);
                emblemRt.anchoredPosition = new Vector2((122f - 432f) * BannerScale, 0f);
                emblemRt.sizeDelta = new Vector2(264, 264) * BannerScale;
                emblemImg = emblemRt.gameObject.AddComponent<Image>(); emblemImg.raycastTarget = false;
                bannerRt.gameObject.SetActive(false);
            }
            else
            {
                killTitle = NewText("KillTitle", root, titleFont, 46);
                killTitle.rectTransform.anchoredPosition = new Vector2(0, -118);
                killTitle.rectTransform.sizeDelta = new Vector2(700, 60);
                killTitle.text = "ELIMINATED";
                killSub = NewText("KillSub", root, titleFont, 22);
                killSub.rectTransform.anchoredPosition = new Vector2(0, -160);
                killSub.rectTransform.sizeDelta = new Vector2(700, 34);
                killTitle.gameObject.SetActive(false); killSub.gameObject.SetActive(false);
            }
        }

        void OnDestroy()
        {
            if (watched != null) { watched.Damaged -= OnLocalDamaged; watched.Died -= OnLocalDied; }
            if (instance == this) instance = null;
        }

        RectTransform NewNode(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        void StyleText(Text t, Font font, int size)
        {
            t.font = font; t.fontSize = size; t.fontStyle = font.name == "LegacyRuntime" ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = TextAnchor.MiddleCenter; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var o = t.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.05f, 0.04f, 0.03f, 0.95f); o.effectDistance = new Vector2(2.2f, -2.2f);
            var s = t.gameObject.AddComponent<Shadow>(); s.effectColor = new Color(0, 0, 0, 0.45f); s.effectDistance = new Vector2(3f, -4f);
        }

        Text NewText(string name, Transform parent, Font font, int size)
        {
            var t = NewNode(name, parent).gameObject.AddComponent<Text>();
            StyleText(t, font, size);
            t.rectTransform.sizeDelta = new Vector2(220, 60);
            return t;
        }

        // ------------------------------------------------------------------ events
        void Hit(Vector3 point, float damage, bool armor, bool kill, ulong targetId, bool head)
        {
            markerTime = Time.time;
            markerKind = kill ? 2 : head ? 3 : armor ? 1 : 0;
            if (Time.time - lastTickSound > 0.045f || kill || head)
            {
                lastTickSound = Time.time;
                GameAudio.Play2D(kill ? "hitmarker_kill" : armor ? "hitmarker_armor" : "hitmarker", kill ? 0.8f : head ? 0.75f : 0.55f, head ? 1.25f : 1f, 0.04f);
            }
            if (kill) GameAudio.Play2D("banner_whoosh", 0.5f, 1f, 0f, 0.05f);

            // stack quick hits on the same cat into one number
            Num n = null;
            foreach (var x in nums)
                if (x.active && !x.kill && x.target == targetId && Time.time - x.start < 0.7f) { n = x; break; }
            if (n == null)
            {
                n = Oldest();
                n.value = 0f; n.start = Time.time; n.world = point; n.target = targetId; n.head = false; n.armor = false;
                n.drift = new Vector2(Random.Range(-38f, 38f), Random.Range(-6f, 10f));
                n.active = true; n.rt.gameObject.SetActive(true);
            }
            else n.start = Time.time - 0.05f;   // keep it alive, no new drift
            n.value += damage;
            n.popStart = Time.time;
            n.kill = kill; n.armor = armor && !head; n.head |= head;
            n.life = kill || n.head ? 1.5f : 1.0f;

            if (kill)
            {
                killTime = Time.time;
                recentKills.Add(Time.time);
                recentKills.RemoveAll(t => Time.time - t > 7f);
                int streak = recentKills.Count;
                int b = streak >= 5 ? 5 : streak == 4 ? 4 : streak == 3 ? 3 : streak == 2 ? 2 : head ? 1 : 0;
                if (bannerImg != null)
                {
                    bannerImg.sprite = banners[b] != null ? banners[b] : banners[0];
                    emblemImg.sprite = emblems[b];
                    emblemImg.enabled = emblems[b] != null;
                }
                else killSub.text = streak >= 4 ? "MULTI KILL!" : streak == 3 ? "TRIPLE KILL!" : streak == 2 ? "DOUBLE KILL!" : head ? "HEADSHOT!" : "+1 KILL";
                CameraFx.Punch(4f);
                CameraFx.Shake(0.28f);
            }
        }

        Num Oldest()
        {
            Num best = null;
            foreach (var x in nums) { if (!x.active) return x; if (best == null || x.start < best.start) best = x; }
            return best;
        }

        void OnLocalDamaged(float amount, GameObject attacker)
        {
            CameraFx.Shake(Mathf.Clamp(0.2f + amount / 50f, 0.2f, 0.7f));
            if (Time.time - lastHurtSound < 0.09f) return;
            lastHurtSound = Time.time;
            GameAudio.Play2D("damage_taken", Mathf.Clamp(0.45f + amount / 60f, 0.45f, 0.9f), 1f, 0.08f);
        }
        void OnLocalDied(GameObject attacker) => CameraFx.Shake(0.9f);

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            WatchLocalHealth();
            Bloom = Mathf.MoveTowards(Bloom, 0f, Time.deltaTime * 2.6f);
            UpdateMarker();
            UpdateNumbers();
            UpdateKillBanner();
        }

        void WatchLocalHealth()
        {
            Health h = null;
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient && nm.LocalClient != null && nm.LocalClient.PlayerObject != null) h = nm.LocalClient.PlayerObject.GetComponent<Health>();
            else if (ThirdPersonCamera.Current != null && ThirdPersonCamera.Current.Target != null) h = ThirdPersonCamera.Current.Target.GetComponent<Health>();
            if (h == watched) return;
            if (watched != null) { watched.Damaged -= OnLocalDamaged; watched.Died -= OnLocalDied; }
            watched = h;
            if (watched != null) { watched.Damaged += OnLocalDamaged; watched.Died += OnLocalDied; }
        }

        void UpdateMarker()
        {
            float age = Time.time - markerTime;
            bool big = markerKind >= 2;
            float dur = big ? 0.55f : 0.24f;
            float a = age < dur * 0.45f ? 1f : Mathf.Clamp01(1f - (age - dur * 0.45f) / (dur * 0.55f));
            float pop = Mathf.Exp(-age * 22f);
            var c = markerKind == 2 ? KillC : markerKind == 3 ? HeadC : markerKind == 1 ? ArmorC : HealthC;
            c.a = a;
            float dist = (big ? 15f : 11f) + 9f * pop;
            float len = (big ? 17f : 12f) * (1f + 0.35f * pop);
            for (int i = 0; i < 4; i++)
            {
                var img = ticks[i];
                img.enabled = a > 0.01f;
                if (!img.enabled) continue;
                float ang = (45f + 90f * i) * Mathf.Deg2Rad;
                img.rectTransform.anchoredPosition = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang)) * dist;
                img.rectTransform.localRotation = Quaternion.Euler(0, 0, -(45f + 90f * i));
                img.rectTransform.sizeDelta = new Vector2(big ? 4f : 3f, len);
                img.color = c;
            }
        }

        // lays out the digit sprites of one number (centred), sized to 'height' canvas pixels
        void LayoutDigits(Num n, string chars, string set, float height)
        {
            var sprites = digitSets[set];
            float k = height / DigitHeight;
            float total = 0f;
            int count = Mathf.Min(chars.Length, MaxGlyphs);
            for (int i = 0; i < count; i++)
            {
                var sp = sprites[chars[i] == '!' ? 10 : chars[i] - '0'];
                total += (sp != null ? sp.rect.width : 55f) * k;
                if (i > 0) total -= DigitOverlap * k;
            }
            float x = -total * 0.5f;
            for (int i = 0; i < MaxGlyphs; i++)
            {
                var g = n.glyphs[i];
                if (i >= count) { g.enabled = false; continue; }
                var sp = sprites[chars[i] == '!' ? 10 : chars[i] - '0'];
                float w = (sp != null ? sp.rect.width : 55f) * k;
                g.enabled = sp != null;
                g.sprite = sp;
                g.rectTransform.sizeDelta = new Vector2(w, (sp != null ? sp.rect.height : DigitHeight) * k);
                g.rectTransform.anchoredPosition = new Vector2(x + w * 0.5f, 0f);
                x += w - DigitOverlap * k;
            }
            n.burst.enabled = set == "headshot" && burstSprite != null;
            n.burst.rectTransform.sizeDelta = Vector2.one * height * 2.3f;
            n.headLabel.gameObject.SetActive(set == "headshot");
            n.headLabel.rectTransform.anchoredPosition = new Vector2(0, -height * 0.85f);
            n.headLabel.fontSize = Mathf.RoundToInt(height * 0.42f);
        }

        void UpdateNumbers()
        {
            var cam = Camera.main;
            foreach (var n in nums)
            {
                if (!n.active) continue;
                float age = Time.time - n.start;
                if (age > n.life || cam == null) { n.active = false; n.rt.gameObject.SetActive(false); continue; }
                Vector3 sp = cam.WorldToScreenPoint(n.world);
                bool visible = sp.z >= 0.2f;
                n.rt.localScale = visible ? n.rt.localScale : Vector3.zero;
                if (!visible) continue;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, null, out var lp);
                float k = age / n.life;
                float rise = 80f * (1f - Mathf.Exp(-age * 3.4f));
                n.rt.anchoredPosition = lp + new Vector2(n.drift.x * k, rise + n.drift.y);

                float pAge = Time.time - n.popStart;
                float pop = 1f + 0.85f * Mathf.Exp(-pAge * 9f) * Mathf.Cos(pAge * 26f);   // elastic punch
                float scale = pop * (k > 0.8f ? Mathf.Lerp(1f, 0.6f, (k - 0.8f) / 0.2f) : 1f);
                n.rt.localScale = Vector3.one * scale;
                float alpha = k > 0.7f ? Mathf.Clamp01(1f - (k - 0.7f) / 0.3f) : 1f;

                int v = Mathf.Max(1, Mathf.RoundToInt(n.value));
                if (digitArt)
                {
                    string set = n.head ? "headshot" : n.armor ? "shield" : "normal";
                    string chars = v + (n.kill || n.head ? "!" : "");
                    float height = Mathf.Lerp(30f, 50f, Mathf.Clamp01(n.value / 100f)) * (n.head ? 1.25f : n.kill ? 1.15f : 1f);
                    string key = chars + set + Mathf.RoundToInt(height);
                    if (key != n.layoutKey) { n.layoutKey = key; LayoutDigits(n, chars, set, height); }
                    var c = new Color(1, 1, 1, alpha);
                    foreach (var g in n.glyphs) g.color = c;
                    n.burst.color = new Color(1, 1, 1, alpha * 0.95f);
                    n.burst.rectTransform.localRotation = Quaternion.Euler(0, 0, age * 40f);
                    n.headLabel.color = new Color(1f, 0.95f, 0.9f, alpha);
                }
                else
                {
                    n.text.text = n.kill ? v + "!" : v.ToString();
                    n.text.fontSize = Mathf.RoundToInt(Mathf.Lerp(28f, 52f, Mathf.Clamp01(n.value / 100f)) * (n.kill ? 1.15f : 1f));
                    var c = n.kill ? KillC : n.head ? HeadC : n.armor ? ArmorC : n.value >= 40f ? BigC : HealthC;
                    c.a = alpha;
                    n.text.color = c;
                    var oc = n.outline.effectColor; oc.a = 0.95f * c.a; n.outline.effectColor = oc;
                }
            }
        }

        void UpdateKillBanner()
        {
            float age = Time.time - killTime;
            bool on = age < 2.1f;
            if (bannerRt != null)
            {
                if (bannerRt.gameObject.activeSelf != on) bannerRt.gameObject.SetActive(on);
                if (!on) return;
                // slide in from the left with an overshoot, hold, slide out to the right and fade
                float x = age < 0.22f ? Mathf.Lerp(-400f, 0f, EaseOutBack(age / 0.22f)) : age > 1.8f ? Mathf.Lerp(0f, 200f, (age - 1.8f) / 0.3f) : 0f;
                bannerRt.anchoredPosition = new Vector2(x, -140f);
                float a = age > 1.8f ? 1f - (age - 1.8f) / 0.3f : 1f;
                bannerImg.color = new Color(1, 1, 1, a);
                // the emblem punches in a moment later, spinning into place
                float e = Mathf.Clamp01((age - 0.08f) / 0.32f);
                float es = age < 0.08f ? 0f : Mathf.Lerp(2.2f, 1f, EaseOutBack(e));
                emblemRt.localScale = Vector3.one * es;
                emblemRt.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(-35f, 0f, EaseOutBack(e)));
                emblemImg.color = new Color(1, 1, 1, a);
                return;
            }
            killTitle.gameObject.SetActive(on); killSub.gameObject.SetActive(on);
            if (!on) return;
            float s = age < 0.2f ? Mathf.Lerp(2.3f, 0.92f, EaseOut(age / 0.2f)) : age < 0.3f ? Mathf.Lerp(0.92f, 1f, (age - 0.2f) / 0.1f) : 1f;
            float al = age < 1.45f ? 1f : 1f - (age - 1.45f) / 0.45f;
            killTitle.rectTransform.localScale = Vector3.one * s;
            var c = Accent; c.a = al; killTitle.color = c;
            float s2 = age < 0.12f ? 0f : age < 0.32f ? Mathf.Lerp(1.6f, 1f, EaseOut((age - 0.12f) / 0.2f)) : 1f;
            killSub.rectTransform.localScale = Vector3.one * s2;
            killSub.color = recentKills.Count >= 2 ? new Color(1f, 0.86f, 0.3f, al) : new Color(0.96f, 0.92f, 0.82f, al);
        }

        static float EaseOut(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }
        static float EaseOutBack(float t) { t = Mathf.Clamp01(t); const float c1 = 1.70158f, c3 = c1 + 1f; return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f); }
    }
}
