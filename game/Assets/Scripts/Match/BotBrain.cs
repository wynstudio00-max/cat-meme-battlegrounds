using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace CatBattleground
{
    /// <summary>
    /// Server-only AI that plays a cat: loots, stays inside the zone, finds enemies it can see and shoots them with the
    /// same weapon stats a person gets. Added at runtime to a normal player prefab (before it is spawned), so on every
    /// client a bot looks and behaves like any other player; nothing about it is replicated.
    /// Moves with a NavMeshAgent (the arena NavMesh is baked by CatBattleground > Build Phase 2 Multiplayer Scene).
    /// </summary>
    public class BotBrain : MonoBehaviour
    {
        const float WalkSpeed = 1.4f;       // keep in sync with PlayerController, the animator blend tree is scaled to it
        const float SprintSpeed = 4.0f;
        const float SightRange = 38f;
        const float LootRange = 30f;

        NavMeshAgent agent;
        PlayerAnimator anim;
        PlayerShooter shooter;
        Health health;
        NetworkPlayer self;

        float thinkTimer;
        float lootScanTimer;
        float strafeTimer;
        float strafeDir = 1f;
        float reactionTimer;
        NetworkPlayer target;
        BombThrower bombs;
        Vector3 lastSeenPos;              // where the last enemy was when it went out of sight (bomb it out of cover)
        float lastSeenTime = -99f, nextBombTime, bombRollTime, faceThrowUntil;
        Vector3 throwFacing;
        LootItem lootGoal;
        Vector3 roamGoal;
        bool hasRoamGoal;
        Vector3 facing = Vector3.forward;
        readonly List<LootItem> lootCache = new List<LootItem>();

        // personality so bots do not all play the same
        float accuracyDegrees;
        float reactionSeconds;
        float aggression;

        public void Init()
        {
            agent = gameObject.AddComponent<NavMeshAgent>();
            agent.radius = 0.4f;
            agent.height = 1.75f;
            agent.angularSpeed = 0f;
            agent.updateRotation = false;
            agent.acceleration = 12f;
            agent.stoppingDistance = 0.3f;
            agent.avoidancePriority = Random.Range(30, 70);
            agent.enabled = false;   // switched on in Place(), once it stands on the NavMesh

            anim = GetComponent<PlayerAnimator>();
            shooter = GetComponent<PlayerShooter>();
            health = GetComponent<Health>();
            self = GetComponent<NetworkPlayer>();
            bombs = GetComponent<BombThrower>();
            nextBombTime = Time.time + Random.Range(10f, 25f);   // not straight after landing

            accuracyDegrees = Random.Range(2.5f, 6f);
            reactionSeconds = Random.Range(0.45f, 0.9f);
            aggression = Random.Range(0.3f, 0.9f);
            facing = transform.forward;
        }

        /// <summary>Take the bot off the NavMesh (it boards the drop ship).</summary>
        public void Unplace()
        {
            if (agent != null && agent.enabled) { agent.isStopped = true; agent.enabled = false; }
            hasRoamGoal = false;
            lootGoal = null;
            target = null;
        }

        /// <summary>Put the bot on the NavMesh at a spawn point (or where it landed).</summary>
        public void Place(Vector3 position, float yaw)
        {
            if (NavMesh.SamplePosition(position, out var hit, 14f, NavMesh.AllAreas)) position = hit.position;
            transform.position = position;
            agent.enabled = true;
            agent.Warp(position);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            facing = transform.forward;
        }

        void Update()
        {
            var match = MatchManager.Instance;
            if (agent == null || !agent.isOnNavMesh) return;

            if (health.IsDead)
            {
                if (agent.enabled) { agent.isStopped = true; agent.enabled = false; }
                return;
            }
            if (match == null || match.State != MatchState.Playing)
            {
                agent.isStopped = true;
                Locomote(Vector3.zero, 0f);
                return;
            }

            thinkTimer -= Time.deltaTime;
            if (thinkTimer <= 0f)
            {
                thinkTimer = Random.Range(0.2f, 0.35f);
                Think(match);
            }
            Act();
        }

        // ---------------------------------------------------------------- decisions
        void Think(MatchManager match)
        {
            var zone = match.Zone;
            var me = transform.position;

            // 1) enemies in sight
            target = FindVisibleEnemy(match);
            if (target != null && reactionTimer <= 0f) reactionTimer = reactionSeconds;
            if (target == null) reactionTimer = 0f;
            if (target != null) { lastSeenPos = target.transform.position; lastSeenTime = Time.time; }
            ConsiderBomb();

            // 2) zone: leave a dangerous circle, or head for the next one before it closes
            bool mustMove = false;
            Vector3 zoneGoal = me;
            if (zone != null && zone.Active)
            {
                var flat = me - zone.Center; flat.y = 0f;
                bool outside = flat.magnitude > zone.Radius * 0.92f;
                var nextFlat = me - zone.NextCenter; nextFlat.y = 0f;
                bool outsideNext = nextFlat.magnitude > zone.NextRadius * 0.85f;
                if (outside || (zone.Shrinking && outsideNext) || (!zone.Shrinking && zone.SecondsLeft < 12f && outsideNext))
                {
                    mustMove = true;
                    var toward = zone.NextCenter + Random.insideUnitSphere * Mathf.Max(1f, zone.NextRadius * 0.4f);
                    toward.y = me.y;
                    zoneGoal = toward;
                }
            }

            if (mustMove && (target == null || health.Current < 35f))
            {
                SetDestination(zoneGoal, SprintSpeed);
                lootGoal = null;
                return;
            }

            // 3) fight
            if (target != null)
            {
                float dist = Vector3.Distance(me, target.transform.position);
                strafeTimer -= 0.3f;
                if (strafeTimer <= 0f) { strafeTimer = Random.Range(0.8f, 2.2f); strafeDir = Random.value < 0.5f ? -1f : 1f; }

                var away = (me - target.transform.position); away.y = 0f; away.Normalize();
                var side = Vector3.Cross(Vector3.up, away) * strafeDir;
                Vector3 goal;
                float desired = Mathf.Lerp(22f, 10f, aggression);
                if (dist > desired + 6f) goal = target.transform.position;                 // close in
                else if (dist < desired - 5f) goal = me + away * 6f + side * 2f;           // back off
                else goal = me + side * 4f;                                                 // circle-strafe
                SetDestination(goal, dist > 25f ? SprintSpeed : WalkSpeed);
                return;
            }

            // 4) loot
            lootScanTimer -= 0.3f;
            if (lootScanTimer <= 0f)
            {
                lootScanTimer = 2f;
                lootCache.Clear();
                lootCache.AddRange(FindObjectsByType<LootItem>());
            }
            if (lootGoal == null || !lootGoal.IsSpawned) lootGoal = PickLoot(me);
            if (lootGoal != null)
            {
                SetDestination(lootGoal.transform.position, mustMove ? SprintSpeed : (Vector3.Distance(me, lootGoal.transform.position) > 12f ? SprintSpeed : WalkSpeed));
                return;
            }

            // 5) roam
            if (mustMove) { SetDestination(zoneGoal, SprintSpeed); return; }
            if (!hasRoamGoal || agent.remainingDistance < 1.5f)
            {
                var centre = zone != null && zone.Active ? zone.NextCenter : Vector3.zero;
                float radius = zone != null && zone.Active ? Mathf.Max(3f, zone.NextRadius * 0.8f) : 80f;
                var p = centre + Random.insideUnitSphere * radius;
                p.y = me.y;
                if (NavMesh.SamplePosition(p, out var hit, 8f, NavMesh.AllAreas)) { roamGoal = hit.position; hasRoamGoal = true; }
            }
            if (hasRoamGoal) SetDestination(roamGoal, WalkSpeed);
        }

        LootItem PickLoot(Vector3 me)
        {
            LootItem best = null;
            float bestScore = float.MaxValue;
            bool hurt = health.Current < health.Max - 20f;
            foreach (var item in lootCache)
            {
                if (item == null || !item.IsSpawned) continue;
                float d = Vector3.Distance(me, item.transform.position);
                if (d > LootRange) continue;
                float score = d;
                if (item.Type == LootType.Medkit && hurt) score *= 0.4f;
                else if (item.Type == LootType.Medkit && !hurt) continue;
                if (score < bestScore) { bestScore = score; best = item; }
            }
            return best;
        }

        NetworkPlayer FindVisibleEnemy(MatchManager match)
        {
            NetworkPlayer best = null;
            float bestDist = SightRange;
            var eye = transform.position + Vector3.up * 1.5f;
            foreach (var other in match.Players)
            {
                if (other == null || other == self || other.Health.IsDead) continue;
                float d = Vector3.Distance(transform.position, other.transform.position);
                if (d > bestDist) continue;
                var point = other.transform.position + Vector3.up * 1.1f;
                if (Physics.Linecast(eye, point, out var hit, ~0, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<NetworkPlayer>() != other) continue;   // something solid in between
                best = other;
                bestDist = d;
            }
            return best;
        }

        // ---------------------------------------------------------------- acting
        void SetDestination(Vector3 goal, float speed)
        {
            agent.isStopped = false;
            agent.speed = speed;
            if (NavMesh.SamplePosition(goal, out var hit, 4f, NavMesh.AllAreas)) agent.SetDestination(hit.position);
        }

        void Act()
        {
            Vector3 velocity = agent.velocity; velocity.y = 0f;

            // facing: look at the enemy while fighting, otherwise where we are going
            Vector3 desiredFacing = facing;
            if (Time.time < faceThrowUntil) desiredFacing = throwFacing;   // winding up a bomb
            else if (target != null)
            {
                var to = target.transform.position - transform.position; to.y = 0f;
                if (to.sqrMagnitude > 0.01f) desiredFacing = to.normalized;
            }
            else if (velocity.sqrMagnitude > 0.05f) desiredFacing = velocity.normalized;
            facing = Vector3.Slerp(facing, desiredFacing, 1f - Mathf.Exp(-9f * Time.deltaTime));
            transform.rotation = Quaternion.LookRotation(facing, Vector3.up);

            Locomote(velocity, agent.speed);

            if (target != null)
            {
                reactionTimer -= Time.deltaTime;
                if (reactionTimer <= 0f) TryShoot();
            }
        }

        void Locomote(Vector3 worldVelocity, float speed)
        {
            if (anim == null) return;
            var local = transform.InverseTransformDirection(worldVelocity);
            anim.SetLocomotion(new Vector2(local.x, local.z), WalkSpeed);
            anim.SetGrounded(true);
            anim.SetCrouching(false);
        }

        // Bombs (2026-10-28): now and then at an enemy in plain sight 8-22 m away, and more often at the spot where one just ducked
        // out of sight (0.5-4 s ago) to flush it out of cover. Every bot keeps the same 3 bombs as a player; at most one per 8-16 s.
        void ConsiderBomb()
        {
            if (bombs == null || bombs.Bombs <= 0 || Time.time < nextBombTime || Time.time < bombRollTime) return;
            bombRollTime = Time.time + 2.5f;   // one roll of the dice every 2.5 s
            var me = transform.position;
            Vector3 aimAt; float chance;
            if (target != null)
            {
                aimAt = target.transform.position;
                chance = 0.1f + 0.25f * aggression;
            }
            else if (Time.time - lastSeenTime > 0.5f && Time.time - lastSeenTime < 4f)
            {
                aimAt = lastSeenPos;
                chance = 0.35f + 0.35f * aggression;
            }
            else return;
            float dist = Vector3.Distance(new Vector3(me.x, 0f, me.z), new Vector3(aimAt.x, 0f, aimAt.z));
            if (dist < 8f || dist > 22f || Random.value > chance) return;   // too close for comfort / out of reach
            if (!BombThrower.SolveThrow(me, aimAt, out var dir)) return;
            // a bot is not perfect: yaw and pitch error, a little more for the worse shots
            dir = Quaternion.AngleAxis(Random.Range(-1f, 1f) * accuracyDegrees * 0.9f, Vector3.up) * dir;
            dir = Quaternion.AngleAxis(Random.Range(-2.5f, 2.5f), Vector3.Cross(dir, Vector3.up).normalized) * dir;
            if (!bombs.ServerBotThrow(dir)) return;
            nextBombTime = Time.time + Random.Range(8f, 16f);
            throwFacing = new Vector3(dir.x, 0f, dir.z).normalized;
            faceThrowUntil = Time.time + BombThrower.ReleaseDelay + 0.25f;
        }

        void TryShoot()
        {
            if (Time.time < faceThrowUntil) return;   // the paw is busy with a bomb
            var origin = transform.position + Vector3.up * 1.5f;
            var aim = target.transform.position + Vector3.up * 1.1f;
            var dir = (aim - origin).normalized;
            // aim error grows with distance
            float error = accuracyDegrees * Mathf.Lerp(0.6f, 1.4f, Vector3.Distance(origin, aim) / SightRange);
            dir = Quaternion.AngleAxis(Random.Range(0f, 360f), dir) * Quaternion.AngleAxis(Random.Range(0f, error), Vector3.Cross(dir, Vector3.up).normalized) * dir;

            // Only shoot once roughly facing the target.
            if (Vector3.Angle(facing, new Vector3(dir.x, 0f, dir.z)) > 25f) return;
            shooter.ServerBotFire(origin, dir);
        }
    }
}
