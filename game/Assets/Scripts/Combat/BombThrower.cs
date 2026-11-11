using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// The BOMB slot (key G, 2026-10-27). The owner plays the left-arm throw (Throw_Grenade, make_throw.py) and asks the server; the
    /// server counts the bombs, and when the paw lets go (<see cref="ReleaseDelay"/>) tells every machine to launch the same
    /// <see cref="Bomb"/> (local Rigidbody on each). After <see cref="Fuse"/> seconds the server takes its own copy's position,
    /// deals area damage (falls off with distance, blocked by walls and floors, half on yourself) and every machine plays
    /// <see cref="BombFx"/>. Offline (no spawned NetworkObject) the same happens locally. Bots do not throw.
    /// </summary>
    public class BombThrower : NetworkBehaviour
    {
        public const int StartBombs = 3;
        public const int MaxBombs = 5;
        /// <summary>Throw_Grenade lets go at frame 14 of 30 (30 fps, state speed 1).</summary>
        public const float ReleaseDelay = 13f / 30f;
        public const float Fuse = 2.2f;              // seconds from the release to the bang
        public const float BlastRadius = 5.5f;
        public const float MaxDamage = 95f;
        const float ThrowSpeed = 13f, ThrowLift = 3f, Cooldown = 1.2f, SelfDamage = 0.5f;

        readonly NetworkVariable<int> bombs = new NetworkVariable<int>(StartBombs);
        int offlineBombs = StartBombs;
        public int Bombs => IsSpawned ? bombs.Value : offlineBombs;

        PlayerController player;
        PlayerAnimator anim;
        PlayerDrop drop;
        Health health;
        float nextThrow, nextServerThrow;
        uint counter;
        Camera aimCamera;

        void Awake()
        {
            player = GetComponent<PlayerController>();
            anim = GetComponent<PlayerAnimator>();
            drop = GetComponent<PlayerDrop>();
            health = GetComponent<Health>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "mixamorig:LeftHand") leftHand = t;
                else if (t.name == "mixamorig:LeftHandMiddle1" || (handTip == null && t.name == "mixamorig:LeftHandIndex1")) handTip = t;
            }
        }

        // ---------------------------------------------------------------- the bomb in the paw (2026-10-28)
        // Shown while G is held (owner) and through the wind-up until the release (everyone: HoldRpc / the bot path), placed every
        // frame after the animation between the left hand and its fingers, so it swings with the throw.
        Transform leftHand, handTip;
        GameObject pawBomb;
        float pawUntil;

        void ShowPawBomb(float seconds) => pawUntil = Mathf.Max(pawUntil, Time.time + seconds);
        void HidePawBomb() => pawUntil = 0f;

        void LateUpdate()
        {
            if (Application.isBatchMode || leftHand == null) return;
            bool show = (IsHolding || Time.time < pawUntil) && (health == null || !health.IsDead);
            if (show && pawBomb == null) pawBomb = Bomb.CreateVisual("PawBomb");
            if (pawBomb == null) return;
            if (pawBomb.activeSelf != show) pawBomb.SetActive(show);
            if (!show) return;
            var palm = handTip != null ? Vector3.Lerp(leftHand.position, handTip.position, 0.6f) : leftHand.position;
            pawBomb.transform.SetPositionAndRotation(palm, leftHand.rotation);
        }

        public override void OnDestroy()
        {
            if (pawBomb != null) Destroy(pawBomb);
            base.OnDestroy();
        }

        // Server -> everyone else: this cat started a throw (the owner already shows its own bomb).
        [Rpc(SendTo.NotOwner)]
        void HoldRpc() => ShowPawBomb(ReleaseDelay);

        /// <summary>Server: new match / respawn.</summary>
        public void ServerResetBombs() { if (IsServer) bombs.Value = StartBombs; offlineBombs = StartBombs; }
        /// <summary>Server: a reward (Zombie Survival wave cleared).</summary>
        public void ServerAddBombs(int n) { if (IsServer) bombs.Value = Mathf.Min(MaxBombs, bombs.Value + n); }

        /// <summary>Owner: G is held and the throw arc is showing.</summary>
        public bool IsHolding { get; private set; }
        BombArc arc;

        // Hold G to aim (the body faces the crosshair, the arc and the blast circle show where it lands), let go to throw,
        // right mouse while holding cancels (2026-10-28).
        void Update()
        {
            if ((IsSpawned && !IsOwner) || GetComponent<BotBrain>() != null) return;
            bool can = CanThrowNow();
            if (!IsHolding)
            {
                if (can && GameInput.BombPressed && Bombs > 0 && Time.time >= nextThrow) SetHolding(true);
                return;
            }
            if (!can || Bombs <= 0 || GameInput.BombCancelPressed)
            {
                SetHolding(false);   // cancelled (or died, landed in a menu, ...)
                return;
            }
            var dir = AimDir();
            if (arc != null) arc.Show(ThrowStart(transform.position, dir), Velocity(dir), transform, BlastRadius);
            if (GameInput.BombHeld) return;

            // released: throw
            SetHolding(false);
            ShowPawBomb(ReleaseDelay);   // still in the paw through the wind-up
            nextThrow = Time.time + Cooldown;
            if (anim != null) anim.TriggerThrow();
            if (IsSpawned) ThrowServerRpc(dir);
            else { offlineBombs--; StartCoroutine(Release(dir, offline: true)); }
        }

        bool CanThrowNow()
        {
            if (player != null && !player.InputEnabled) return false;
            if (!GameInput.Locked) return false;
            if (MatchManager.Instance != null && MatchManager.Instance.InputLocked) return false;
            if (health != null && health.IsDead) return false;
            if (anim != null && anim.IsKnocked) return false;
            if (drop != null && !drop.CanFight) return false;
            return true;
        }

        void SetHolding(bool on)
        {
            IsHolding = on;
            if (player != null) player.AimOverride = on;
            if (on && arc == null && !Application.isBatchMode) arc = BombArc.Create();
            if (arc != null) arc.Hide();
        }

        void OnDisable() { if (IsHolding) SetHolding(false); }

        Vector3 AimDir()
        {
            if (aimCamera == null) aimCamera = Camera.main;
            return aimCamera != null ? aimCamera.transform.forward : transform.forward;
        }

        /// <summary>Where the bomb leaves the paw: the same formula on the owner (arc) and the server (real throw).</summary>
        public static Vector3 ThrowStart(Vector3 feet, Vector3 dir)
        {
            var flat = new Vector3(dir.x, 0f, dir.z);
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
            var left = Vector3.Cross(flat, Vector3.up).normalized;   // the throwing paw is the left one
            return feet + Vector3.up * 1.35f + flat * 0.35f + left * 0.25f;
        }

        public static Vector3 Velocity(Vector3 dir) => dir.normalized * ThrowSpeed + Vector3.up * ThrowLift;

        // ---------------------------------------------------------------- bots (2026-10-28)
        /// <summary>Server: a bot throws along <paramref name="dir"/> (same rules as a player: count, cooldown, release delay).</summary>
        public bool ServerBotThrow(Vector3 dir)
        {
            if (!IsServer || health == null || health.IsDead) return false;
            if (drop != null && !drop.CanFight) return false;
            if (bombs.Value <= 0 || Time.time < nextServerThrow) return false;
            nextServerThrow = Time.time + Cooldown;
            bombs.Value--;
            if (anim != null) anim.TriggerThrow();   // the server owns the bots, NetworkAnimator passes it on
            ShowPawBomb(ReleaseDelay);               // the host's own view of its bots (the server is their owner)
            HoldRpc();                               // everybody else
            StartCoroutine(Release(dir.normalized, offline: false));
            return true;
        }

        /// <summary>The throw direction that lands a bomb on <paramref name="target"/> from <paramref name="feet"/> (ignores the
        /// small drag and bounces). False when it is out of reach (about 24 m on flat ground).</summary>
        public static bool SolveThrow(Vector3 feet, Vector3 target, out Vector3 dir)
        {
            dir = Vector3.forward;
            var flat = target - feet; flat.y = 0f;
            float want = flat.magnitude;
            if (want < 0.5f) return false;
            flat /= want;
            float g = -Physics.gravity.y, bestErr = float.MaxValue;
            for (float pitch = -15f; pitch <= 55f; pitch += 1f)   // low arcs first: the first good one is the fastest
            {
                float r = pitch * Mathf.Deg2Rad;
                var d = flat * Mathf.Cos(r) + Vector3.up * Mathf.Sin(r);
                var s = ThrowStart(feet, d); var v = Velocity(d);
                float dy = s.y - target.y;
                float disc = v.y * v.y + 2f * g * dy;
                if (disc < 0f) continue;
                float t = (v.y + Mathf.Sqrt(disc)) / g;
                var land = new Vector2(s.x + v.x * t, s.z + v.z * t);
                float err = Vector2.Distance(land, new Vector2(target.x, target.z));
                if (err < bestErr) { bestErr = err; dir = d; }
                if (err < 0.8f) break;
            }
            return bestErr < 2.5f;
        }

        [Rpc(SendTo.Server)]
        void ThrowServerRpc(Vector3 dir)
        {
            if (health != null && health.IsDead) return;
            if (drop != null && !drop.CanFight) return;
            if (bombs.Value <= 0 || Time.time < nextServerThrow) return;
            if (dir.sqrMagnitude < 0.5f || dir.sqrMagnitude > 1.5f) return;
            nextServerThrow = Time.time + Cooldown * 0.8f;
            bombs.Value--;
            HoldRpc();
            StartCoroutine(Release(dir.normalized, offline: false));
        }

        IEnumerator Release(Vector3 dir, bool offline)
        {
            yield return new WaitForSeconds(ReleaseDelay);
            if (health != null && health.IsDead) yield break;
            var start = ThrowStart(transform.position, dir);   // same as the owner's arc
            var velocity = Velocity(dir);
            ulong id = ((IsSpawned ? NetworkObjectId : 0UL) << 20) | (++counter & 0xFFFFF);
            if (offline) Launch(id, start, velocity);
            else LaunchRpc(id, start, velocity);

            yield return new WaitForSeconds(Fuse);
            var b = Bomb.Find(id);
            var at = b != null ? b.transform.position : start;
            ServerDamage(at, offline);
            if (offline) Blow(id, at);
            else ExplodeRpc(id, at);
        }

        [Rpc(SendTo.Everyone)]
        void LaunchRpc(ulong id, Vector3 start, Vector3 velocity) => Launch(id, start, velocity);

        void Launch(ulong id, Vector3 start, Vector3 velocity)
        {
            HidePawBomb();   // it leaves the paw now
            Bomb.Spawn(id, start, velocity, transform, Fuse + 3f);
            if (!Application.isBatchMode) GameAudio.Play("bomb_throw", start, 0.8f, 2f, 25f);
        }

        [Rpc(SendTo.Everyone)]
        void ExplodeRpc(ulong id, Vector3 at) => Blow(id, at);

        static void Blow(ulong id, Vector3 at)
        {
            Bomb.Remove(id);
            BombFx.Explode(at, BlastRadius);
        }

        // ---------------------------------------------------------------- damage (server / offline)
        static readonly Collider[] overlap = new Collider[64];

        void ServerDamage(Vector3 at, bool offline)
        {
            if (!offline && !IsServer) return;
            int n = Physics.OverlapSphereNonAlloc(at, BlastRadius, overlap, ~0, QueryTriggerInteraction.Ignore);
            var done = new HashSet<Health>();
            for (int i = 0; i < n; i++)
            {
                var h = overlap[i].GetComponentInParent<Health>();
                if (h == null || h.IsDead || !done.Add(h)) continue;
                var centre = h.transform.position + Vector3.up * 0.9f * h.transform.lossyScale.y;
                float dist = Vector3.Distance(at, centre);
                if (dist > BlastRadius || Blocked(at + Vector3.up * 0.25f, centre, h)) continue;
                float dmg = MaxDamage * Mathf.Lerp(1f, 0.15f, dist / BlastRadius);
                if (h.gameObject == gameObject) dmg *= SelfDamage;
                float before = h.Current, armorBefore = h.Armor;
                h.TakeDamage(dmg, gameObject);
                if (h.gameObject == gameObject || GetComponent<BotBrain>() != null) continue;
                float dealt = (before - h.Current) + (armorBefore - h.Armor);
                if (dealt <= 0.01f) continue;
                var no = h.GetComponent<NetworkObject>();
                ulong tid = no != null ? no.NetworkObjectId : (ulong)(uint)h.GetHashCode();
                if (offline) CombatFeedback.OnHitConfirmed(centre, dealt, h.Armor < armorBefore, h.IsDead, tid, false);
                else BombHitRpc(centre, dealt, h.Armor < armorBefore, h.IsDead, tid);
            }
        }

        // walls and floors between the bang and the target stop the blast
        static bool Blocked(Vector3 from, Vector3 to, Health target)
        {
            foreach (var hit in Physics.RaycastAll(from, to - from, Vector3.Distance(from, to), ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(target.transform)) continue;
                if (hit.collider.GetComponentInParent<Health>() != null || hit.collider.GetComponent<Bomb>() != null) continue;   // other bodies do not shield
                return true;
            }
            return false;
        }

        [Rpc(SendTo.Owner)]
        void BombHitRpc(Vector3 point, float damage, bool armor, bool kill, ulong targetId) =>
            CombatFeedback.OnHitConfirmed(point, damage, armor, kill, targetId, false);
    }
}
