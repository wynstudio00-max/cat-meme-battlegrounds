using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// Zombie Survival HUD (own overlay canvas, client only, auto-created when a NetworkBootstrap exists), top centre
    /// under the compass: WAVE n, zombies left or the countdown to the next wave, and the boss health bar while a boss lives.
    /// Hidden in battle royale.
    /// </summary>
    public class ZombieHud : MonoBehaviour
    {
        static ZombieHud instance;

        static readonly Color Sand = new Color(0.96f, 0.92f, 0.82f), Toxic = new Color(0.55f, 1f, 0.35f), Blood = new Color(0.9f, 0.18f, 0.16f);

        RectTransform panel, bossRow, bossFill;
        Text waveText, infoText, bossText;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            instance = new GameObject("ZombieHud").AddComponent<ZombieHud>();
        }

        void Awake()
        {
            var builtIn = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var titleFont = Resources.Load<Font>("Fonts/Title") ?? builtIn;
            var numFont = Resources.Load<Font>("Fonts/Numbers") ?? builtIn;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 12;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;

            panel = Node("Panel", transform);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 1f); panel.pivot = new Vector2(0.5f, 1f);
            panel.anchoredPosition = new Vector2(0, -78); panel.sizeDelta = new Vector2(420, 120);

            waveText = Label("Wave", panel, titleFont, 40, Toxic, new Vector2(0, -24));
            infoText = Label("Info", panel, numFont, 20, Sand, new Vector2(0, -60));

            bossRow = Node("Boss", panel);
            bossRow.anchorMin = bossRow.anchorMax = new Vector2(0.5f, 1f); bossRow.pivot = new Vector2(0.5f, 1f);
            bossRow.anchoredPosition = new Vector2(0, -82); bossRow.sizeDelta = new Vector2(360, 30);
            var back = bossRow.gameObject.AddComponent<Image>(); back.color = new Color(0.05f, 0.04f, 0.04f, 0.8f); back.raycastTarget = false;
            bossFill = Node("Fill", bossRow);
            bossFill.anchorMin = new Vector2(0, 0); bossFill.anchorMax = new Vector2(1, 1); bossFill.pivot = new Vector2(0, 0.5f);
            bossFill.offsetMin = new Vector2(3, 3); bossFill.offsetMax = new Vector2(-3, -3);
            var fill = bossFill.gameObject.AddComponent<Image>(); fill.color = Blood; fill.raycastTarget = false;
            bossText = Label("BossName", bossRow, titleFont, 18, Sand, Vector2.zero);
            bossText.rectTransform.anchorMin = bossText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            bossText.text = "BOSS";
            panel.gameObject.SetActive(false);
        }

        static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        static Text Label(string name, RectTransform parent, Font font, int size, Color color, Vector2 pos)
        {
            var rt = Node(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(420, size + 10);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.color = color; t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var o = rt.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0.04f, 0.03f, 0.02f, 0.9f); o.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        void Update()
        {
            var match = MatchManager.Instance;
            bool show = match != null && match.IsSpawned && match.IsZombies && match.State == MatchState.Playing;
            if (panel.gameObject.activeSelf != show) panel.gameObject.SetActive(show);
            if (!show) return;

            var waves = match.Waves;
            if (waves.InBreak)
            {
                waveText.text = waves.Wave == 0 ? "GET READY" : "WAVE " + waves.Wave + " CLEARED";
                infoText.text = "Next wave in " + Mathf.CeilToInt(waves.BreakTimeLeft) + "s";
            }
            else
            {
                waveText.text = "WAVE " + Mathf.Max(1, waves.Wave);
                infoText.text = waves.Remaining + (waves.Remaining == 1 ? " zombie left" : " zombies left");
            }

            var boss = waves.Boss;
            bool bossOn = boss != null && !boss.Health.IsDead;
            if (bossRow.gameObject.activeSelf != bossOn) bossRow.gameObject.SetActive(bossOn);
            if (bossOn) bossFill.localScale = new Vector3(Mathf.Clamp01(boss.HealthFraction), 1f, 1f);
        }
    }
}
