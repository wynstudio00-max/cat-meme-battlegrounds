using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    public enum LootType { Medkit, Armor, Ammo }

    /// <summary>
    /// A pickup lying in the arena. The server detects who walks into the trigger, applies the effect to that player
    /// and despawns the item. Needs: NetworkObject, a trigger collider and a kinematic Rigidbody (so trigger events fire).
    /// </summary>
    public class LootItem : NetworkBehaviour
    {
        [SerializeField] LootType type;
        [SerializeField] float amount = 40f;       // heal / armor / ammo

        public LootType Type => type;

        void OnTriggerEnter(Collider other)
        {
            if (!IsServer || !IsSpawned) return;
            var player = other.GetComponentInParent<NetworkPlayer>();
            if (player == null) return;
            var health = player.GetComponent<Health>();
            if (health == null || health.IsDead) return;

            var netHealth = player.GetComponent<NetworkPlayerHealth>();
            var shooter = player.GetComponent<PlayerShooter>();
            switch (type)
            {
                case LootType.Medkit:
                    if (health.Current >= health.Max) return;          // keep it for someone who needs it
                    netHealth.ServerHeal(amount);
                    break;
                case LootType.Armor:
                    if (health.Armor >= Health.MaxArmor) return;
                    netHealth.ServerAddArmor(amount);
                    break;
                case LootType.Ammo:
                    shooter.ServerAddAmmo(Mathf.RoundToInt(amount));
                    break;
            }
            NetworkObject.Despawn(true);
        }
    }
}
