using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// Sniper scope view (2026-11-07): black lens ring filling the screen height with dark bars on the sides, a duplex reticle
    /// (thick outer posts, thin centre lines, mil dots, centre dot) in the weapon's scopeTint, a soft lens vignette and a faint
    /// glass tint. Shown while the local player looks through a scope (PlayerShooter.LocalScope); all textures are made in code.
    /// Sorting 8: under GameUI (10) / VitalsHud / GameHud (11) so health, ammo and the kill feed stay readable.
    /// </summary>
    public class ScopeOverlay : MonoBehaviour
    {
        static ScopeOverlay instance;
        CanvasGroup group;
        readonly System.Collections.Generic.List<Graphic> reticle = new System.Collections.Generic.List<Graphic>();
        RectTransform lens;
        Image glass;
        Color lastTint = new Color(-1, 0, 0);

        /// <summary>True while the scope picture covers the screen (GameUI hides its own crosshair then).</summary>
        public static bool Visible => instance != null && instance.group != null && instance.group.alpha > 0.5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            instance = new GameObject("ScopeOverlay").AddComponent<ScopeOverlay>();
        }

        void Awake()
        {
            instance = this;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;                 // the lens always fills the screen height
            group = gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f; group.blocksRaycasts = false; group.interactable = false;

            var black = new Color(0.01f, 0.01f, 0.012f, 1f);
            // lens: a square as tall as the screen; side bars cover the rest of a wide screen
            lens = Rect("Lens", transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1080, 1080));
            glass = Img("Glass", lens, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.55f, 0.75f, 0.85f, 0.05f), null);
            var ring = Img("Ring", lens, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.white, RingSprite());
            ring.raycastTarget = false;
            var left = Img("BarL", transform, new Vector2(0, 0), new Vector2(0.5f, 1), Vector2.zero, Vector2.zero, black, null);
            left.rectTransform.offsetMax = new Vector2(-520, 0);   // overlaps the black lens corners (sway never opens a gap)
            var right = Img("BarR", transform, new Vector2(0.5f, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero, black, null);
            right.rectTransform.offsetMin = new Vector2(520, 0);

            // duplex reticle: thick posts from the rim to 1/4 of the radius, thin lines through the centre
            const float R = 540f, inner = 135f;
            float postLen = R - inner;
            Line(new Vector2(0, inner + postLen / 2), new Vector2(9, postLen));
            Line(new Vector2(0, -(inner + postLen / 2)), new Vector2(9, postLen));
            Line(new Vector2(inner + postLen / 2, 0), new Vector2(postLen, 9));
            Line(new Vector2(-(inner + postLen / 2), 0), new Vector2(postLen, 9));
            Line(Vector2.zero, new Vector2(2, inner * 2));
            Line(Vector2.zero, new Vector2(inner * 2, 2));
            for (int i = 1; i <= 4; i++)          // mil dots on the thin lines
            {
                float d = i * inner / 4.5f;
                Line(new Vector2(d, 0), new Vector2(6, 6)); Line(new Vector2(-d, 0), new Vector2(6, 6));
                Line(new Vector2(0, -d), new Vector2(6, 6));
            }
            Line(Vector2.zero, new Vector2(5, 5));   // centre dot
        }

        void Line(Vector2 pos, Vector2 size)
        {
            var g = Img("Reticle", lens, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size, Color.black, null);
            reticle.Add(g);
        }

        void LateUpdate()
        {
            var scope = PlayerShooter.LocalScope;
            float blend = scope != null ? scope.ScopeBlend : 0f;
            // the picture snaps in during the last part of the zoom (like raising the scope to the eye)
            float a = Mathf.Clamp01((blend - 0.55f) / 0.35f);
            group.alpha = a;
            if (scope == null) return;
            var tint = scope.Weapon.scopeTint;
            if (tint != lastTint)
            {
                lastTint = tint;
                foreach (var g in reticle) g.color = new Color(tint.r, tint.g, tint.b, 0.95f);
                // a gold reticle gets a warm lens, the plain one a cool blue coating
                glass.color = tint.r > 0.5f ? new Color(1f, 0.85f, 0.45f, 0.06f) : new Color(0.55f, 0.75f, 0.85f, 0.05f);
            }
            // slight breathing sway of the reticle picture (visual only; the shot still goes to the screen centre)
            float t = Time.time;
            lens.anchoredPosition = new Vector2(Mathf.Sin(t * 0.9f) * 2.5f, Mathf.Sin(t * 1.3f + 1f) * 2f) * a;
        }

        // ---------------------------------------------------------------- helpers
        static RectTransform Rect(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        static Image Img(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size, Color c, Sprite s)
        {
            var rt = Rect(name, parent, aMin, aMax, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c; img.sprite = s; img.raycastTarget = false;
            return img;
        }

        /// <summary>Black outside a circle, clear inside, a soft rim and a darker vignette just inside the rim.</summary>
        static Sprite RingSprite()
        {
            const int N = 512;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[N * N];
            float c = (N - 1) * 0.5f, r = N * 0.5f - 2f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / r;     // 0 centre .. 1 rim
                    float outside = Mathf.Clamp01((d - 0.985f) / 0.015f);                 // hard but anti-aliased rim
                    float vignette = Mathf.Clamp01((d - 0.72f) / 0.27f); vignette *= vignette * 0.75f;
                    float alpha = Mathf.Max(outside, vignette);
                    px[y * N + x] = new Color32(3, 3, 4, (byte)(alpha * 255));
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
        }
    }
}
