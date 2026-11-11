using System;
using Unity.Netcode;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Transport for single-player offline play: the host runs alone (server + its own player), so nothing is ever
    /// sent or received and no socket is opened. This is what lets a browser (WebGL) build run a whole match
    /// (bots, zone, loot, zombies) locally, since WebGL cannot listen with UnityTransport.
    /// Clients cannot connect to it.
    /// </summary>
    public class OfflineTransport : NetworkTransport
    {
        public override ulong ServerClientId => 0;

        public override void Initialize(NetworkManager networkManager = null) { }
        public override bool StartServer() => true;
        public override bool StartClient() => false;

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery) { }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            clientId = 0;
            payload = default;
            receiveTime = Time.realtimeSinceStartup;
            return NetworkEvent.Nothing;
        }

        public override void DisconnectRemoteClient(ulong clientId) { }
        public override void DisconnectLocalClient() { }
        public override ulong GetCurrentRtt(ulong clientId) => 0;
        public override void Shutdown() { }
    }
}
