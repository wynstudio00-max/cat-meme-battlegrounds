using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Sends this device's match results to the leaderboard (2026-10-28, offline beta: the host is the only human, so it reports for
    /// itself). Asks for a run token when the match goes to Playing, and reports once when the local cat is out (battle royale:
    /// it got a placement; zombies: it died) or the match ended. Leaving a match early reports nothing.
    /// Results for the HUD: <see cref="LastText"/> (e.g. "+130 PTS · WEEK RANK #4").
    /// </summary>
    public class LeaderboardReporter : MonoBehaviour
    {
        static LeaderboardReporter instance;
        /// <summary>One line for the eliminated / results screens; empty while nothing to say.</summary>
        public static string LastText { get; private set; } = "";

        MatchState lastState = MatchState.Waiting;
        string mode, token;
        float startTime;
        bool waitingToken, reported;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isBatchMode || instance != null) return;
            if (FindAnyObjectByType<NetworkBootstrap>() == null) return;
            instance = new GameObject("LeaderboardReporter").AddComponent<LeaderboardReporter>();
        }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            var match = MatchManager.Instance;
            if (nm == null || match == null || !nm.IsHost) { lastState = MatchState.Waiting; return; }   // only the offline / editor host reports
            var me = nm.LocalClient != null && nm.LocalClient.PlayerObject != null ? nm.LocalClient.PlayerObject.GetComponent<NetworkPlayer>() : null;
            var state = match.State;

            if (state != lastState)
            {
                if (state == MatchState.Countdown || state == MatchState.Waiting) { token = null; reported = false; waitingToken = false; mode = null; LastText = ""; }
                if (state == MatchState.Playing && lastState != MatchState.Playing && match.HumanCount <= 1) Begin(match);
                lastState = state;
            }
            if (me == null || reported || mode == null) return;

            bool done = match.IsZombies ? (me.Health.IsDead || state == MatchState.Ended)
                                        : (me.Placement > 0 || state == MatchState.Ended);
            if (done) Report(match, me);
        }

        /// <summary>Shown when a match is played without a wallet (2026-11-08: the leaderboard is for connected wallets only).</summary>
        public const string NoWalletText = "CONNECT YOUR WALLET TO RANK ON THE LEADERBOARD";

        void Begin(MatchManager match)
        {
            mode = match.IsZombies ? "zombies" : "br";
            startTime = Time.realtimeSinceStartup;
            LastText = "";
            // guests play unranked: no token, nothing is sent
            if (!WalletSession.Connected) { mode = null; LastText = NoWalletText; return; }
            waitingToken = true;
            string forMode = mode;
            LeaderboardApi.StartRun(forMode, WalletSession.Address, t =>
            {
                waitingToken = false;
                if (forMode != mode) return;
                token = t;
                if (t == null) LastText = "LEADERBOARD OFFLINE · NOT RANKED";
            });
        }

        void Report(MatchManager match, NetworkPlayer me)
        {
            reported = true;
            if (token == null)
            {
                if (waitingToken) LastText = "LEADERBOARD OFFLINE · NOT RANKED";
                return;
            }
            var run = new LeaderboardApi.RunResult
            {
                token = token,
                kills = me.Kills,
                placement = match.IsZombies ? 0 : me.Placement,
                waves = match.IsZombies && match.Waves != null ? match.Waves.WavesCleared : 0,
                damage = me.Damage,
                seconds = Mathf.RoundToInt(Time.realtimeSinceStartup - startTime),
                matchSize = match.IsZombies ? 1 : match.MatchSize,
                character = me.CharacterId,
                build = Application.version,
                dev = Application.isEditor || Debug.isDebugBuild,
            };
            token = null;
            LastText = "SENDING SCORE…";
            LeaderboardApi.FinishRun(mode, run, r =>
            {
                if (r == null || !r.ok) { LastText = "LEADERBOARD OFFLINE · NOT RANKED"; return; }
                string pts = mode == "zombies" ? "WAVE " + run.waves + " · " + run.kills + " KILLS" : "+" + r.points + " PTS";
                LastText = r.counted ? pts + (r.weekRank > 0 ? " · WEEK RANK #" + r.weekRank : "") : pts + " · NOT RANKED (under review)";
            });
        }
    }
}
