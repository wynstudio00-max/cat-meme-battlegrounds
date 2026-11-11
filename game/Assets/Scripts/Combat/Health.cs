using System;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Hit points (+ armor) for anything that can be shot. Offline it is authoritative; online only the server
    /// calls TakeDamage/Heal/AddArmor and <see cref="NetworkPlayerHealth"/> mirrors the values to clients.
    /// Armor soaks 60% of each hit until it is used up.
    /// </summary>
    public class Health : MonoBehaviour
    {
        public const float MaxArmor = 100f;
        const float ArmorAbsorb = 0.6f;

        [SerializeField] float maxHealth = 100f;

        public float Max => maxHealth;
        public float Current { get; private set; }
        public float Armor { get; private set; }
        public bool IsDead => Current <= 0f;

        public event Action<float, GameObject> Damaged; // amount (after armor), attacker
        public event Action<GameObject> Died;           // attacker

        void Awake()
        {
            Current = maxHealth;
            // red hit flash + elimination burst on every machine (visual only, not on a dedicated server)
            if (!Application.isBatchMode && GetComponent<HitReaction>() == null) gameObject.AddComponent<HitReaction>();
        }

        public void TakeDamage(float amount, GameObject attacker = null)
        {
            if (IsDead || amount <= 0f) return;
            if (Armor > 0f)
            {
                float absorbed = Mathf.Min(Armor, amount * ArmorAbsorb);
                Armor -= absorbed;
                amount -= absorbed;
            }
            Current = Mathf.Max(0f, Current - amount);
            Damaged?.Invoke(amount, attacker);
            if (IsDead) Died?.Invoke(attacker);
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;
            Current = Mathf.Min(maxHealth, Current + amount);
        }

        /// <summary>New maximum and full health (zombies: hit points per kind and wave).</summary>
        public void SetMax(float max)
        {
            maxHealth = Mathf.Max(1f, max);
            Current = maxHealth;
        }

        public void AddArmor(float amount) => Armor = Mathf.Clamp(Armor + amount, 0f, MaxArmor);

        public void ResetHealth()
        {
            Current = maxHealth;
            Armor = 0f;
        }

        /// <summary>Apply values replicated from the server (clients only). Raises the same events as TakeDamage.</summary>
        public void SyncTo(float value, GameObject attacker = null)
        {
            float prev = Current;
            Current = Mathf.Clamp(value, 0f, maxHealth);
            if (Current < prev)
            {
                Damaged?.Invoke(prev - Current, attacker);
                if (Current <= 0f) Died?.Invoke(attacker);
            }
        }

        public void SyncArmor(float value) => Armor = Mathf.Clamp(value, 0f, MaxArmor);
    }
}
