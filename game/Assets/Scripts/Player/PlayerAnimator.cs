using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Single entry point for driving the cat Animator (see CatPlayer.controller, built by CatBattleground > Build Phase 1 Test Scene).
    /// Locomotion is a 2D blend tree fed in units of "walk speed" (1 = walking, ~2.6 = sprinting).
    /// Combat clips live on the UpperBody layer (arms/spine only) so they play while moving.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimator : MonoBehaviour
    {
        [SerializeField] float moveDamping = 0.08f;
        [SerializeField] float groundedGrace = 0.12f;   // seconds in the air before the Fall state kicks in

        Animator animator;
        NetworkObject netObj;
        NetworkAnimator netAnim;
        Health health;
        int upperBodyLayer = -1;
        float airTime;
        bool dead, knocked;

        static readonly int MoveX = Animator.StringToHash("MoveX");
        static readonly int MoveY = Animator.StringToHash("MoveY");
        static readonly int MoveSpeed = Animator.StringToHash("MoveSpeed");
        static readonly int Grounded = Animator.StringToHash("Grounded");
        static readonly int Crouching = Animator.StringToHash("Crouching");
        static readonly int ParachuteParam = Animator.StringToHash("Parachute");
        static readonly int SkydiveParam = Animator.StringToHash("Skydive");
        static readonly int Dead = Animator.StringToHash("Dead");
        static readonly int DeathBack = Animator.StringToHash("DeathBack");
        static readonly int KnockedParam = Animator.StringToHash("Knocked");
        static readonly int JumpTrig = Animator.StringToHash("Jump");
        static readonly int FireTrig = Animator.StringToHash("Fire");
        static readonly int ReloadTrig = Animator.StringToHash("Reload");
        static readonly int ReloadSpeedParam = Animator.StringToHash("ReloadSpeed");
        /// <summary>Length of the Reload clip at speed 1 (54 frames at 30 fps, the assault rifle's reloadTime).</summary>
        public const float ReloadClipSeconds = 1.8f;
        static readonly int HitTrig = Animator.StringToHash("Hit");
        static readonly int MeleeTrig = Animator.StringToHash("Melee");
        static readonly int ThrowTrig = Animator.StringToHash("Throw");
        static readonly int InteractTrig = Animator.StringToHash("Interact");
        static readonly int VictoryParam = Animator.StringToHash("Victory");

        /// <summary>Offline, or the owning client when online. NetworkAnimator (owner mode) replicates everything else.</summary>
        bool Authority => netObj == null || !netObj.IsSpawned || netObj.IsOwner;

        public bool IsDead => dead;
        public bool IsKnocked => knocked;

        void Awake()
        {
            animator = GetComponent<Animator>();
            if (!Application.isBatchMode && GetComponent<FootFx>() == null) gameObject.AddComponent<FootFx>();   // running / landing dust
            netObj = GetComponent<NetworkObject>();
            netAnim = GetComponent<NetworkAnimator>();
            health = GetComponent<Health>();
            upperBodyLayer = animator.GetLayerIndex("UpperBody");
        }

        void OnEnable()
        {
            if (health == null) return;
            health.Damaged += OnDamaged;
            health.Died += OnDied;
        }

        void OnDisable()
        {
            if (health == null) return;
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
        }

        void Update()
        {
            // Combat layer is off while down or dead.
            if (upperBodyLayer >= 0 && Authority)
            {
                float target = (dead || knocked) ? 0f : 1f;
                animator.SetLayerWeight(upperBodyLayer, Mathf.MoveTowards(animator.GetLayerWeight(upperBodyLayer), target, Time.deltaTime * 8f));
            }
        }

        /// <param name="localVelocity">Planar velocity in the player's own space (x = right, y = forward), metres/second.</param>
        /// <param name="walkSpeed">Speed that counts as "1" in the blend tree.</param>
        public void SetLocomotion(Vector2 localVelocity, float walkSpeed)
        {
            if (!Authority) return;
            Vector2 v = localVelocity / Mathf.Max(walkSpeed, 0.01f);
            animator.SetFloat(MoveX, v.x, moveDamping, Time.deltaTime);
            animator.SetFloat(MoveY, v.y, moveDamping, Time.deltaTime);
            animator.SetFloat(MoveSpeed, v.magnitude, moveDamping, Time.deltaTime);
        }

        public void SetGrounded(bool grounded)
        {
            airTime = grounded ? 0f : airTime + Time.deltaTime;
            animator.SetBool(Grounded, grounded || airTime < groundedGrace);
        }

        public void SetCrouching(bool crouching) => animator.SetBool(Crouching, crouching);

        /// <summary>Hanging under an open parachute (plays the dangling pose instead of the free-fall tuck).</summary>
        public void SetParachute(bool open) => animator.SetBool(ParachuteParam, open);

        /// <summary>Free fall out of the drop ship (belly-down pose).</summary>
        public void SetSkydive(bool diving) => animator.SetBool(SkydiveParam, diving);

        // a real jump is airborne at once: skip the grounded grace (it is for small bumps), so the short take-off clip
        // always continues into Fall instead of reading "still grounded" at its end
        public void TriggerJump() { airTime = groundedGrace; Trigger(JumpTrig); }
        public void TriggerFire() => Trigger(FireTrig);
        /// <summary>Plays the reload so it lasts <paramref name="seconds"/> (the weapon's reloadTime).</summary>
        public void TriggerReload(float seconds = ReloadClipSeconds)
        {
            if (!Authority) return;
            animator.SetFloat(ReloadSpeedParam, ReloadClipSeconds / Mathf.Max(0.3f, seconds));
            Trigger(ReloadTrig);
        }
        public void TriggerMelee() => Trigger(MeleeTrig);
        public void TriggerThrow() => Trigger(ThrowTrig);
        public void TriggerInteract() => Trigger(InteractTrig);
        /// <summary>Winner celebration (VictorySequence): rifle up, fist pumps, holds the pose until set false. Bool: NetworkAnimator syncs it.</summary>
        public void SetVictory(bool value)
        {
            if (!Authority || animator == null) return;
            if (animator.GetBool(VictoryParam) != value) animator.SetBool(VictoryParam, value);
        }

        /// <summary>Downed (for revive in duo/squad). Pass false to get back up.</summary>
        public void SetKnocked(bool value)
        {
            if (dead) return;
            knocked = value;
            animator.SetBool(KnockedParam, value);
        }

        public void Revive()
        {
            dead = false;
            knocked = false;
            animator.SetBool(Dead, false);
            animator.SetBool(KnockedParam, false);
            animator.Rebind();
        }

        void Trigger(int hash)
        {
            if (!Authority) return;
            if (netAnim != null && netObj != null && netObj.IsSpawned) netAnim.SetTrigger(hash);
            else animator.SetTrigger(hash);
        }

        void OnDamaged(float amount, GameObject attacker)
        {
            if (Authority && !dead) Trigger(HitTrig);
        }

        void OnDied(GameObject attacker)
        {
            if (!Authority) return;
            dead = true;
            // Shot from the front knocks the cat backwards; shot from behind drops it on its face.
            bool fromFront = true;
            if (attacker != null)
            {
                Vector3 to = attacker.transform.position - transform.position;
                to.y = 0f;
                fromFront = Vector3.Dot(to, transform.forward) >= 0f;
            }
            animator.SetBool(DeathBack, fromFront);
            animator.SetBool(Dead, true);
        }
    }
}
