using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    public enum MatchState { Waiting = 0, Countdown = 1, Playing = 2, Ended = 3 }

    public enum TeamMode { Solo = 1, Duo = 2, Squad = 4 }

    /// <summary>BattleRoyale = drop ship, zone, last cat standing. Zombies = start in the village, survive endless waves.</summary>
    public enum MatchMode { BattleRoyale = 0, Zombies = 1 }

    /// <summary>
    /// Server-authoritative battle-royale loop with matchmaking:
    ///   Waiting   - first player joins, a 60 s search starts. A full lobby (10 players) starts at once.
    ///   Countdown - if the lobby is not full, the free slots are filled with bots. Everyone boards the drop ship.
    ///   Playing   - the ship flies over the island and everyone parachutes down with their own fixed weapon; when the
    ///               ship has passed, the zone starts shrinking. Last cat standing wins (solo only for now).
    ///   Ended     - winner shown, bots removed, back to Waiting.
    /// Zombie Survival (<see cref="MatchMode.Zombies"/>): no bots, no ship, no zone; the humans start in the village and
    /// <see cref="ZombieWaveManager"/> sends waves until every human is dead.
    /// Bots are normal player prefabs driven by <see cref="BotBrain"/> on the server; clients cannot tell them apart.
    /// Spawned at runtime by NetworkBootstrap when the server starts.
    /// </summary>
    [RequireComponent(typeof(ZoneController), typeof(LootSpawner))]
    public class MatchManager : NetworkBehaviour
    {
        public static MatchManager Instance { get; private set; }

        [SerializeField] CharacterCatalog catalog;                 // bots pick their cat from here
        [SerializeField] int matchSize = 10;                       // humans + bots
        [SerializeField] float searchMinSeconds = 38f;             // matchmaking counts up from 0; once it reaches a random time in this range
        [SerializeField] float searchMaxSeconds = 44f;             // and nobody else joined, bots fill the empty slots
        [SerializeField] float countdownSeconds = 10f;
        [Tooltip("Testing: start the match ~1 s after the first player joins (bots fill the rest) with a 3 s boarding countdown. Turn off for the real 38-44 s matchmaking.")]
        [SerializeField] bool quickStart = true;
        [SerializeField] float endScreenSeconds = 15f;
        [SerializeField] float arenaRadius = 110f;                 // playable island radius (drop ship path and parachute clamp)

        public const float ShipSpeed = 12f;                        // m/s
        public const float ShipAltitude = 120f;                    // m above sea level

        readonly NetworkVariable<int> state = new NetworkVariable<int>((int)MatchState.Waiting);
        readonly NetworkVariable<float> timer = new NetworkVariable<float>(0f);          // seconds left in the current state (where relevant)
        readonly NetworkVariable<int> aliveCount = new NetworkVariable<int>(0);
        readonly NetworkVariable<int> humanCount = new NetworkVariable<int>(0);
        readonly NetworkVariable<FixedString64Bytes> resultText = new NetworkVariable<FixedString64Bytes>();
        readonly NetworkVariable<Vector3> shipStart = new NetworkVariable<Vector3>();
        readonly NetworkVariable<Vector3> shipDir = new NetworkVariable<Vector3>(Vector3.forward);
        readonly NetworkVariable<double> flightStart = new NetworkVariable<double>(-1.0);   // server time the ship left, -1 = still boarding
                                                                                             // (zombie mode: when the match started)
        readonly NetworkVariable<int> mode = new NetworkVariable<int>((int)MatchMode.BattleRoyale);
        readonly NetworkVariable<int> nameSeed = new NetworkVariable<int>(1);   // made-up player names for this match (PlayerNames)
        /// <summary>Seed of this match's made-up player names: <c>PlayerNames.Of(player.Number, NameSeed)</c>.</summary>
        public int NameSeed => nameSeed.Value;

        ZoneController zone;
        LootSpawner loot;
        ZombieWaveManager waves;
        float stateTimer;
        float lobbyTimer;      // seconds searched so far (counts up)
        float lobbyTarget;     // when the search gives up waiting for more humans
        float evalTimer;
        float syncTimer;
        bool lobbyRunning;
        bool dropFinished;
        DropShipVisual shipVisual;
        readonly List<NetworkPlayer> players = new List<NetworkPlayer>();   // humans + bots, server side
        readonly List<NetworkObject> bots = new List<NetworkObject>();
        readonly Dictionary<NetworkPlayer, int> anonymousNumber = new Dictionary<NetworkPlayer, int>();

        // ---------------------------------------------------------------- drop ship (same maths on every machine)
        public float ArenaRadius => arenaRadius;
        float FlightLength => (arenaRadius + 50f) * 2f;
        public float FlightDuration => FlightLength / ShipSpeed;
        public float FlightElapsed => flightStart.Value < 0.0 || NetworkManager == null ? 0f : (float)(NetworkManager.ServerTime.Time - flightStart.Value);
        // The ship may be jumped from only while it is over the island (the route is centred on it).
        float JumpWindowStart => (FlightLength * 0.5f - arenaRadius * 0.9f) / ShipSpeed;
        float JumpWindowEnd => (FlightLength * 0.5f + arenaRadius * 0.9f) / ShipSpeed;
        /// <summary>0..1 across the time the ship is over the island (clamped).</summary>
        public float JumpWindowFraction => Mathf.Clamp01((FlightElapsed - JumpWindowStart) / (JumpWindowEnd - JumpWindowStart));
        /// <summary>True while players may jump.</summary>
        public bool CanJump => IsBattleRoyale && State == MatchState.Playing && flightStart.Value >= 0.0 && FlightElapsed >= JumpWindowStart && FlightElapsed <= JumpWindowEnd;
        public bool ShipVisible => IsBattleRoyale && (State == MatchState.Countdown || (State == MatchState.Playing && FlightElapsed < FlightDuration + 2f));

        public MatchMode GameMode => (MatchMode)mode.Value;
        public bool IsBattleRoyale => GameMode == MatchMode.BattleRoyale;
        public bool IsZombies => GameMode == MatchMode.Zombies && waves != null;
        public ZombieWaveManager Waves => waves;
        public Vector3 ShipDirection => shipDir.Value;
        public Vector3 ShipPosition => shipStart.Value + shipDir.Value * (ShipSpeed * Mathf.Clamp(FlightElapsed, 0f, FlightDuration));

        public MatchState State => (MatchState)state.Value;
        public float Timer => timer.Value;
        public int AliveCount => aliveCount.Value;
        public int HumanCount => humanCount.Value;
        public int MatchSize => matchSize;
        public string ResultText => resultText.Value.ToString();
        /// <summary>Only solo is open for now; duo and squad are locked.</summary>
        public TeamMode Mode => TeamMode.Solo;
        public ZoneController Zone => zone;
        public IReadOnlyList<NetworkPlayer> Players => players;   // server side

        /// <summary>Movement and shooting are frozen during the countdown.</summary>
        public bool InputLocked => State == MatchState.Countdown;
        public bool AcceptingPlayers => State == MatchState.Waiting;

        void Awake()
        {
            zone = GetComponent<ZoneController>();
            loot = GetComponent<LootSpawner>();
            waves = GetComponent<ZombieWaveManager>();   // null on an old MatchSystem prefab: zombie mode then falls back to battle royale
        }

        // Server: the host's lobby pick (NetworkBootstrap.SelectedMode); a dedicated server stays battle royale for now.
        void PickMode()
        {
            var nb = NetworkBootstrap.Instance;
            var wanted = nb != null && IsHost ? nb.SelectedMode : MatchMode.BattleRoyale;
            if (wanted == MatchMode.Zombies && waves == null) wanted = MatchMode.BattleRoyale;
            mode.Value = (int)wanted;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!Application.isBatchMode && shipVisual == null) shipVisual = new GameObject("DropShip").AddComponent<DropShipVisual>();
            if (!IsServer) return;
            PickMode();
            ResetSearch();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
            if (shipVisual != null) Destroy(shipVisual.gameObject);
        }

        void SetTimer(float value)
        {
            // Only replicate visible changes (about 5 per second) instead of every frame.
            if (Mathf.Abs(timer.Value - value) > 0.2f || value == 0f) timer.Value = value;
        }

        // ---------------------------------------------------------------- server loop
        void Update()
        {
            if (!IsServer || !IsSpawned) return;

            CollectPlayers();
            int humans = 0;
            foreach (var p in players) if (!p.IsBot) humans++;

            evalTimer += Time.deltaTime;
            bool evaluate = evalTimer >= 0.5f;
            if (evaluate) evalTimer = 0f;

            // Everybody left mid-match: clean up straight away instead of letting bots play alone.
            if (State != MatchState.Waiting && humans == 0) { ResetToLobby(); return; }

            switch (State)
            {
                case MatchState.Waiting: TickWaiting(humans); break;
                case MatchState.Countdown: TickCountdown(); break;
                case MatchState.Playing: if (IsBattleRoyale) TickDrop(); if (evaluate) TickPlaying(); break;
                case MatchState.Ended: TickEnded(); break;
            }

            syncTimer += Time.deltaTime;
            if (syncTimer >= 0.2f)
            {
                syncTimer = 0f;
                humanCount.Value = humans;
                int alive = 0;
                foreach (var p in players) if (!p.Health.IsDead) alive++;
                aliveCount.Value = alive;
            }
        }

        /// <summary>Host / server button: stop searching and start the countdown now (empty slots are filled with bots).</summary>
        public void StartNow()
        {
            if (!IsServer || State != MatchState.Waiting) return;
            CollectPlayers();
            if (players.Count == 0) return;
            EnterCountdown();
        }

        // Matchmaking looks like a real queue: it counts up from 0 and ends at a slightly different time every match.
        void ResetSearch()
        {
            lobbyTimer = 0f;
            lobbyTarget = quickStart || IsZombies ? 1f : Random.Range(searchMinSeconds, searchMaxSeconds);   // zombies: solo, nobody to wait for
        }

        void TickWaiting(int humans)
        {
            if (humans == 0)
            {
                lobbyRunning = false;
                ResetSearch();
                SetTimer(0f);
                return;
            }
            lobbyRunning = true;
            lobbyTimer += Time.deltaTime;
            SetTimer(lobbyTimer);
            if (humans >= matchSize || lobbyTimer >= lobbyTarget) EnterCountdown();   // nobody else came: bots take the empty slots
        }

        void EnterCountdown()
        {
            state.Value = (int)MatchState.Countdown;
            stateTimer = quickStart ? 3f : countdownSeconds;
            SetTimer(stateTimer);
            resultText.Value = "";
            lobbyRunning = false;

            if (IsZombies)
            {
                // Everyone on foot in the village; no bots, no ship.
                waves.ServerPrepare();
                for (int i = 0; i < players.Count; i++) ResetPlayer(players[i], waves.StartSpot(i), Random.Range(0f, 360f));
                loot.DespawnAll();
                AssignTeams();
                flightStart.Value = -1.0;
                return;
            }

            // Humans first, then bots fill every free slot up to the match size.
            RefreshSpawnOrder();
            foreach (var p in players) ResetPlayer(p);
            loot.DespawnAll();
            FillWithBots(matchSize - players.Count);
            CollectPlayers();
            AssignTeams();
            PlanShip();
            foreach (var p in players) p.GetComponent<PlayerDrop>().ServerBoard();
        }

        // The ship crosses the island along a random line, a little off-centre so the route differs every match.
        void PlanShip()
        {
            float angle = Random.value * Mathf.PI * 2f;
            var dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            var side = new Vector3(dir.z, 0f, -dir.x);
            var start = -dir * (arenaRadius + 50f) + side * Random.Range(-arenaRadius * 0.2f, arenaRadius * 0.2f);
            start.y = ShipAltitude;
            shipStart.Value = start;
            shipDir.Value = dir;
            flightStart.Value = -1.0;
            dropFinished = false;
        }

        void TickCountdown()
        {
            stateTimer -= Time.deltaTime;
            SetTimer(Mathf.Max(0f, stateTimer));
            if (stateTimer > 0f) return;

            state.Value = (int)MatchState.Playing;
            flightStart.Value = NetworkManager.ServerTime.Time;   // the ship takes off, the zone starts once it has passed
            loot.SpawnAll();
            if (IsZombies) waves.Begin();
        }

        // Runs every frame while playing: when the ship reaches the end of its route everyone still aboard is dropped and the zone begins.
        void TickDrop()
        {
            if (dropFinished || flightStart.Value < 0.0 || FlightElapsed < JumpWindowEnd) return;
            dropFinished = true;
            foreach (var p in players) p.GetComponent<PlayerDrop>().ServerForceJump();
            zone.StartZone();
        }

        void TickPlaying()
        {
            if (IsZombies)
            {
                // Survival: over when no human cat is left standing.
                int kills = 0;
                foreach (var p in players)
                {
                    if (p.IsBot) continue;
                    if (!p.Health.IsDead) return;
                    kills += p.Kills;
                }
                int survived = waves.WavesCleared;
                waves.ServerFreeze();
                EndMatch("Survived " + survived + (survived == 1 ? " wave" : " waves") + " · " + kills + " kills");
                return;
            }

            NetworkPlayer lastAlive = null;
            int alive = 0;
            foreach (var p in players)
            {
                if (p.Health.IsDead) continue;
                alive++;
                lastAlive = p;
            }

            // Last cat standing wins. A one-player test match (-minplayers style) ends when that player dies.
            if (alive > 1) return;
            if (lastAlive != null) lastAlive.ServerSetOut(1, -1, -1, FlightElapsed);
            EndMatch(alive == 1 ? PlayerNames.Of(AnonymousNumber(lastAlive), nameSeed.Value) + " wins!" : "Nobody survived");
        }

        void EndMatch(string result)
        {
            state.Value = (int)MatchState.Ended;
            stateTimer = endScreenSeconds;
            SetTimer(stateTimer);
            resultText.Value = result;
            zone.StopZone();
        }

        void TickEnded()
        {
            stateTimer -= Time.deltaTime;
            SetTimer(Mathf.Max(0f, stateTimer));
            if (stateTimer <= 0f) ResetToLobby();
        }

        void ResetToLobby()
        {
            zone.StopZone();
            flightStart.Value = -1.0;
            loot.DespawnAll();
            if (waves != null) waves.Stop();
            DespawnBots();
            CollectPlayers();
            RefreshSpawnOrder();
            foreach (var p in players) ResetPlayer(p);
            state.Value = (int)MatchState.Waiting;
            PickMode();
            ResetSearch();
            SetTimer(0f);
            resultText.Value = "";
        }

        // ---------------------------------------------------------------- players and bots
        void CollectPlayers()
        {
            players.Clear();
            foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;
                var p = client.PlayerObject.GetComponent<NetworkPlayer>();
                if (p != null) players.Add(p);
            }
            bots.RemoveAll(b => b == null);
            foreach (var bot in bots)
            {
                var p = bot.GetComponent<NetworkPlayer>();
                if (p != null) players.Add(p);
            }
        }

        void FillWithBots(int count)
        {
            if (catalog == null || count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                var entry = PickBotCharacter();
                if (entry == null || entry.playerPrefab == null) continue;

                var spawn = NetworkBootstrap.GetSpawnPoint();
                var go = Instantiate(entry.playerPrefab, spawn.position, spawn.rotation);
                var brain = go.AddComponent<BotBrain>();   // before Spawn: NetworkPlayer/PlayerShooter look for it on spawn
                brain.Init();
                var net = go.GetComponent<NetworkObject>();
                net.Spawn();
                go.GetComponent<NetworkPlayer>().ServerSetCharacter(entry.id, entry.tier);
                brain.Place(spawn.position, spawn.eulerAngles.y);
                bots.Add(net);
            }
        }

        // Mostly the free characters, now and then a rarer one, so a lobby looks like a believable mix of players.
        CharacterCatalog.Entry PickBotCharacter()
        {
            float r = Random.value;
            CharacterTier tier = r < 0.40f ? CharacterTier.Original : r < 0.80f ? CharacterTier.Common : r < 0.95f ? CharacterTier.Rare : CharacterTier.Legendary;
            var pool = catalog.InTier(tier);
            if (pool.Count == 0) pool = catalog.InTier(CharacterTier.Common);
            if (pool.Count == 0) pool = catalog.entries.FindAll(e => e.playerPrefab != null);
            return pool.Count == 0 ? null : pool[Random.Range(0, pool.Count)];
        }

        void DespawnBots()
        {
            foreach (var bot in bots)
                if (bot != null && bot.IsSpawned) bot.Despawn(true);
            bots.Clear();
        }

        void AssignTeams()
        {
            // Solo: everybody is their own team.
            for (int i = 0; i < players.Count; i++) players[i].ServerSetTeam(i);
            anonymousNumber.Clear();
            nameSeed.Value = Random.Range(1, int.MaxValue);   // new names every match

            // Anonymous numbers for the winner banner, shuffled so a bot cannot be told from a person by its number.
            var numbers = new List<int>();
            for (int i = 1; i <= players.Count; i++) numbers.Add(i);
            for (int i = numbers.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (numbers[i], numbers[j]) = (numbers[j], numbers[i]);
            }
            for (int i = 0; i < players.Count; i++)
            {
                anonymousNumber[players[i]] = numbers[i];
                players[i].ServerSetNumber(numbers[i]);
                players[i].ServerResetStats();
            }
        }

        /// <summary>Kill feed for the HUD: killer number (0 = the zone), victim number, the killer's weapon id (-1 = none).</summary>
        public static event System.Action<int, int, int> KillFeed;

        [Rpc(SendTo.Everyone)]
        void KillFeedRpc(int killer, int victim, int weapon) => KillFeed?.Invoke(killer, victim, weapon);

        /// <summary>Server: a cat was eliminated during the match (called from NetworkPlayerHealth).</summary>
        public void ServerOnPlayerDied(NetworkPlayer victim, GameObject attacker)
        {
            if (!IsServer || State != MatchState.Playing || victim == null) return;
            if (IsZombies)
            {
                victim.ServerSetOut(0, -1, -1, FlightElapsed);   // no placement in survival; the HUD shows waves instead
                return;
            }
            CollectPlayers();
            int alive = 0;
            foreach (var p in players) if (!p.Health.IsDead) alive++;
            var killer = attacker != null ? attacker.GetComponentInParent<NetworkPlayer>() : null;
            if (killer == victim) killer = null;
            int weapon = killer != null ? killer.GetComponent<PlayerShooter>().WeaponId : -1;
            if (killer != null) killer.ServerAddKill();
            int killerNumber = killer != null ? AnonymousNumber(killer) : 0;
            victim.ServerSetOut(alive + 1, killerNumber, weapon, FlightElapsed);
            KillFeedRpc(killerNumber, AnonymousNumber(victim), weapon);
        }

        /// <summary>Server: damage dealt by a player during the match (for the end screens).</summary>
        public void ServerOnDamage(NetworkPlayer victim, GameObject attacker, float amount)
        {
            if (!IsServer || State != MatchState.Playing || attacker == null) return;
            var shooter = attacker.GetComponentInParent<NetworkPlayer>();
            if (shooter != null && shooter != victim) shooter.ServerAddDamage(amount);
        }

        /// <summary>Seconds the ship still flies over the island (jump allowed); negative before the island is reached.</summary>
        public float JumpTimeLeft => JumpWindowEnd - FlightElapsed;
        public float TimeUntilJumpZone => JumpWindowStart - FlightElapsed;

        int AnonymousNumber(NetworkPlayer p) => p != null && anonymousNumber.TryGetValue(p, out var n) ? n : 0;

        void RefreshSpawnOrder()
        {
            // Make spawn rotation start from a random point so repeated matches do not always put the same cat in the same place.
            for (int i = Random.Range(0, 16); i > 0; i--) NetworkBootstrap.GetSpawnPoint();
        }

        void ResetPlayer(NetworkPlayer p)
        {
            var spawn = NetworkBootstrap.GetSpawnPoint();
            ResetPlayer(p, spawn.position, spawn.eulerAngles.y);
        }

        void ResetPlayer(NetworkPlayer p, Vector3 position, float yaw)
        {
            p.GetComponent<PlayerDrop>().ServerReset();
            p.GetComponent<NetworkPlayerHealth>().ServerReviveAt(position, yaw);
            p.GetComponent<PlayerShooter>().ServerResetLoadout();
            var thrower = p.GetComponent<BombThrower>();
            if (thrower != null) thrower.ServerResetBombs();
        }
    }
}
