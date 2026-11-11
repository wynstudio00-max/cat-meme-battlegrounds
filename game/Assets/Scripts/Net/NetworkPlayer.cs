using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Per-player network setup. Movement is owner-authoritative (NetworkTransform in Owner mode),
    /// so only the owning client runs input, camera, HUD and the CharacterController.
    /// Everyone else sees an interpolated puppet with a trigger capsule that the server can raycast against.
    /// Also carries which character (and card tier) this player got from the pass-card check.
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        readonly NetworkVariable<FixedString32Bytes> characterId = new NetworkVariable<FixedString32Bytes>();
        readonly NetworkVariable<int> tier = new NetworkVariable<int>();
        readonly NetworkVariable<int> teamId = new NetworkVariable<int>(-1);   // set by the server when a match starts
        // match stats for the HUD / end screens (server writes, everyone reads)
        readonly NetworkVariable<int> number = new NetworkVariable<int>(0);         // anonymous "Player N" for this match
        readonly NetworkVariable<int> kills = new NetworkVariable<int>(0);
        readonly NetworkVariable<int> damage = new NetworkVariable<int>(0);
        readonly NetworkVariable<int> placement = new NetworkVariable<int>(0);      // 0 = still in the match
        readonly NetworkVariable<int> killedBy = new NetworkVariable<int>(-1);      // killer's number, 0 = the zone, -1 = not dead
        readonly NetworkVariable<int> killerWeapon = new NetworkVariable<int>(-1);
        readonly NetworkVariable<float> survived = new NetworkVariable<float>(0f);  // seconds from take-off to elimination / win
        readonly NetworkVariable<int> headshots = new NetworkVariable<int>(0);      // shots that hit a head (results card)
        Health health;
        float damageExact;

        public int Number => number.Value;
        public int Kills => kills.Value;
        public int Damage => damage.Value;
        public int Placement => placement.Value;
        public int KilledBy => killedBy.Value;
        public int KillerWeapon => killerWeapon.Value;
        public float Survived => survived.Value;
        public int Headshots => headshots.Value;
        public void ServerAddHeadshot() { if (IsServer) headshots.Value++; }

        public void ServerResetStats()
        {
            if (!IsServer) return;
            kills.Value = 0; headshots.Value = 0; damage.Value = 0; damageExact = 0f; placement.Value = 0; killedBy.Value = -1; killerWeapon.Value = -1; survived.Value = 0f;
        }
        public void ServerSetNumber(int n) { if (IsServer) number.Value = n; }
        public void ServerAddKill() { if (IsServer) kills.Value++; }
        public void ServerAddDamage(float amount) { if (!IsServer) return; damageExact += amount; damage.Value = Mathf.RoundToInt(damageExact); }
        public void ServerSetOut(int place, int killer, int weapon, float seconds)
        {
            if (!IsServer) return;
            placement.Value = place; killedBy.Value = killer; killerWeapon.Value = weapon; survived.Value = seconds;
        }

        public Health Health => health != null ? health : (health = GetComponent<Health>());
        public int TeamId => teamId.Value >= 0 ? teamId.Value : (int)OwnerClientId + 1000;   // lobby: everyone is their own team
        public void ServerSetTeam(int id) { if (IsServer) teamId.Value = id; }

        /// <summary>True only on the server, for AI-controlled players. Never replicated, so clients cannot tell bots from people.</summary>
        public bool IsBot { get; private set; }

        public string CharacterId => characterId.Value.ToString();
        public CharacterTier Tier => (CharacterTier)tier.Value;

        /// <summary>Message shown on the HUD for a few seconds (card reveal).</summary>
        public string Banner { get; private set; }
        public float BannerUntil { get; private set; }

        public void ServerSetCharacter(string id, CharacterTier characterTier)
        {
            if (!IsServer) return;
            characterId.Value = id;
            tier.Value = (int)characterTier;
            GetComponent<PlayerShooter>().ServerResetLoadout();   // each cat carries its own weapon
        }

        public override void OnNetworkSpawn()
        {
            IsBot = IsServer && GetComponent<BotBrain>() != null;
            bool owner = IsOwner && !IsBot;   // a bot is owned by the server but nobody plays it
            gameObject.name = "Player_" + OwnerClientId + (owner ? " (you)" : "");

            var controller = GetComponent<PlayerController>();
            var cc = GetComponent<CharacterController>();
            var hud = GetComponent<SimpleHud>();
            var tester = GetComponent<PlayerAnimationTester>();

            controller.enabled = owner;
            cc.enabled = owner;                  // a CharacterController fights NetworkTransform on puppets (and the bot's NavMeshAgent)                  // a CharacterController fights NetworkTransform on puppets
            if (hud != null) hud.enabled = owner;
            if (tester != null) tester.enabled = false;   // debug keys are offline-only
            var spectate = GetComponent<SpectateController>();
            if (spectate != null) spectate.enabled = owner;

            if (owner)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var follow = cam.GetComponent<ThirdPersonCamera>();
                    if (follow == null) follow = cam.gameObject.AddComponent<ThirdPersonCamera>();
                    follow.SetTarget(controller);
                }
            }

            if (IsServer && !IsBot && NetworkBootstrap.TryTakeResult(OwnerClientId, out var result))
            {
                characterId.Value = result.characterId;
                tier.Value = (int)result.tier;
                GetComponent<PlayerShooter>().ServerResetLoadout();
                CardInfoRpc(result.tier.ToString(), result.characterId, result.tokenId, result.newlyRevealed);
            }
        }

        [Rpc(SendTo.Owner)]
        void CardInfoRpc(string tierName, string character, string tokenId, bool isNew)
        {
            Banner = string.IsNullOrEmpty(tokenId)
                ? "No card - playing as " + character
                : (isNew ? "Card #" + tokenId + " [" + tierName + "] revealed: " + character + "!" : "Card #" + tokenId + " [" + tierName + "]: " + character);
            BannerUntil = Time.time + 6f;
        }
    }
}
