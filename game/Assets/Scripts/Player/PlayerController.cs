using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Third-person movement in the PUBG / Fortnite style. Owns the camera yaw/pitch (the mouse orbits the camera freely).
    ///  * Free mode: the cat turns to face where it walks, so pressing S runs towards the camera and shows its face.
    ///  * Aim mode (right mouse held, or firing / just fired): the body faces the crosshair and strafes with the 8-way clips.
    /// Movement is always relative to the camera yaw.
    /// Speeds match the Mixamo locomotion clips through the blend-tree timeScales in AnimatorBuilder (walk 1.28, sprint 1.0, crouch 0.8).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement (m/s)")]
        [SerializeField] float walkSpeed = 1.4f;
        [SerializeField] float sprintSpeed = 4.0f;
        [SerializeField] float crouchSpeed = 0.9f;
        [SerializeField] float jumpHeight = 1.3f;   // metres (was 0.9); air time ~0.69 s
        [SerializeField] float gravity = -22f;
        [SerializeField] float turnSpeed = 14f;          // aim mode: how fast the body snaps to the crosshair
        [SerializeField] float freeTurnSpeed = 10f;      // free mode: how fast the body turns towards the walking direction
        [SerializeField] float aimHoldAfterShot = 0.8f;  // keep facing the crosshair this long after the trigger is released

        [Header("Look")]
        [SerializeField] float mouseSensitivity = 0.12f;
        [SerializeField] float minPitch = -35f;
        [SerializeField] float maxPitch = 60f;

        [Header("Capsule")]
        [SerializeField] float standHeight = 1.75f;
        [SerializeField] float crouchHeight = 1.15f;

        CharacterController controller;
        PlayerAnimator anim;
        PlayerDrop drop;
        Vector3 velocity;
        bool wasGrounded = true;
        bool crouching;
        float aimUntil;

        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public bool InputEnabled { get; set; } = true;
        public bool IsCrouching => crouching;
        public float MouseSensitivity { get => mouseSensitivity; set { mouseSensitivity = value; PlayerPrefs.SetFloat("mouseSensitivity", value); } }
        /// <summary>Right mouse held, or shooting: the body faces the crosshair and the camera zooms to the shoulder.</summary>
        public bool IsAiming { get; private set; }
        /// <summary>Set by another component (BombThrower while G is held) to face the crosshair like aim mode.</summary>
        public bool AimOverride { get; set; }

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            // A CharacterController rests skinWidth (0.08 m) above the ground; raise the capsule by that much so the
            // transform - where the cat's feet are - touches the ground instead of floating (2026-11-07, user: Cash Cat floats).
            if (controller != null) controller.center = new Vector3(0f, controller.height * 0.5f + controller.skinWidth, 0f);
            anim = GetComponent<PlayerAnimator>();
            drop = GetComponent<PlayerDrop>();
            mouseSensitivity = PlayerPrefs.GetFloat("mouseSensitivity", mouseSensitivity);
            Yaw = transform.eulerAngles.y;
        }

        void Start() { if (!GameInput.TouchMode) SetCursorLocked(true); }

        void Update()
        {
            var mouse = Mouse.current;
            if (!GameInput.TouchMode)
            {
                var kb = Keyboard.current;
                if (kb == null || mouse == null) return;
                if (kb.escapeKey.wasPressedThisFrame && !GameUI.Active) SetCursorLocked(false);   // with the menu UI, ESC opens the pause menu (GameUI)
                else if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked && !GameUI.BlocksGameInput) SetCursorLocked(true);
            }

            bool active = InputEnabled && (anim == null || (!anim.IsDead && !anim.IsKnocked))
                          && !(MatchManager.Instance != null && MatchManager.Instance.InputLocked);
            bool locked = GameInput.Locked;
            if (active && locked) Look();
            if (active && locked && GameInput.FireHeld) aimUntil = Time.time + aimHoldAfterShot;
            IsAiming = active && locked && (GameInput.AimHeld || Time.time < aimUntil || AimOverride);
            if (drop != null && drop.Airborne) { IsAiming = false; return; }   // on the ship / parachuting: PlayerDrop moves the cat
            if (!controller.enabled) return;   // switched off for a moment (landing / teleport)
            Move(active);
        }

        void Look()
        {
            var delta = GameInput.Look * mouseSensitivity;
            // through a scope the view turns as slowly as it is zoomed (same feel on screen as without it)
            var scope = PlayerShooter.LocalScope;
            if (scope != null && scope.gameObject == gameObject && scope.ScopeBlend > 0f)
                delta *= Mathf.Lerp(1f, scope.Weapon.scopeFov / 60f, scope.ScopeBlend);
            Yaw += delta.x;
            Pitch = Mathf.Clamp(Pitch - delta.y, minPitch, maxPitch);
        }

        void Move(bool active)
        {
            Vector2 input = Vector2.zero;
            if (active)
            {
                input = GameInput.Move;
                if (GameInput.CrouchPressed) SetCrouch(!crouching);
            }

            var yawRot = Quaternion.Euler(0f, Yaw, 0f);
            bool moving = input.sqrMagnitude > 0.01f;
            Vector2 localVel;      // what the animator gets, in the body's own space
            Vector3 move;
            Quaternion facing = transform.rotation;
            float turn = freeTurnSpeed;
            if (IsAiming)
            {
                // strafe: body to the crosshair, sprint only forwards, a little slower backwards
                bool sprint = active && !crouching && GameInput.Sprint && input.y > 0f;
                float speed = crouching ? crouchSpeed : (sprint ? sprintSpeed : walkSpeed);
                if (!sprint && input.y < 0f && !crouching) speed = walkSpeed * 0.9f;
                localVel = input * speed;
                move = yawRot * new Vector3(localVel.x, 0f, localVel.y);
                facing = yawRot; turn = turnSpeed;
            }
            else
            {
                // free: walk where the keys point (relative to the camera) and turn the body that way
                bool sprint = active && !crouching && GameInput.Sprint && moving;
                float speed = crouching ? crouchSpeed : (sprint ? sprintSpeed : walkSpeed);
                var dir = yawRot * new Vector3(input.x, 0f, input.y);
                move = dir * speed;
                if (moving) facing = Quaternion.LookRotation(dir.normalized, Vector3.up);
                // the body leads with its chest, so the forward clips are used; slower blend while still turning round
                float ahead = moving ? Mathf.Max(0f, Vector3.Dot(transform.forward, dir.normalized)) : 0f;
                localVel = new Vector2(0f, input.magnitude * speed * Mathf.Lerp(0.6f, 1f, ahead));
            }

            bool grounded = controller.isGrounded;
            // landing thump after a real fall (not small steps); only shakes when this is the camera's own cat
            if (grounded && !wasGrounded && velocity.y < -7f && CameraFx.IsLocalView(this))
                CameraFx.Shake(Mathf.Clamp(0.12f + (-velocity.y - 7f) / 20f, 0.12f, 0.6f));
            wasGrounded = grounded;
            if (grounded && velocity.y < 0f) velocity.y = -2f;
            if (active && grounded && !crouching && GameInput.JumpPressed)
            {
                velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
                if (anim != null) anim.TriggerJump();
            }
            velocity.y += gravity * Time.deltaTime;

            controller.Move((move + new Vector3(0f, velocity.y, 0f)) * Time.deltaTime);

            transform.rotation = Quaternion.Slerp(transform.rotation, facing, turn * Time.deltaTime);

            if (anim != null)
            {
                anim.SetLocomotion(localVel, walkSpeed);
                anim.SetGrounded(controller.isGrounded);
                anim.SetCrouching(crouching);
            }
        }

        /// <summary>Move the player instantly (respawn). A CharacterController must be disabled while its transform is set.</summary>
        public void Teleport(Vector3 position, float yaw)
        {
            controller.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            controller.enabled = true;
            Yaw = yaw;
            Pitch = 0f;
            velocity = Vector3.zero;
        }

        void SetCrouch(bool value)
        {
            crouching = value;
            float h = value ? crouchHeight : standHeight;
            controller.height = h;
            controller.center = new Vector3(0f, h * 0.5f + controller.skinWidth, 0f);
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
