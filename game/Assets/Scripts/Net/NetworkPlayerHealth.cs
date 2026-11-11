using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Replicates <see cref="Health"/>. The server is the only place damage, healing and armor are applied; clients
    /// mirror the values and get the same Damaged/Died events, so animation and HUD code does not care whether it runs online.
    /// Outside a match (lobby / practice) a dead cat respawns; during a match it stays dead.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class NetworkPlayerHealth : NetworkBehaviour
    {
        [SerializeField] float respawnDelay = 5f;

        // Declared first so the attacker id is already updated when hp change callbacks run on clients.
        readonly NetworkVariable<ulong> lastAttacker = new NetworkVariable<ulong>(ulong.MaxValue);
        readonly NetworkVariable<float> hp = new NetworkVariable<float>(100f);
        readonly NetworkVariable<float> armor = new NetworkVariable<float>(0f);

        Health health;

        public override void OnNetworkSpawn()
        {
            health = GetComponent<Health>();
            if (IsServer)
            {
                hp.Value = health.Max;
                health.Damaged += ServerOnDamaged;
                health.Died += ServerOnDied;
            }
            else
            {
                hp.OnValueChanged += OnHpChanged;
                armor.OnValueChanged += OnArmorChanged;
                health.SyncTo(hp.Value);   // late joiners
                health.SyncArmor(armor.Value);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (health == null) return;
            health.Damaged -= ServerOnDamaged;
            health.Died -= ServerOnDied;
            hp.OnValueChanged -= OnHpChanged;
            armor.OnValueChanged -= OnArmorChanged;
        }

        // ---------------------------------------------------------------- server API
        public void ServerHeal(float amount)
        {
            if (!IsServer) return;
            health.Heal(amount);
            hp.Value = health.Current;
        }

        public void ServerAddArmor(float amount)
        {
            if (!IsServer) return;
            health.AddArmor(amount);
            armor.Value = health.Armor;
        }

        /// <summary>Full heal, no armor, moved to a spawn point and animator reset (match start / reset, respawn).</summary>
        public void ServerReviveAt(Vector3 position, float yaw)
        {
            if (!IsServer) return;
            health.ResetHealth();
            hp.Value = health.Max;
            armor.Value = 0f;
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } } };
            RespawnClientRpc(position, yaw, target);
        }

        // ---------------------------------------------------------------- server events
        void ServerOnDamaged(float amount, GameObject attacker)
        {
            if (MatchManager.Instance != null) MatchManager.Instance.ServerOnDamage(GetComponent<NetworkPlayer>(), attacker, amount);
            var attackerObj = attacker != null ? attacker.GetComponentInParent<NetworkObject>() : null;
            lastAttacker.Value = attackerObj != null ? attackerObj.NetworkObjectId : ulong.MaxValue;
            hp.Value = health.Current;
            armor.Value = health.Armor;
        }

        void ServerOnDied(GameObject attacker)
        {
            if (MatchManager.Instance != null) MatchManager.Instance.ServerOnPlayerDied(GetComponent<NetworkPlayer>(), attacker);
            if (MatchManager.Instance == null || MatchManager.Instance.State != MatchState.Playing)
                StartCoroutine(RespawnAfterDelay());
        }

        IEnumerator RespawnAfterDelay()
        {
            yield return new WaitForSeconds(respawnDelay);
            if (MatchManager.Instance != null && MatchManager.Instance.State == MatchState.Playing) yield break;   // match started meanwhile
            var spawn = NetworkBootstrap.GetSpawnPoint();
            ServerReviveAt(spawn.position, spawn.eulerAngles.y);
        }

        [ClientRpc]
        void RespawnClientRpc(Vector3 position, float yaw, ClientRpcParams rpcParams = default)
        {
            if (!IsOwner) return;
            GetComponent<PlayerController>().Teleport(position, yaw);
            GetComponent<PlayerAnimator>().Revive();
        }

        // ---------------------------------------------------------------- clients
        void OnHpChanged(float previous, float current)
        {
            GameObject attacker = null;
            if (lastAttacker.Value != ulong.MaxValue &&
                NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(lastAttacker.Value, out var obj))
                attacker = obj.gameObject;
            health.SyncTo(current, attacker);
        }

        void OnArmorChanged(float previous, float current) => health.SyncArmor(current);
    }
}
