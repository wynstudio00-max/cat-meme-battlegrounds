using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace CatBattleground
{
    /// <summary>
    /// Zombie Survival: endless waves around the village, server-authoritative, on the MatchSystem next to MatchManager.
    /// MatchManager calls Begin when the match starts playing and Stop when it ends; the match is over when every human
    /// cat is dead. Each wave has more and tougher zombies (runners from wave 2, tanks from wave 4, a boss every 5th wave);
    /// clearing a wave heals the survivors, refills some ammo and gives a short break.
    /// Score for the leaderboard later: waves cleared + kills (NetworkPlayer.Kills).
    /// </summary>
    public class ZombieWaveManager : NetworkBehaviour
    {
        public static ZombieWaveManager Instance { get; private set; }

        [SerializeField] GameObject[] zombiePrefabs;   // tinted cat bodies (ZombieSetup); a random one per zombie
        // 2026-10-26 (user: too few, too easy): bigger, faster waves, shorter breaks, every zombie runs (ZombieStats)
        [SerializeField] float firstBreak = 5f;
        [SerializeField] float breakSeconds = 8f;
        [SerializeField] float spawnMinDistance = 24f;
        [SerializeField] float spawnMaxDistance = 38f;
        [SerializeField] int maxAliveCap = 34;          // browser performance: never more zombies alive than this
        [SerializeField, Range(0f, 1f)] float dropChance = 0.1f;

        readonly NetworkVariable<int> wave = new NetworkVariable<int>(0);           // 0 = before the first wave
        readonly NetworkVariable<int> cleared = new NetworkVariable<int>(0);        // waves survived
        readonly NetworkVariable<int> remaining = new NetworkVariable<int>(0);      // zombies of this wave still to kill
        readonly NetworkVariable<float> breakTimer = new NetworkVariable<float>(0f); // seconds until the next wave (0 = fighting)
        readonly NetworkVariable<ulong> bossId = new NetworkVariable<ulong>(ulong.MaxValue);

        enum Phase { Idle, Break, Wave }
        Phase phase = Phase.Idle;
        float timer, spawnTimer;
        int toSpawn, spawnedThisWave, maxAlive;
        float spawnInterval, hpMultiplier, damageMultiplier;
        bool bossPending;
        Vector3 centre;
        readonly List<Zombie> alive = new List<Zombie>();

        public int Wave => wave.Value;
        public int WavesCleared => cleared.Value;
        public int Remaining => remaining.Value;
        public float BreakTimeLeft => breakTimer.Value;
        public bool InBreak => breakTimer.Value > 0f;
        /// <summary>The boss of this wave while it lives (client side too), else null.</summary>
        public Zombie Boss =>
            bossId.Value != ulong.MaxValue && NetworkManager != null && NetworkManager.SpawnManager != null &&
            NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(bossId.Value, out var obj) ? obj.GetComponent<Zombie>() : null;
        /// <summary>Where the human starts and the waves come from (centre of the village).</summary>
        public Vector3 Centre => centre;

        public override void OnNetworkSpawn() => Instance = this;
        public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }

        // ---------------------------------------------------------------- arena
        /// <summary>Start spot for Zombie Survival: the middle of the biggest cluster of houses (the village), on the NavMesh.</summary>
        public static Vector3 FindArenaCentre()
        {
            // the dedicated zombie town (ArenaBuilder.Zombie, 2026-10-28) when the scene has it
            var start = GameObject.Find("ZombieStart");
            if (start != null)
                return NavMesh.SamplePosition(start.transform.position, out var s, 6f, NavMesh.AllAreas) ? s.position : start.transform.position;

            var houses = new List<Vector3>();
            var buildings = GameObject.Find("Arena/Buildings");
            if (buildings != null)
                foreach (Transform c in buildings.transform)
                    if (c.name.StartsWith("house")) houses.Add(c.position);

            Vector3 best = Vector3.zero;
            int bestCount = 0;
            foreach (var h in houses)
            {
                Vector3 sum = Vector3.zero; int n = 0;
                foreach (var o in houses)
                    if ((o - h).sqrMagnitude < 35f * 35f) { sum += o; n++; }
                if (n > bestCount) { bestCount = n; best = sum / n; }
            }
            if (NavMesh.SamplePosition(best, out var hit, 25f, NavMesh.AllAreas)) best = hit.position;
            return best;
        }

        /// <summary>Server: a free spot near the centre for a human (spread around it a little).</summary>
        public Vector3 StartSpot(int index)
        {
            var p = centre + Quaternion.Euler(0f, index * 72f, 0f) * Vector3.forward * (index == 0 ? 0f : 3f);
            return NavMesh.SamplePosition(p, out var hit, 6f, NavMesh.AllAreas) ? hit.position + Vector3.up * 0.1f : centre + Vector3.up * 0.5f;
        }

        public void ServerPrepare()
        {
            if (!IsServer) return;
            Stop();
            centre = FindArenaCentre();
        }

        // ---------------------------------------------------------------- server loop
        public void Begin()
        {
            if (!IsServer) return;
            wave.Value = 0;
            cleared.Value = 0;
            remaining.Value = 0;
            StartBreak(firstBreak);
        }

        /// <summary>Server: the match ended; no more spawns or wave timers (the zombies stay for the results screen).</summary>
        public void ServerFreeze()
        {
            if (!IsServer) return;
            phase = Phase.Idle;
            breakTimer.Value = 0f;
        }

        /// <summary>Server: back to the lobby. Removes every zombie.</summary>
        public void Stop()
        {
            if (!IsServer) return;
            phase = Phase.Idle;
            breakTimer.Value = 0f;
            bossId.Value = ulong.MaxValue;
            foreach (var z in alive)
                if (z != null && z.IsSpawned) z.NetworkObject.Despawn(true);
            alive.Clear();
            StopAllCoroutines();
            foreach (var z in FindObjectsByType<Zombie>())   // corpses waiting for their despawn
                if (z.IsSpawned) z.NetworkObject.Despawn(true);
        }

        void Update()
        {
            if (!IsServer || !IsSpawned || phase == Phase.Idle) return;
            var match = MatchManager.Instance;
            if (match == null || match.State != MatchState.Playing) return;

            if (phase == Phase.Break)
            {
                timer -= Time.deltaTime;
                if (Mathf.Abs(breakTimer.Value - timer) > 0.2f) breakTimer.Value = Mathf.Max(0.01f, timer);
                if (timer <= 0f) StartWave(wave.Value + 1);
                return;
            }

            alive.RemoveAll(z => z == null || !z.IsSpawned || z.Health.IsDead);
            spawnTimer -= Time.deltaTime;
            if (spawnTimer <= 0f && alive.Count < maxAlive)
            {
                if (bossPending) { bossPending = false; Spawn(ZombieKind.Boss); spawnTimer = 2f; }
                else if (spawnedThisWave < toSpawn) { Spawn(PickKind(wave.Value)); spawnedThisWave++; spawnTimer = spawnInterval; }
            }
            if (remaining.Value <= 0 && spawnedThisWave >= toSpawn && !bossPending) WaveCleared();
        }

        void StartBreak(float seconds)
        {
            phase = Phase.Break;
            timer = seconds;
            breakTimer.Value = seconds;
        }

        void StartWave(int n)
        {
            phase = Phase.Wave;
            wave.Value = n;
            breakTimer.Value = 0f;
            // 2026-10-28 (user: a few less, they are tougher now): wave 1: 11 zombies, 15 at once; wave 5: 31 / 27; wave 10: 59 / 34 (cap)
            toSpawn = Mathf.Min(11 + 5 * (n - 1) + Mathf.Max(0, n - 8) * 2, 130);
            maxAlive = Mathf.Min(13 + 3 * n, GameInput.TouchMode ? Mathf.Min(maxAliveCap, MobileProfile.ZombieAliveCap) : maxAliveCap);
            spawnInterval = Mathf.Max(0.2f, 0.9f - 0.06f * n);
            hpMultiplier = 1f + 0.12f * (n - 1);
            damageMultiplier = 1f + 0.05f * (n - 1);
            bossPending = n % 5 == 0;
            spawnedThisWave = 0;
            spawnTimer = 1f;
            remaining.Value = toSpawn + (bossPending ? 1 : 0);
            WaveRpc(n, bossPending);
        }

        void WaveCleared()
        {
            cleared.Value = wave.Value;
            bossId.Value = ulong.MaxValue;
            // reward: survivors patch up and get spare rounds for the next wave
            var match = MatchManager.Instance;
            if (match != null)
                foreach (var p in match.Players)
                {
                    if (p == null || p.IsBot || p.Health.IsDead) continue;
                    p.GetComponent<NetworkPlayerHealth>().ServerHeal(30f);
                    var shooter = p.GetComponent<PlayerShooter>();
                    shooter.ServerAddAmmo(shooter.Weapon.magazine * 2);
                    var thrower = p.GetComponent<BombThrower>();
                    if (thrower != null) thrower.ServerAddBombs(1);
                }
            ClearedRpc(wave.Value);
            StartBreak(breakSeconds);
        }

        // Everybody runs; "runners" are the small fast ones (from wave 1), tanks from wave 3, both more often every wave.
        static ZombieKind PickKind(int n)
        {
            float runner = Mathf.Min(0.45f, 0.12f + 0.06f * (n - 1));
            float tank = n >= 3 ? Mathf.Min(0.22f, 0.05f * (n - 2)) : 0f;
            float r = Random.value;
            return r < tank ? ZombieKind.Tank : r < tank + runner ? ZombieKind.Runner : ZombieKind.Walker;
        }

        void Spawn(ZombieKind kind)
        {
            if (zombiePrefabs == null || zombiePrefabs.Length == 0) { Debug.LogWarning("ZombieWaveManager: no zombie prefabs (run Build Phase 2 Multiplayer Scene)."); remaining.Value--; return; }
            var prefab = zombiePrefabs[Random.Range(0, zombiePrefabs.Length)];
            if (prefab == null) { remaining.Value--; return; }
            var pos = SpawnPosition();
            var toCentre = centre - pos; toCentre.y = 0f;
            float yaw = toCentre.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toCentre).eulerAngles.y : 0f;

            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            var zombie = go.GetComponent<Zombie>();
            zombie.NetworkObject.Spawn(true);
            zombie.ServerSetup(kind, hpMultiplier, pos, yaw, damageMultiplier);
            alive.Add(zombie);
            if (kind == ZombieKind.Boss) bossId.Value = zombie.NetworkObjectId;
        }

        NavMeshPath pathBuffer;
        NavMeshPath PathCheck => pathBuffer ??= new NavMeshPath();

        // A ring around a living cat, out of its immediate sight range, on the NavMesh - and only where a full path to that cat
        // exists (2026-10-27: zombies used to rise on roofs / upper floors of other houses, cut off from the cats, and just stood
        // there, so a long wave meant hunting for the last ones).
        Vector3 SpawnPosition()
        {
            var match = MatchManager.Instance;
            var humans = new List<Vector3>();
            if (match != null)
                foreach (var p in match.Players)
                    if (p != null && !p.IsBot && !p.Health.IsDead) humans.Add(p.transform.position);
            Vector3 around = humans.Count > 0 ? humans[Random.Range(0, humans.Count)] : centre;
            bool aroundOnMesh = NavMesh.SamplePosition(around, out var aroundHit, 4f, NavMesh.AllAreas);

            for (int attempt = 0; attempt < 24; attempt++)
            {
                float a = Random.value * Mathf.PI * 2f;
                var p = around + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * Random.Range(spawnMinDistance, spawnMaxDistance);
                if (!NavMesh.SamplePosition(p, out var hit, 8f, NavMesh.AllAreas)) continue;
                bool tooClose = false;
                foreach (var h in humans) if ((hit.position - h).sqrMagnitude < 16f * 16f) { tooClose = true; break; }
                if (tooClose) continue;
                if (aroundOnMesh && (!NavMesh.CalculatePath(hit.position, aroundHit.position, NavMesh.AllAreas, PathCheck)
                                     || PathCheck.status != NavMeshPathStatus.PathComplete)) continue;   // cut off from the cat
                return hit.position;
            }
            var fallback = NetworkBootstrap.GetSpawnPoint().position;
            return NavMesh.SamplePosition(fallback, out var fb, 10f, NavMesh.AllAreas) ? fb.position : fallback;
        }

        /// <summary>Server (from Zombie): a zombie that is stuck or wandered far off rises again near a cat.</summary>
        public void ServerRelocate(Zombie zombie)
        {
            if (!IsServer || zombie == null || phase != Phase.Wave) return;
            var pos = SpawnPosition();
            var toCentre = centre - pos; toCentre.y = 0f;
            zombie.ServerTeleport(pos, toCentre.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toCentre).eulerAngles.y : 0f);
        }

        // ---------------------------------------------------------------- kills
        /// <summary>Server (from Zombie): credit the kill, maybe drop loot, remove the body after a while.</summary>
        public void ServerOnZombieDied(Zombie zombie, GameObject attacker)
        {
            if (!IsServer) return;
            alive.Remove(zombie);
            if (phase == Phase.Wave) remaining.Value = Mathf.Max(0, remaining.Value - 1);
            var killer = attacker != null ? attacker.GetComponentInParent<NetworkPlayer>() : null;
            if (killer != null) killer.ServerAddKill();
            if (zombie.Kind == ZombieKind.Boss) bossId.Value = ulong.MaxValue;

            var loot = GetComponent<LootSpawner>();
            if (loot != null)
            {
                var at = zombie.transform.position + Vector3.up * 0.1f;
                if (zombie.Kind == ZombieKind.Boss) { loot.SpawnDrop(at, LootType.Armor); loot.SpawnDrop(at + Vector3.right * 1.5f, LootType.Medkit); }
                else if (Random.value < (zombie.Kind == ZombieKind.Tank ? dropChance * 3f : dropChance)) loot.SpawnDrop(at);
            }
            StartCoroutine(RemoveBody(zombie, 4f));
        }

        IEnumerator RemoveBody(Zombie zombie, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (zombie != null && zombie.IsSpawned) zombie.NetworkObject.Despawn(true);
        }

        // ---------------------------------------------------------------- announcements (every machine)
        static readonly Color Toxic = new Color(0.55f, 1f, 0.35f), Blood = new Color(1f, 0.3f, 0.25f);

        [Rpc(SendTo.Everyone)]
        void WaveRpc(int n, bool boss)
        {
            if (boss) Announcer.Show("WAVE " + n + " · BOSS INCOMING!", "A giant zombie is coming", Blood, "announce_alarm", 2.6f);
            else Announcer.Show("WAVE " + n, n == 1 ? "Here they come - hold the village" : "They are coming", Toxic, "announce_alarm", 2f);
        }

        [Rpc(SendTo.Everyone)]
        void ClearedRpc(int n) => Announcer.Show("WAVE " + n + " CLEARED", "+30 HP · +2 magazines · +1 bomb", Toxic, "announce", 2f);
    }
}
