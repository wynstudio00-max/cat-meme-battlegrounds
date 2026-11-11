using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace CatBattleground
{
    /// <summary>
    /// The winner's moment (2026-11-07), local to the winning player, ~5.5 s before the results card:
    ///   0.0 s  white flash + slow motion on the final kill (timeScale 0.3 for 0.6 s; only where this machine runs the match:
    ///          host / offline, so a remote client never desyncs)
    ///   0.6 s  camera eases to one still front shot of the cat (ThirdPersonCamera reads OrbitTarget; no circling for now, user);
    ///          the cat plays its Victory clip (PlayerAnimator.SetVictory, synced by NetworkAnimator); banner LAST CAT STANDING!,
    ///          fanfare, confetti (KillFx)
    ///   0.9 s  $CMBG coins rain down the screen (UI only, one 128 px sprite Resources/UI/Victory/coin)
    ///   5.5 s  Running = false -> GameUI shows the results card (count-up numbers).
    /// Everything is code + UI; the only asset is the coin sprite (and one short Victory clip per cat).
    /// </summary>
    public class VictorySequence : MonoBehaviour
    {
        static VictorySequence instance;

        /// <summary>True while the celebration plays (GameUI waits with the results card).</summary>
        public static bool Running { get; private set; }
        /// <summary>The cat the gameplay camera circles (null = normal camera).</summary>
        public static Transform OrbitTarget { get; private set; }
        /// <summary>Unscaled time when the orbit started.</summary>
        public static float OrbitStart { get; private set; }

        const float SlowMoSeconds = 0.6f, CardAfter = 5.5f;
        static readonly Color Gold = new Color(1f, 0.82f, 0.25f);

        bool sawPlaying, done;
        float startedAt = -1f;
        bool slowApplied, orbitStarted, coinsStarted;
        NetworkPlayer winner;
        PlayerController winnerController;

        Image flash;
        RectTransform coinLayer;
        Sprite coinSprite;
        readonly System.Collections.Generic.List<Coin> coins = new System.Collections.Generic.List<Coin>();
        float coinSpawnLeft, coinTimer;

        class Coin { public RectTransform rt; public Image img; public Vector2 vel; public float spin, phase, life; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            instance = new GameObject("VictorySequence").AddComponent<VictorySequence>();
        }

        void Awake()
        {
            instance = this;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 14;   // under the Announcer banner (15), over the HUD
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            coinLayer = new GameObject("Coins", typeof(RectTransform)).GetComponent<RectTransform>();
            coinLayer.SetParent(transform, false);
            coinLayer.anchorMin = Vector2.zero; coinLayer.anchorMax = Vector2.one; coinLayer.offsetMin = coinLayer.offsetMax = Vector2.zero;

            var f = new GameObject("Flash", typeof(RectTransform)).GetComponent<RectTransform>();
            f.SetParent(transform, false);
            f.anchorMin = Vector2.zero; f.anchorMax = Vector2.one; f.offsetMin = f.offsetMax = Vector2.zero;
            flash = f.gameObject.AddComponent<Image>();
            flash.color = new Color(1f, 1f, 1f, 0f); flash.raycastTarget = false;

            coinSprite = Resources.Load<Sprite>("UI/Victory/coin");
            if (coinSprite == null)
            {
                var t = Resources.Load<Texture2D>("UI/Victory/coin");
                if (t != null) coinSprite = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f));
            }
        }

        void Update()
        {
            var match = MatchManager.Instance;
            var nm = NetworkManager.Singleton;
            var me = nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null
                ? nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>() : null;
            var state = match != null && match.IsSpawned ? match.State : MatchState.Waiting;

            if (state == MatchState.Playing) { sawPlaying = true; if (done) Reset(); }
            if (state != MatchState.Ended && (startedAt >= 0f || done)) Reset();

            // the winner: Battle Royale, local player placed 1st (placement can arrive a frame after the state)
            if (!done && startedAt < 0f && state == MatchState.Ended && sawPlaying && me != null && !match.IsZombies && me.Placement == 1)
                Begin(me);

            if (startedAt >= 0f) Tick();
            UpdateCoins();
        }

        void Begin(NetworkPlayer me)
        {
            winner = me;
            winnerController = me.GetComponent<PlayerController>();
            startedAt = Time.unscaledTime;
            Running = true;
            slowApplied = orbitStarted = coinsStarted = false;
            flash.color = new Color(1f, 1f, 1f, 0.75f);
            // slow motion only where the simulation runs here (host / offline browser match)
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer) { Time.timeScale = 0.3f; slowApplied = true; }
            if (winnerController != null) winnerController.InputEnabled = false;
            GameInput.LockCursor(false);
        }

        void Tick()
        {
            float t = Time.unscaledTime - startedAt;
            flash.color = new Color(1f, 1f, 1f, Mathf.Max(0f, 0.75f - t * 2.2f));
            if (slowApplied && t >= SlowMoSeconds) { Time.timeScale = 1f; slowApplied = false; }

            if (!orbitStarted && t >= SlowMoSeconds && winner != null)
            {
                orbitStarted = true;
                OrbitTarget = winner.transform;
                OrbitStart = Time.unscaledTime;
                var anim = winner.GetComponent<PlayerAnimator>();
                if (anim != null) anim.SetVictory(true);
                Announcer.Show("LAST CAT STANDING!", "VICTORY  ·  #1", Gold, "victory", 3.6f);
                KillFx.Play(winner.transform.position + Vector3.up * 1.2f);
                KillFx.Play(winner.transform.position + Vector3.up * 2.2f + winner.transform.right);
            }
            if (!coinsStarted && t >= 0.9f) { coinsStarted = true; coinSpawnLeft = 56; coinTimer = 0f; }
            if (Running && t >= CardAfter) Running = false;   // results card; the orbit keeps turning behind it
        }

        void Reset()
        {
            if (slowApplied) Time.timeScale = 1f;
            slowApplied = false;
            if (winner != null)
            {
                var anim = winner.GetComponent<PlayerAnimator>();
                if (anim != null) anim.SetVictory(false);
            }
            if (winnerController != null) winnerController.InputEnabled = true;
            winner = null; winnerController = null;
            startedAt = -1f; Running = false; OrbitTarget = null; done = false;
            sawPlaying = false;
            flash.color = new Color(1f, 1f, 1f, 0f);
            foreach (var c in coins) if (c.rt != null) Destroy(c.rt.gameObject);
            coins.Clear(); coinSpawnLeft = 0;
        }

        // ---------------------------------------------------------------- coin rain (UI only)
        void UpdateCoins()
        {
            float dt = Time.unscaledDeltaTime;
            if (coinSpawnLeft > 0)
            {
                coinTimer -= dt;
                while (coinTimer <= 0f && coinSpawnLeft > 0)
                {
                    coinTimer += 0.045f; coinSpawnLeft--;
                    SpawnCoin();
                }
            }
            var size = ((RectTransform)transform).rect.size;
            for (int i = coins.Count - 1; i >= 0; i--)
            {
                var c = coins[i];
                c.life += dt;
                c.vel.y -= 1500f * dt;                         // gravity, px/s^2
                c.rt.anchoredPosition += c.vel * dt;
                c.phase += c.spin * dt;
                c.rt.localScale = new Vector3(Mathf.Cos(c.phase), 1f, 1f);       // flipping coin
                c.rt.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(c.phase * 0.5f) * 18f);
                float y = c.rt.anchoredPosition.y;
                if (y < -size.y * 0.5f + 120f) c.img.color = new Color(1, 1, 1, Mathf.Clamp01((y + size.y * 0.5f) / 120f));
                if (y < -size.y * 0.5f - 80f) { Destroy(c.rt.gameObject); coins.RemoveAt(i); }
            }
        }

        void SpawnCoin()
        {
            var size = ((RectTransform)transform).rect.size;
            var rt = new GameObject("Coin", typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(coinLayer, false);
            float s = Random.Range(46f, 86f);
            rt.sizeDelta = new Vector2(s, s);
            rt.anchoredPosition = new Vector2(Random.Range(-size.x * 0.48f, size.x * 0.48f), size.y * 0.5f + s);
            var img = rt.gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            if (coinSprite != null) img.sprite = coinSprite;
            else img.color = Gold;
            coins.Add(new Coin
            {
                rt = rt, img = img,
                vel = new Vector2(Random.Range(-90f, 90f), Random.Range(-300f, -60f)),
                spin = Random.Range(5f, 11f) * (Random.value < 0.5f ? -1f : 1f),
                phase = Random.Range(0f, 6.28f)
            });
            if (Random.value < 0.25f) GameAudio.Play2D("pickup", 0.12f, Random.Range(1.3f, 1.7f));
        }

        void OnDestroy()
        {
            if (slowApplied) Time.timeScale = 1f;
            if (instance == this) { instance = null; Running = false; OrbitTarget = null; }
        }
    }
}
