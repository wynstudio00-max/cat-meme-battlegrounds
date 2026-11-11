using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace CatBattleground
{
    public enum ZombieKind : byte { Walker = 0, Runner = 1, Tank = 2, Boss = 3 }

    /// <summary>Numbers of one zombie kind (hit points before the wave multiplier, m/s, damage per swipe).
    /// gait = the zombie cat's loop (1 shamble walk, 2 run); screamChance = roars when it appears.
    /// 2026-10-26 (user: too easy, make them all run): every kind runs (Mixamo zombie run, 1.9 m/s at rate 1). The player walks 1.4
    /// and sprints 4.0 m/s, so walkers (2.7) catch a walking cat and runners (3.4) are almost as fast as a sprint.</summary>
    public struct ZombieStats
    {
        public float hp, speed, damage, reach, windup, interval, scale, screamChance;
        public int gait;

        public static ZombieStats Of(ZombieKind kind)
        {
            switch (kind)
            {
                case ZombieKind.Runner: return new ZombieStats { hp = 45f, speed = 3.4f, damage = 10f, reach = 1.2f, windup = 0.4f, interval = 0.9f, scale = 0.9f, gait = 2, screamChance = 0f };
                case ZombieKind.Tank: return new ZombieStats { hp = 320f, speed = 2.0f, damage = 28f, reach = 1.6f, windup = 0.7f, interval = 1.6f, scale = 1.4f, gait = 2, screamChance = 1f };
                case ZombieKind.Boss: return new ZombieStats { hp = 1500f, speed = 2.2f, damage = 42f, reach = 2.2f, windup = 0.8f, interval = 1.8f, scale = 2.0f, gait = 2, screamChance = 1f };
                default: return new ZombieStats { hp = 70f, speed = 2.7f, damage = 14f, reach = 1.3f, windup = 0.5f, interval = 1.2f, scale = 1f, gait = 2, screamChance = 0.15f };
            }
        }
    }

    /// <summary>
    /// One zombie of the Zombie Survival mode. Server-authoritative: the server walks it with a NavMeshAgent toward the
    /// nearest living (human) cat and swipes when in reach; clients only see the replicated transform, animator and
    /// hit points. Spawned and despawned by <see cref="ZombieWaveManager"/>. The body is a tinted cat model without a
    /// weapon (placeholder until real zombie models / Mixamo zombie clips exist), built by CatBattleground > Build Phase 2.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class Zombie : NetworkBehaviour
    {
        const float AnimWalkSpeed = 1.4f;   // the cat blend tree's "1" (PlayerController walk speed)

        readonly NetworkVariable<byte> kind = new NetworkVariable<byte>(0);
        readonly NetworkVariable<float> maxHp = new NetworkVariable<float>(70f);
        readonly NetworkVariable<float> hp = new NetworkVariable<float>(70f);

        Health health;
        ZombieAnimator zanim;   // the zombie cat (Mixamo zombie clips)
        PlayerAnimator anim;    // fallback: old tinted cat bodies
        NavMeshAgent agent;
        Collider[] colliders;
        ZombieStats stats;

        NetworkPlayer target;
        float thinkTimer, nextAttack, hitAt = -1f, screamUntil;
        float nextGroan, nextHurtSound;
        float damageMultiplier = 1f;   // server: grows with the wave
        float stuckTime;               // server: how long it has made no progress towards its cat

        // voices (Tools/Audio/make_sfx.py zombie): deeper for the big ones
        enum Voice : byte { Scream, Attack, Bite, Hurt, Death }
        float VoicePitch => Kind == ZombieKind.Runner ? 1.15f : Kind == ZombieKind.Tank ? 0.82f : Kind == ZombieKind.Boss ? 0.68f : 1f;
        Vector3 facing = Vector3.forward;

        public ZombieKind Kind => (ZombieKind)kind.Value;
        public Health Health => health;
        public float HealthFraction => maxHp.Value > 0f ? hp.Value / maxHp.Value : 0f;

        void Awake()
        {
            health = GetComponent<Health>();
            zanim = GetComponent<ZombieAnimator>();
            anim = zanim == null ? GetComponent<PlayerAnimator>() : null;
            agent = GetComponent<NavMeshAgent>();
            colliders = GetComponentsInChildren<Collider>(true);
            if (agent != null) agent.enabled = false;   // switched on in ServerPlace, once it stands on the NavMesh
        }

        public override void OnNetworkSpawn()
        {
            kind.OnValueChanged += OnKindChanged;
            ApplyKind();
            nextGroan = Time.time + Random.Range(1f, 6f);
            if (IsServer)
            {
                health.Damaged += ServerOnDamaged;
                health.Died += ServerOnDied;
            }
            else
            {
                hp.OnValueChanged += OnHpChanged;
                health.SyncTo(hp.Value);
                if (health.IsDead) SetCollidersEnabled(false);
            }
        }

        public override void OnNetworkDespawn()
        {
            kind.OnValueChanged -= OnKindChanged;
            hp.OnValueChanged -= OnHpChanged;
            health.Damaged -= ServerOnDamaged;
            health.Died -= ServerOnDied;
        }

        void OnKindChanged(byte previous, byte current) => ApplyKind();

        // Size by kind on every machine (NetworkTransform does not sync scale). The NavMeshAgent scales with it.
        void ApplyKind()
        {
            stats = ZombieStats.Of(Kind);
            transform.localScale = Vector3.one * stats.scale;
        }

        // ---------------------------------------------------------------- server setup
        /// <summary>Server, right after Spawn: kind, hit points (with the wave multiplier) and position on the NavMesh.</summary>
        public void ServerSetup(ZombieKind zombieKind, float hpMultiplier, Vector3 position, float yaw, float damageMultiplier = 1f)
        {
            if (!IsServer) return;
            this.damageMultiplier = damageMultiplier;
            kind.Value = (byte)zombieKind;
            ApplyKind();
            health.SetMax(stats.hp * hpMultiplier);
            maxHp.Value = health.Max;
            hp.Value = health.Current;

            if (NavMesh.SamplePosition(position, out var hit, 10f, NavMesh.AllAreas)) position = hit.position;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            facing = transform.forward;
            if (agent != null)
            {
                agent.enabled = true;
                agent.Warp(position);
                agent.updateRotation = false;
                agent.speed = stats.speed;
                agent.acceleration = 8f + stats.speed * 3f;   // runners get up to speed quickly
                agent.stoppingDistance = stats.reach * 0.8f;
                agent.avoidancePriority = Random.Range(30, 70);
            }
            nextAttack = Time.time + 0.5f;
            // some roar when they rise (tanks and the boss always): they stand still for the scream
            if (zanim != null && Random.value < stats.screamChance)
            {
                screamUntil = Time.time + ZombieAnimator.ScreamSeconds / 1.3f;
                nextAttack = screamUntil;
                zanim.TriggerScream();
                VoiceRpc(Voice.Scream);
            }
        }

        /// <summary>Server: move to another spot on the NavMesh at once (ZombieWaveManager.ServerRelocate).</summary>
        public void ServerTeleport(Vector3 position, float yaw)
        {
            if (!IsServer || health.IsDead || agent == null) return;
            if (!agent.enabled) agent.enabled = true;
            agent.Warp(position);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            facing = transform.forward;
            hitAt = -1f;
            thinkTimer = 0f;
            stuckTime = 0f;
            if (GetComponent<Unity.Netcode.Components.NetworkTransform>() is { } nt) nt.Teleport(position, transform.rotation, transform.localScale);
        }

        void ServerOnDamaged(float amount, GameObject attacker)
        {
            hp.Value = health.Current;
            if (MatchManager.Instance != null) MatchManager.Instance.ServerOnDamage(null, attacker, amount);   // counts toward the shooter's damage
            if (!health.IsDead && Time.time >= nextHurtSound) { nextHurtSound = Time.time + 0.5f; VoiceRpc(Voice.Hurt); }
        }

        void ServerOnDied(GameObject attacker)
        {
            hp.Value = 0f;
            if (agent != null && agent.enabled) { agent.isStopped = true; agent.enabled = false; }
            SetCollidersEnabled(false);   // bullets fly through the body
            hitAt = -1f;
            VoiceRpc(Voice.Death);
            if (ZombieWaveManager.Instance != null) ZombieWaveManager.Instance.ServerOnZombieDied(this, attacker);
        }

        void OnHpChanged(float previous, float current)
        {
            health.SyncTo(current);
            if (current <= 0f) SetCollidersEnabled(false);
        }

        void SetCollidersEnabled(bool on)
        {
            foreach (var c in colliders) if (c != null) c.enabled = on;
        }

        // ---------------------------------------------------------------- server AI
        void Update()
        {
            // idle groans on every machine that plays sound (not replicated: each client picks its own moments)
            if (IsSpawned && !health.IsDead && Time.time >= nextGroan && !Application.isBatchMode)
            {
                nextGroan = Time.time + Random.Range(4f, 9f);
                GameAudio.Play("zombie_groan", transform.position + Vector3.up, 0.7f, 2.5f, 28f, VoicePitch, 0.08f);
            }
            if (!IsServer || !IsSpawned || health.IsDead || agent == null || !agent.enabled) return;
            if (!agent.isOnNavMesh)
            {
                stuckTime += Time.deltaTime;   // knocked off the NavMesh: bring it back near a cat
                if (stuckTime > 3f) { stuckTime = 0f; if (ZombieWaveManager.Instance != null) ZombieWaveManager.Instance.ServerRelocate(this); }
                return;
            }
            var match = MatchManager.Instance;
            if (match == null || match.State != MatchState.Playing)
            {
                agent.isStopped = true;
                Locomote(Vector3.zero);
                return;
            }

            if (Time.time < screamUntil)
            {
                agent.isStopped = true;
                Locomote(Vector3.zero);
                return;
            }

            thinkTimer -= Time.deltaTime;
            if (thinkTimer <= 0f)
            {
                thinkTimer = Random.Range(0.25f, 0.4f);
                target = NearestHuman(match);
                if (target != null)
                {
                    agent.isStopped = false;
                    agent.speed = stats.speed;
                    agent.SetDestination(target.transform.position);
                }
                else agent.isStopped = true;
            }

            float dist = float.MaxValue;
            Vector3 toTarget = Vector3.zero;
            if (target != null)
            {
                toTarget = target.transform.position - transform.position; toTarget.y = 0f;
                dist = toTarget.magnitude;
            }
            // a cat standing up on a rock / crate: the zombie can only get to its foot, so it reaches a bit further (claws up)
            float reachNow = stats.reach;
            if (target != null && target.transform.position.y - transform.position.y > 0.5f) reachNow += 0.8f;
            bool inReach = target != null && dist <= reachNow && CanReach(target);

            // watchdog: no way to the cat (partial path), not getting anywhere, or far off -> rise again near a cat
            if (target != null)
            {
                float dist3 = Vector3.Distance(target.transform.position, transform.position);
                bool cutOff = !agent.pathPending && agent.hasPath && agent.pathStatus != NavMeshPathStatus.PathComplete;
                bool idle = agent.velocity.sqrMagnitude < 0.15f && !inReach && hitAt < 0f;
                // a zombie waiting right under its cat (on a rock, a roof edge) is not stuck: only far-off ones get moved
                if (dist3 > 70f || ((cutOff || idle) && dist > 4.5f)) stuckTime += Time.deltaTime;
                else stuckTime = 0f;
                if (stuckTime > (dist3 > 70f ? 2f : 6f))
                {
                    stuckTime = 0f;
                    if (ZombieWaveManager.Instance != null) ZombieWaveManager.Instance.ServerRelocate(this);
                    return;
                }
            }

            // facing: the victim when close, otherwise the way it walks
            Vector3 velocity = agent.velocity; velocity.y = 0f;
            Vector3 want = facing;
            if (target != null && dist < 4f && toTarget.sqrMagnitude > 0.01f) want = toTarget.normalized;
            else if (velocity.sqrMagnitude > 0.05f) want = velocity.normalized;
            facing = Vector3.Slerp(facing, want, 1f - Mathf.Exp(-7f * Time.deltaTime));
            transform.rotation = Quaternion.LookRotation(facing, Vector3.up);
            Locomote(velocity);

            // swipe: wind up, then hit if the cat is still in reach (a quick dodge back avoids it)
            if (inReach && Time.time >= nextAttack && hitAt < 0f && Vector3.Angle(facing, toTarget) < 50f)
            {
                nextAttack = Time.time + stats.interval;
                hitAt = Time.time + stats.windup;
                int attack = zanim != null && dist < 0.9f * stats.scale && Random.value < 0.35f ? 1 : 0;   // close: sometimes a neck bite
                if (zanim != null) zanim.TriggerAttack(attack, stats.windup);
                else if (anim != null) anim.TriggerMelee();
                VoiceRpc(attack == 1 ? Voice.Bite : Voice.Attack);
            }
            if (hitAt > 0f && Time.time >= hitAt)
            {
                hitAt = -1f;
                if (target != null && !target.Health.IsDead && dist <= reachNow + 0.5f && CanReach(target))
                {
                    target.Health.TakeDamage(stats.damage * damageMultiplier, gameObject);
                    ImpactRpc(target.transform.position + Vector3.up * 1.1f);
                }
            }
        }

        // The reach above is measured flat, so a zombie standing under a cat on the floor above (or behind a wall) would still hit it.
        // The claws only land when the cat is at about the same height and nothing solid is in between.
        bool CanReach(NetworkPlayer cat)
        {
            // up to 2.3 m: a cat on a rock or crate still gets clawed (2026-10-28, 1.1 m let cats camp on rocks); a floor above
            // is ~5 m up with TownScale 1.7 and the line check below stops it anyway
            float dy = cat.transform.position.y - transform.position.y;
            if (dy > 2.3f + 0.5f * (stats.scale - 1f) || dy < -1.2f) return false;
            var from = transform.position + Vector3.up * 1.0f * stats.scale;
            var to = cat.transform.position + Vector3.up * 1.0f;
            foreach (var hit in Physics.RaycastAll(from, to - from, Vector3.Distance(from, to), ~0, QueryTriggerInteraction.Ignore))
            {
                var t = hit.collider.transform;
                if (t.IsChildOf(transform) || t.IsChildOf(cat.transform)) continue;   // its own body / the cat
                if (hit.collider.GetComponentInParent<Zombie>() != null) continue;      // other zombies do not shield the cat
                return false;
            }
            return true;
        }

        NetworkPlayer NearestHuman(MatchManager match)
        {
            NetworkPlayer best = null;
            float bestDist = float.MaxValue;
            foreach (var p in match.Players)
            {
                if (p == null || p.IsBot || p.Health.IsDead) continue;
                float d = (p.transform.position - transform.position).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        void Locomote(Vector3 worldVelocity)
        {
            var local = transform.InverseTransformDirection(worldVelocity) / Mathf.Max(stats.scale, 0.01f);   // a big zombie takes longer strides
            if (zanim != null) { zanim.SetLocomotion(stats.gait, new Vector2(local.x, local.z).magnitude); return; }
            if (anim == null) return;
            anim.SetLocomotion(new Vector2(local.x, local.z), AnimWalkSpeed);
            anim.SetGrounded(true);
        }

        [Rpc(SendTo.Everyone)]
        void ImpactRpc(Vector3 point) => GameAudio.Play("impact_flesh", point, 0.8f, 2f, 30f);

        [Rpc(SendTo.Everyone)]
        void VoiceRpc(Voice voice)
        {
            if (Application.isBatchMode) return;
            var at = transform.position + Vector3.up * stats.scale;
            switch (voice)
            {
                case Voice.Scream: GameAudio.Play("zombie_scream", at, 1f, 5f * stats.scale, 70f, VoicePitch, 0.05f); break;
                case Voice.Attack: GameAudio.Play("zombie_attack", at, 0.85f, 3f, 35f, VoicePitch); break;
                case Voice.Bite: GameAudio.Play("zombie_bite", at, 0.9f, 3f, 35f, VoicePitch); break;
                case Voice.Hurt: GameAudio.Play("zombie_hit", at, 0.6f, 2.5f, 30f, VoicePitch, 0.1f); break;
                case Voice.Death: GameAudio.Play("zombie_death", at, 0.85f, 3f, 40f, VoicePitch); break;
            }
        }
    }
}
