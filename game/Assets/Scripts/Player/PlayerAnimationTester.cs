using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// Debug keys to preview every animation in the test scene. Remove (or disable) once real gameplay triggers them.
    /// V melee, G grenade, E interact, H take damage, K toggle knocked-down, L die / revive.
    /// </summary>
    [RequireComponent(typeof(PlayerAnimator))]
    public class PlayerAnimationTester : MonoBehaviour
    {
        PlayerAnimator anim;
        Health health;
        bool knocked;

        void Awake()
        {
            anim = GetComponent<PlayerAnimator>();
            health = GetComponent<Health>();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || Cursor.lockState != CursorLockMode.Locked) return;

            if (kb.lKey.wasPressedThisFrame)
            {
                if (health.IsDead) { health.ResetHealth(); anim.Revive(); }
                else health.TakeDamage(health.Max, gameObject);
                return;
            }
            if (anim.IsDead) return;

            if (kb.vKey.wasPressedThisFrame) anim.TriggerMelee();
            if (kb.gKey.wasPressedThisFrame) anim.TriggerThrow();
            if (kb.eKey.wasPressedThisFrame) anim.TriggerInteract();
            if (kb.hKey.wasPressedThisFrame) health.TakeDamage(15f, null);
            if (kb.kKey.wasPressedThisFrame) { knocked = !knocked; anim.SetKnocked(knocked); }
        }
    }
}
