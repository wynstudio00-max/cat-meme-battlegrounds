using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Drives the zombie cat's own Animator (Assets/Animations/Zombie.controller, built by ZombieSetup from the user's Mixamo
    /// "Scary Zombie Pack"): a Locomotion blend tree on <c>Gait</c> (0 idle, 1 shamble walk, 2 run, 3 running crawl) whose playback
    /// rate <c>MoveRate</c> follows the real speed (no foot sliding), Attack / NeckBite (speed <c>AttackSpeed</c> so the blow lands
    /// at the server's windup), Scream, and two deaths. Server only: NetworkAnimator (server authority) replicates the parameters.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class ZombieAnimator : MonoBehaviour
    {
        /// <summary>Travel speed of each loop at playback rate 1, in Unity metres per second for a 1.75 m zombie (build_zombie.py).</summary>
        public const float WalkClipSpeed = 0.204f, RunClipSpeed = 1.898f, CrawlClipSpeed = 1.339f;
        /// <summary>Seconds from the start of the clip to the blow at rate 1 (claw frame 33, bite frame 42; build_zombie.py).</summary>
        public const float ClawHitTime = 32f / 30f, BiteHitTime = 41f / 30f;
        /// <summary>Scream clip length at rate 1 (85 frames).</summary>
        public const float ScreamSeconds = 84f / 30f;

        static readonly int GaitParam = Animator.StringToHash("Gait");
        static readonly int MoveRateParam = Animator.StringToHash("MoveRate");
        static readonly int AttackTrig = Animator.StringToHash("Attack");
        static readonly int AttackIndexParam = Animator.StringToHash("AttackIndex");
        static readonly int AttackSpeedParam = Animator.StringToHash("AttackSpeed");
        static readonly int ScreamTrig = Animator.StringToHash("Scream");
        static readonly int DeadParam = Animator.StringToHash("Dead");
        static readonly int DeathIndexParam = Animator.StringToHash("DeathIndex");

        Animator animator;
        NetworkObject netObj;
        NetworkAnimator netAnim;
        Health health;
        float gait, rate = 1f;

        bool Authority => netObj == null || !netObj.IsSpawned || (netObj.NetworkManager != null && netObj.NetworkManager.IsServer);

        void Awake()
        {
            animator = GetComponent<Animator>();
            netObj = GetComponent<NetworkObject>();
            netAnim = GetComponent<NetworkAnimator>();
            health = GetComponent<Health>();
        }

        void OnEnable() { if (health != null) health.Died += OnDied; }
        void OnDisable() { if (health != null) health.Died -= OnDied; }

        /// <param name="targetGait">0 idle, 1 walk, 2 run, 3 running crawl.</param>
        /// <param name="localSpeed">Planar speed divided by the zombie's scale (a big zombie takes longer strides).</param>
        public void SetLocomotion(int targetGait, float localSpeed)
        {
            if (!Authority) return;
            float clip = targetGait == 2 ? RunClipSpeed : targetGait == 3 ? CrawlClipSpeed : WalkClipSpeed;
            bool moving = targetGait > 0 && localSpeed > 0.05f;
            float wantRate = moving ? Mathf.Clamp(localSpeed / clip, 0.4f, 4.5f) : 1f;
            float wantGait = moving ? targetGait : 0f;
            float k = 1f - Mathf.Exp(-10f * Time.deltaTime);
            gait = Mathf.Lerp(gait, wantGait, k);
            rate = Mathf.Lerp(rate, wantRate, k);
            animator.SetFloat(GaitParam, gait);
            animator.SetFloat(MoveRateParam, rate);
        }

        /// <summary>Claw (index 0) or neck bite (1); the clip is sped up so the blow lands after <paramref name="windup"/> seconds.</summary>
        public void TriggerAttack(int index, float windup)
        {
            if (!Authority) return;
            float hit = index == 1 ? BiteHitTime : ClawHitTime;
            animator.SetInteger(AttackIndexParam, index);
            animator.SetFloat(AttackSpeedParam, Mathf.Clamp(hit / Mathf.Max(windup, 0.1f), 0.5f, 4f));
            Trigger(AttackTrig);
        }

        public void TriggerScream() { if (Authority) Trigger(ScreamTrig); }

        void Trigger(int hash)
        {
            if (netAnim != null && netObj != null && netObj.IsSpawned) netAnim.SetTrigger(hash);
            else animator.SetTrigger(hash);
        }

        void OnDied(GameObject attacker)
        {
            if (!Authority) return;
            animator.SetInteger(DeathIndexParam, Random.Range(0, 2));
            animator.SetBool(DeadParam, true);
        }
    }
}
