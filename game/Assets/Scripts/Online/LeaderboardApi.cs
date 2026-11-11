using System;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CatBattleground
{
    /// <summary>
    /// Client for the leaderboard Worker (Backend/leaderboard, Cloudflare Worker + D1, 2026-10-28).
    /// The player is a random id + device secret made on first use (PlayerPrefs lbPlayerId / lbSecret, name = PlayerPrefs playerName).
    /// Every match: StartRun at take-off (server hands out a one-time token), FinishRun with the result (HMAC with SubmitKey); the
    /// server recomputes the points and checks they are possible. Server address: URL ?api=..., else PlayerPrefs lbApi, else
    /// the local `wrangler dev` in the editor / DefaultUrl in builds.
    /// </summary>
    public static class LeaderboardApi
    {
        /// <summary>The deployed Worker (set after `npx wrangler deploy`, e.g. https://cmbg-leaderboard.&lt;account&gt;.workers.dev
        /// or a custom domain like https://api.catbattlegrounds.com).</summary>
        public const string DefaultUrl = "https://api.catbattlegrounds.com";
        /// <summary>Same Worker under its workers.dev address: tried when the default does not answer (new domain not in
        /// someone's DNS yet, or one of the two addresses switched off).</summary>
        public const string FallbackUrl = "https://cmbg-leaderboard.wynstudio00.workers.dev";
        static string workingBase;   // the address that answered last (this session)
        public const string LocalUrl = "http://127.0.0.1:8787";
        // Same text as the Worker secret SUBMIT_KEY. Only friction against hand-made requests: a browser build can be read.
        const string SubmitKey = "REPLACE_WITH_YOUR_SUBMIT_KEY";

        public static string BaseUrl
        {
            get
            {
                var url = Application.absoluteURL;
                int i = string.IsNullOrEmpty(url) ? -1 : url.IndexOf("api=", StringComparison.Ordinal);
                if (i >= 0)
                {
                    var v = url.Substring(i + 4); int amp = v.IndexOf('&'); if (amp >= 0) v = v.Substring(0, amp);
                    return UnityWebRequest.UnEscapeURL(v).TrimEnd('/');
                }
                var pref = PlayerPrefs.GetString("lbApi", "");
                if (!string.IsNullOrEmpty(pref)) return pref.TrimEnd('/');
                return Application.isEditor ? LocalUrl : DefaultUrl;
            }
        }

        static string PlayerId => PlayerPrefs.GetString("lbPlayerId", "");
        static string Secret => PlayerPrefs.GetString("lbSecret", "");
        public static bool Registered => PlayerId.Length == 36 && Secret.Length == 64;
        static string PlayerName => PlayerPrefs.GetString("playerName", "GUEST CAT");

        // ------------------------------------------------------------------ JSON shapes (JsonUtility)
        [Serializable] class RegisterReq { public string name; }
        [Serializable] class RegisterRes { public bool ok; public string playerId, secret, name, error; public string[] suggestions; }
        [Serializable] public class NameCheck { public bool ok, available; public string name, reason; public string[] suggestions; }
        [Serializable] class StartReq { public string mode, wallet; }
        [Serializable] class StartRes { public bool ok; public string token, error; }
        [Serializable] public class RunResult
        {
            public string token, sig, character, build;
            public int kills, placement, waves, damage, seconds, matchSize;
            public bool dev;
        }
        [Serializable] public class FinishRes { public bool ok, counted; public int points, week, weekRank, weekScore; public string flag, error; }
        [Serializable] public class Row { public int rank, score, kills, matches; public string name, wallet; public bool you; }
        [Serializable] public class Board
        {
            public bool ok; public string mode, period, error; public int week; public double weekStart, weekEnd, serverTime;
            public Row[] rows; public Row me;
        }

        // ------------------------------------------------------------------ calls
        /// <summary>Make sure this device has a leaderboard player. done(true) when it has one.</summary>
        public static void EnsurePlayer(Action<bool> done)
        {
            if (Registered) { done?.Invoke(true); return; }
            Send("POST", "/v1/register", JsonUtility.ToJson(new RegisterReq { name = PlayerName }), false, (ok, text) =>
            {
                var r = ok ? Parse<RegisterRes>(text) : null;
                if (r != null && r.ok && !string.IsNullOrEmpty(r.playerId))
                {
                    PlayerPrefs.SetString("lbPlayerId", r.playerId);
                    PlayerPrefs.SetString("lbSecret", r.secret);
                    PlayerPrefs.Save();
                    done?.Invoke(true);
                }
                else
                {
                    // the name kept on this device (saved while offline) belongs to someone else: the lobby asks for a new one
                    if (r == null && (text ?? "").Contains("taken")) { PlayerPrefs.SetInt("nameConfirmed", 0); PlayerPrefs.SetInt("nameRejected", 1); PlayerPrefs.Save(); }
                    done?.Invoke(false);
                }
            });
        }

        /// <summary>Is this name free on the leaderboard? (look-alikes count as the same name: spaces, _, 0/O, 1/I).
        /// done(result, or null when the server is unreachable). Your own name counts as free.</summary>
        public static void CheckName(string name, Action<NameCheck> done)
        {
            Send("GET", "/v1/name/check?name=" + UnityWebRequest.EscapeURL(name ?? ""), null, Registered, (ok, text) =>
            {
                var r = ok ? Parse<NameCheck>(text) : null;
                done?.Invoke(r != null && r.ok ? r : null);
            });
        }

        /// <summary>Suggestions from the last "taken" answer of ClaimName (empty otherwise).</summary>
        public static string[] LastSuggestions { get; private set; } = new string[0];

        /// <summary>A match is starting: get a run token ("br" or "zombies") for this WALLET (2026-11-08: only matches played with
        /// a connected wallet are ranked; the board is one row per wallet). done(token or null).</summary>
        public static void StartRun(string mode, string wallet, Action<string> done)
        {
            if (!ChainCards.IsAddress(wallet)) { done?.Invoke(null); return; }
            EnsurePlayer(ok =>
            {
                if (!ok) { done?.Invoke(null); return; }
                Send("POST", "/v1/run/start", JsonUtility.ToJson(new StartReq { mode = mode, wallet = wallet }), true, (ok2, text) =>
                {
                    var r = ok2 ? Parse<StartRes>(text) : null;
                    done?.Invoke(r != null && r.ok ? r.token : null);
                });
            });
        }

        public static void FinishRun(string mode, RunResult run, Action<FinishRes> done)
        {
            run.sig = Hmac(string.Join("|", run.token, mode, run.kills, run.placement, run.waves, run.damage, run.seconds, run.matchSize));
            Send("POST", "/v1/run/finish", JsonUtility.ToJson(run), true, (ok, text) =>
            {
                var r = Parse<FinishRes>(text) ?? new FinishRes { ok = false, error = "no answer" };
                if (!ok && string.IsNullOrEmpty(r.error)) r.error = "no answer";
                done?.Invoke(r);
            });
        }

        /// <summary>mode "br" | "zombies", period "week" | "all". done(board, or null when the server is unreachable).</summary>
        public static void GetBoard(string mode, string period, Action<Board> done)
        {
            string w = WalletSession.Connected ? "&wallet=" + WalletSession.Address : "";   // marks "you" by the connected wallet
            Send("GET", $"/v1/leaderboard?mode={mode}&period={period}&limit=50{w}", null, Registered, (ok, text) =>
            {
                var b = ok ? Parse<Board>(text) : null;
                done?.Invoke(b != null && b.ok ? b : null);
            });
        }

        /// <summary>
        /// Reserve a unique name on the leaderboard (registers this device first if needed). done(result):
        /// "ok", "taken" (another player has it), "invalid" (refused by the name rules) or "offline" (no answer - the caller
        /// may keep the name locally; it goes with the next registration).
        /// </summary>
        public static void ClaimName(string name, Action<string> done)
        {
            void Result(bool ok, string text)
            {
                var r = Parse<RegisterRes>(text);
                LastSuggestions = r != null && r.suggestions != null ? r.suggestions : new string[0];
                if (ok && r != null && r.ok) { done?.Invoke("ok"); return; }
                string err = r != null ? r.error ?? "" : "";
                done?.Invoke(err.Contains("taken") ? "taken"
                    : string.IsNullOrEmpty(text) || err.Contains("too many") ? "offline" : "invalid");   // rate limit: like offline
            }
            if (Registered)
            {
                Send("POST", "/v1/name", JsonUtility.ToJson(new RegisterReq { name = name }), true, Result);
                return;
            }
            Send("POST", "/v1/register", JsonUtility.ToJson(new RegisterReq { name = name }), false, (ok, text) =>
            {
                var r = ok ? Parse<RegisterRes>(text) : null;
                if (r != null && r.ok && !string.IsNullOrEmpty(r.playerId))
                {
                    PlayerPrefs.SetString("lbPlayerId", r.playerId);
                    PlayerPrefs.SetString("lbSecret", r.secret);
                    PlayerPrefs.Save();
                }
                Result(ok, text);
            });
        }

        public static void Rename(string name)
        {
            if (!Registered) return;   // the name goes with the registration later
            Send("POST", "/v1/name", JsonUtility.ToJson(new RegisterReq { name = name }), true, null);
        }

        // ------------------------------------------------------------------ plumbing
        static T Parse<T>(string text) where T : class
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return JsonUtility.FromJson<T>(text); } catch { return null; }
        }

        static string Hmac(string message)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(SubmitKey)))
            {
                var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(message));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static void Send(string method, string path, string json, bool withAuth, Action<bool, string> done)
        {
            Runner.Get().StartCoroutine(SendRoutine(method, path, json, withAuth, done));
        }

        static IEnumerator SendRoutine(string method, string path, string json, bool withAuth, Action<bool, string> done)
        {
            var first = BaseUrl;
            bool canFall = first == DefaultUrl || first == FallbackUrl;
            if (canFall && workingBase != null) first = workingBase;
            bool retry = false;
            yield return SendOnce(first, method, path, json, withAuth, (ok, code, text) =>
            {
                // no answer at all, or a Cloudflare error page (e.g. "error code: 1042"): try the other address
                if (canFall && code == 0) { retry = true; return; }
                if (canFall && ok) workingBase = first;
                done?.Invoke(ok, text);
            });
            if (!retry) yield break;
            var second = first == DefaultUrl ? FallbackUrl : DefaultUrl;
            yield return SendOnce(second, method, path, json, withAuth, (ok, code, text) =>
            {
                if (ok) workingBase = second;
                done?.Invoke(ok, text);
            });
        }

        static IEnumerator SendOnce(string baseUrl, string method, string path, string json, bool withAuth, Action<bool, long, string> done)
        {
            using (var req = new UnityWebRequest(baseUrl + path, method))
            {
                if (json != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                if (withAuth && Registered) req.SetRequestHeader("Authorization", "Bearer " + PlayerId + "." + Secret);
                req.timeout = 12;
                yield return req.SendWebRequest();
                bool ok = req.result == UnityWebRequest.Result.Success;
                string text = req.downloadHandler != null ? req.downloadHandler.text : null;
                if (!ok) Debug.Log($"[Leaderboard] {method} {path}: {req.responseCode} {req.error} {text}");
                // a deleted / unknown player on the server: forget it so the next call registers again
                if (req.responseCode == 401 && withAuth) { PlayerPrefs.DeleteKey("lbPlayerId"); PlayerPrefs.DeleteKey("lbSecret"); }
                // a Cloudflare error page is not our API's answer: report it as code 0 / no text (= offline for the callers)
                bool cfError = !ok && (text ?? "").StartsWith("error code:");
                done?.Invoke(ok, cfError ? 0 : req.responseCode, cfError ? null : text);
            }
        }

        class Runner : MonoBehaviour
        {
            static Runner instance;
            public static Runner Get()
            {
                if (instance == null)
                {
                    instance = new GameObject("LeaderboardApi").AddComponent<Runner>();
                    DontDestroyOnLoad(instance.gameObject);
                }
                return instance;
            }
        }
    }
}
