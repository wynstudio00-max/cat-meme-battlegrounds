using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// Connection menu (IMGUI), card picker and connection approval.
    /// Approval validates the chosen pass card through <see cref="ICardService"/> (mock for now), reveals the card's
    /// character on first use, locks the card to one session, and spawns the matching character prefab on a spawn point.
    /// Host = server and player in one process (editor only: WebGL cannot host); Server = dedicated; Client joins by IP.
    /// Command line (builds / second editor): -host, -server, -client, -address 192.168.x.x, -port 7777, -card 301
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        [SerializeField] CharacterCatalog catalog;
        [SerializeField] GameObject matchSystemPrefab;   // MatchManager + zone + loot spawner, spawned when the server starts
        [SerializeField] int maxPlayers = 10;   // humans per match; bots fill the rest of the 10 slots
        [SerializeField] ushort port = 7777;
        [SerializeField] string address = "127.0.0.1";
        [Tooltip("Editor / development builds: read the pass cards of this wallet (any address, read-only) without a browser wallet. Command line -wallet 0x...")]
        [SerializeField] string devWallet = "";
        [Tooltip("Testing without the chain: every wallet 'owns' the five MockChain cards (reveals stored locally).")]
        [SerializeField] bool useMockCards = false;
        [Tooltip("wss:// (TLS) for the client. The server itself stays plain ws behind a TLS proxy such as Caddy.")]
        [SerializeField] bool secureConnection = false;
        [SerializeField] string webSocketPath = "/";
        [Tooltip("Browser build plays single-player offline (host + bots on OfflineTransport). Page URL ?online=1 turns it off.")]
        [SerializeField] bool offlineInBrowser = true;
        [Tooltip("Editor / desktop: host on OfflineTransport instead of the WebSocket transport (command line -offline).")]
        [SerializeField] bool offlineHost = false;

        // A browser (WebGL) build cannot listen for connections, so it can never run a server for others.
        static bool CanListen => Application.platform != RuntimePlatform.WebGLPlayer;
        // Hosting = a match running in this process: always outside the browser, in the browser only offline (alone).
        static bool CanHost => CanListen || (instance != null && instance.Offline);

        /// <summary>Single-player: host alone on <see cref="OfflineTransport"/> (nobody can join, no socket).</summary>
        public bool Offline { get; private set; }

        /// <summary>Game mode the next hosted match runs (picked in the lobby, remembered in PlayerPrefs "gameMode").</summary>
        public MatchMode SelectedMode
        {
            get => (MatchMode)PlayerPrefs.GetInt("gameMode", (int)MatchMode.BattleRoyale);
            set => PlayerPrefs.SetInt("gameMode", (int)value);
        }
        public static bool OfflineHere => instance != null && instance.Offline;

        static NetworkBootstrap instance;
        readonly List<Transform> spawnPoints = new List<Transform>();
        int nextSpawn;
        string status = "";

        // server side
        ICardService cardService;
        readonly Dictionary<ulong, CardResolveResult> results = new Dictionary<ulong, CardResolveResult>();
        readonly HashSet<string> cardsInUse = new HashSet<string>();

        // client side
        IWalletCardsProvider walletProvider;
        List<PassCard> myCards = new List<PassCard>();   // mock cards only; real ones live in WalletSession
        string selectedToken;        // empty = original character; remembered in PlayerPrefs "selectedCard"
        bool hostStarting;           // the host's own card is being checked on the chain
        string testCharacter;        // testing: play any cat without a card (editor / development builds only)

        void Awake()
        {
            instance = this;
            if (PlayerPrefs.HasKey("testCharacter")) PlayerPrefs.DeleteKey("testCharacter");   // old picker choice
            Offline = Application.platform == RuntimePlatform.WebGLPlayer ? offlineInBrowser : offlineHost;
        }

        // ---------------------------------------------------------------- API for the menu UI (GameUI)
        public static NetworkBootstrap Instance => instance;
        public static bool CanHostHere => CanHost;
        public static bool CanListenHere => CanListen;
        public string Address { get => address; set => address = value; }
        public IReadOnlyList<PassCard> Cards => useMockCards ? myCards : WalletSession.Cards;
        public bool MockCards => useMockCards;
        public string SelectedCard
        {
            get { if (selectedToken == null) selectedToken = PlayerPrefs.GetString("selectedCard", ""); return selectedToken; }
            set { selectedToken = value ?? ""; PlayerPrefs.SetString("selectedCard", selectedToken); }
        }
        /// <summary>The selected card if it is in the wallet and opened (else default).</summary>
        public bool TryGetSelected(out PassCard card)
        {
            card = default;
            if (string.IsNullOrEmpty(SelectedCard)) return false;
            foreach (var c in Cards) if (c.tokenId == SelectedCard) { card = c; return true; }
            return false;
        }
        /// <summary>Wallet sent with the card choice ("" = none).</summary>
        string CurrentWallet => useMockCards ? "mock" : WalletSession.Address;
        /// <summary>Editor / development builds only: wallet whose cards are read without a browser wallet.</summary>
        public string DevWallet => TestPickerAllowed ? devWallet : "";
        public string Status => status;

        /// <summary>The test picker exists only in the editor and in development builds; a release server ignores it.</summary>
        public static bool TestPickerAllowed => Application.isEditor || Debug.isDebugBuild;
/// <summary>Catalog id to play without a card ("" = use the pass card / original cat). Only from the command line
        /// (-cat ninja) in editor / development builds; the in-game picker is gone (2026-11-06) and the old remembered
        /// PlayerPrefs value is cleared, because it silently overrode the selected pass card.</summary>
        public string TestCharacter
        {
            get => TestPickerAllowed ? testCharacter ?? "" : "";
            set => testCharacter = value ?? "";
        }
        public IReadOnlyList<CharacterCatalog.Entry> Characters =>
            catalog != null ? catalog.entries : (IReadOnlyList<CharacterCatalog.Entry>)Array.Empty<CharacterCatalog.Entry>();

        bool TryTestResult(string id, out CardResolveResult result)
        {
            result = default;
            if (!TestPickerAllowed || string.IsNullOrEmpty(id) || catalog == null) return false;
            var entry = catalog.Get(id);
            if (entry == null || entry.playerPrefab == null) return false;
            result = new CardResolveResult { ok = true, tokenId = "", tier = entry.tier, characterId = entry.id };
            return true;
        }
        public int MaxPlayers => maxPlayers;
        public void UiHost() { if (CanHost) StartHost(NetworkManager.Singleton); }
        public void UiJoin() => StartClient(NetworkManager.Singleton);
        public void UiServer() { if (CanListen) StartServer(NetworkManager.Singleton); }
        public void UiDisconnect()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null) nm.Shutdown();
            cardsInUse.Clear(); results.Clear(); status = "";
        }

        void Start()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) { Debug.LogError("NetworkBootstrap: no NetworkManager in the scene."); return; }

            // the browser needs the card service too: it hosts its own offline match
            if (useMockCards)
            {
                cardService = new MockCardService(catalog);
                walletProvider = new MockWalletCardsProvider();
                walletProvider.GetCards("mock", cards => myCards = cards);
            }
            else
            {
                var chain = new ChainCardService(catalog);
                cardService = chain;
                walletProvider = chain;
                WalletSession.Changed += ForgetUnusableSelection;
                if (Application.platform != RuntimePlatform.WebGLPlayer && ChainCards.IsAddress(DevWallet)) WalletSession.SetAddress(DevWallet);
            }

            nm.ConnectionApprovalCallback = Approve;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            nm.OnServerStarted += SpawnMatchSystem;
            RefreshSpawnPoints();
            ApplyCommandLine(nm);
            ApplyUrlParameters(nm);
        }

        void OnDestroy() => WalletSession.Changed -= ForgetUnusableSelection;

        /// <summary>Once the wallet's cards are known, drop a remembered card that is gone (sold / other wallet).</summary>
        void ForgetUnusableSelection()
        {
            if (WalletSession.Loading || string.IsNullOrEmpty(SelectedCard)) return;
            if (!WalletSession.Connected || (string.IsNullOrEmpty(WalletSession.Error) && !TryGetSelected(out _))) SelectedCard = "";
        }

        /// <summary>WebGL: take the server from the page URL, e.g. index.html?host=play.example.com&amp;port=443&amp;secure=1&amp;card=301&amp;autoconnect=1</summary>
        void SpawnMatchSystem()
        {
            if (matchSystemPrefab == null) { Debug.LogWarning("NetworkBootstrap: no match system prefab assigned; running without a battle-royale match."); return; }
            var go = Instantiate(matchSystemPrefab);
            go.GetComponent<NetworkObject>().Spawn();
        }

        void ApplyUrlParameters(NetworkManager nm)
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer) return;
            var url = Application.absoluteURL;
            int q = url.IndexOf('?');
            if (q < 0) return;
            bool auto = false;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2) continue;
                var value = Uri.UnescapeDataString(kv[1]);
                switch (kv[0])
                {
                    case "host": address = value; break;
                    case "port": if (ushort.TryParse(value, out var p)) port = p; break;
                    case "secure": secureConnection = value == "1" || value == "true"; break;
                    case "path": webSocketPath = value; break;
                    case "card": SelectedCard = value; break;
                    case "wallet" when TestPickerAllowed: WalletSession.SetAddress(value); break;   // dev builds: view any wallet's cards
                    case "autoconnect": auto = value == "1" || value == "true"; break;
                    case "online": if (value == "1" || value == "true") Offline = false; break;
                }
            }
            if (auto && !Offline) StartClient(nm);
        }

        void ApplyCommandLine(NetworkManager nm)
        {
            if (!CanHost && Application.platform == RuntimePlatform.WebGLPlayer) return;
            var args = Environment.GetCommandLineArgs();
            string mode = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-host": mode = "host"; break;
                    case "-server": mode = "server"; break;
                    case "-client": mode = "client"; break;
                    case "-secure": secureConnection = true; break;
                    case "-offline": Offline = true; break;
                    case "-path" when i + 1 < args.Length: webSocketPath = args[i + 1]; break;
                    case "-maxplayers" when i + 1 < args.Length && int.TryParse(args[i + 1], out var mp): maxPlayers = mp; break;
                    case "-address" when i + 1 < args.Length: address = args[i + 1]; break;
                    case "-card" when i + 1 < args.Length: SelectedCard = args[i + 1]; break;
                    case "-wallet" when i + 1 < args.Length && TestPickerAllowed && !useMockCards: WalletSession.SetAddress(args[i + 1]); break;
                    case "-cat" when i + 1 < args.Length: TestCharacter = args[i + 1].ToLowerInvariant(); break;
                    case "-port" when i + 1 < args.Length && ushort.TryParse(args[i + 1], out var p): port = p; break;
                }
            }
            if (mode == "host") StartHost(nm);
            else if (mode == "server") StartServer(nm);
            else if (mode == "client") StartClient(nm);
        }

        // ---------------------------------------------------------------- spawning
        void RefreshSpawnPoints()
        {
            spawnPoints.Clear();
            foreach (var sp in FindObjectsByType<SpawnPoint>()) spawnPoints.Add(sp.transform);
            spawnPoints.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            nextSpawn = 0;
        }

        /// <summary>Next spawn point in rotation (also used for respawns).</summary>
        public static Transform GetSpawnPoint()
        {
            if (instance == null || instance.spawnPoints.Count == 0) return NetworkManager.Singleton.transform;
            var t = instance.spawnPoints[instance.nextSpawn % instance.spawnPoints.Count];
            instance.nextSpawn++;
            return t;
        }

        /// <summary>Server only: the card result chosen for this client (consumed once, when its player spawns).</summary>
        public static bool TryTakeResult(ulong clientId, out CardResolveResult result)
        {
            result = default;
            if (instance == null || !instance.results.TryGetValue(clientId, out result)) return false;
            return true;
        }

        void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            var nm = NetworkManager.Singleton;
            // NGO runs this callback for the host too (synchronously, with an empty payload). The host's card was already
            // resolved in StartHost -> BeginHost: use that result, never resolve again (that gave the Original cat and
            // overwrote the host's chosen card, 2026-11-06).
            if (request.ClientNetworkId == NetworkManager.ServerClientId && results.TryGetValue(NetworkManager.ServerClientId, out var hostResult))
            {
                var hostEntry = catalog.Get(hostResult.characterId);
                var hostPrefab = hostEntry != null && hostEntry.playerPrefab != null ? hostEntry.playerPrefab.GetComponent<NetworkObject>() : null;
                var hostSpawn = GetSpawnPoint();
                response.Approved = true;
                response.CreatePlayerObject = true;
                response.PlayerPrefabHash = hostPrefab != null ? hostPrefab.PrefabIdHash : (uint?)null;
                response.Position = hostSpawn.position;
                response.Rotation = hostSpawn.rotation;
                return;
            }
            if (nm.ConnectedClientsList.Count >= maxPlayers) { Reject(response, "Server is full."); return; }
            if (MatchManager.Instance != null && !MatchManager.Instance.AcceptingPlayers) { Reject(response, "A match is in progress. Try again in a minute."); return; }

            var payload = new ConnectPayload();
            if (request.Payload != null && request.Payload.Length > 0)
            {
                try { payload = JsonUtility.FromJson<ConnectPayload>(Encoding.UTF8.GetString(request.Payload)) ?? payload; }
                catch (Exception) { Reject(response, "Bad connection data."); return; }
            }

            ulong clientId = request.ClientNetworkId;
            response.Pending = true;   // allows an async (backend) card check
            if (TryTestResult(payload.testCharacter, out var testResult)) { OnResolved(testResult); return; }
            cardService.Resolve(payload.wallet, payload.tokenId, OnResolved);

            void OnResolved(CardResolveResult result)
            {
                response.Pending = false;
                if (!result.ok) { Reject(response, result.error); return; }
                if (!string.IsNullOrEmpty(result.tokenId) && !cardsInUse.Add(result.tokenId))
                { Reject(response, "That card is already in use in another session."); return; }

                var entry = catalog.Get(result.characterId);
                var prefabObject = entry != null && entry.playerPrefab != null ? entry.playerPrefab.GetComponent<NetworkObject>() : null;
                if (prefabObject == null)
                {
                    if (!string.IsNullOrEmpty(result.tokenId)) cardsInUse.Remove(result.tokenId);
                    Reject(response, "Character prefab missing for '" + result.characterId + "'.");
                    return;
                }

                var spawn = GetSpawnPoint();
                results[clientId] = result;
                response.Approved = true;
                response.CreatePlayerObject = true;
                response.PlayerPrefabHash = prefabObject.PrefabIdHash;
                response.Position = spawn.position;
                response.Rotation = spawn.rotation;
            }
        }

        static void Reject(NetworkManager.ConnectionApprovalResponse response, string reason)
        {
            response.Pending = false;
            response.Approved = false;
            response.Reason = reason;
        }

        void OnClientDisconnected(ulong clientId)
        {
            if (results.TryGetValue(clientId, out var r))
            {
                if (!string.IsNullOrEmpty(r.tokenId)) cardsInUse.Remove(r.tokenId);
                results.Remove(clientId);
            }
            var nm = NetworkManager.Singleton;
            if (!nm.IsServer && clientId == nm.LocalClientId)
                status = string.IsNullOrEmpty(nm.DisconnectReason) ? "Disconnected from server." : nm.DisconnectReason;
        }

        // ---------------------------------------------------------------- start / stop
        void ConfigureTransport(NetworkManager nm, bool listen)
        {
            var transport = nm.GetComponent<UnityTransport>();
            nm.NetworkConfig.NetworkTransport = transport;        // back from OfflineTransport if an offline match ran before
            transport.UseWebSockets = true;                      // WebGL can only speak WebSocket, so every client does
            transport.UseEncryption = secureConnection && !listen; // wss for clients; the server sits behind a TLS proxy
            transport.ConnectionData.WebSocketPath = webSocketPath;
            transport.SetConnectionData(address, port, listen ? "0.0.0.0" : null);
            if (!listen && secureConnection) transport.SetClientSecrets(address);
        }

        void StartHost(NetworkManager nm)
        {
            // The host skips connection approval, so resolve its own card here and pick its prefab up front.
            // The chain check is asynchronous (a web request); the mock answers at once.
            if (hostStarting || nm.IsListening) return;
            if (TryTestResult(TestCharacter, out var test)) { BeginHost(nm, test); return; }
            hostStarting = true;
            if (!string.IsNullOrEmpty(SelectedCard)) status = "Checking card #" + SelectedCard + " ...";
            cardService.Resolve(CurrentWallet, SelectedCard, r => { hostStarting = false; BeginHost(nm, r); });
        }

        void BeginHost(NetworkManager nm, CardResolveResult resolved)
        {
            if (nm.IsListening) return;
            if (!resolved.ok) { status = resolved.error; return; }
            status = "";
            var entry = catalog.Get(resolved.characterId);
            if (entry == null || entry.playerPrefab == null) { status = "Character prefab missing for '" + resolved.characterId + "'."; return; }
            if (!string.IsNullOrEmpty(resolved.tokenId)) cardsInUse.Add(resolved.tokenId);

            nm.NetworkConfig.PlayerPrefab = entry.playerPrefab;
            results[NetworkManager.ServerClientId] = resolved;
            if (Offline)
            {
                var offline = nm.GetComponent<OfflineTransport>();
                if (offline == null) offline = nm.gameObject.AddComponent<OfflineTransport>();
                nm.NetworkConfig.NetworkTransport = offline;
                status = nm.StartHost() ? "Offline match" : "Could not start the offline match.";
                return;
            }
            ConfigureTransport(nm, true);
            status = nm.StartHost() ? "Hosting on port " + port : "Could not start host.";
        }

        void StartServer(NetworkManager nm)
        {
            Application.targetFrameRate = 30;   // matches the 30 Hz network tick; keeps a headless server cool
            QualitySettings.vSyncCount = 0;
            ConfigureTransport(nm, true);
            status = nm.StartServer() ? "Dedicated server (WebSocket) on port " + port : "Could not start server.";
            Debug.Log("[Server] " + status);
        }

        void StartClient(NetworkManager nm)
        {
            var payload = new ConnectPayload { wallet = CurrentWallet, tokenId = SelectedCard, sessionToken = "", testCharacter = TestPickerAllowed ? TestCharacter : "" };
            nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            ConfigureTransport(nm, false);
            status = nm.StartClient() ? "Connecting to " + address + ":" + port + " ..." : "Could not start client.";
        }

        // ---------------------------------------------------------------- UI
        void OnGUI()
        {
            if (GameUI.Active) return;   // the uGUI menu replaces this placeholder
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            const float w = 280f;
            GUILayout.BeginArea(new Rect(12, 12, w, 520));
            if (!nm.IsClient && !nm.IsServer)
            {
                GUILayout.Label("CAT BATTLEGROUND");
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP", GUILayout.Width(24));
                address = GUILayout.TextField(address);
                GUILayout.EndHorizontal();

                GUILayout.Label("Pass card (the card draws its cat on first use):");
                if (GUILayout.Toggle(SelectedCard == "", "No card - original cat")) SelectedCard = "";
                foreach (var card in Cards)
                    if (GUILayout.Toggle(SelectedCard == card.tokenId, "Card #" + card.tokenId + "  [" + card.tier + "] " + (card.Playable ? card.characterId : "sealed")) && card.Playable) SelectedCard = card.tokenId;

                if (CanHost && GUILayout.Button("Host (server + player)")) StartHost(nm);
                if (GUILayout.Button("Join as client")) StartClient(nm);
                if (CanListen && GUILayout.Button("Dedicated server")) StartServer(nm);
            }
            else
            {
                string role = nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";
                GUILayout.Label(role + " | players: " + FindObjectsByType<NetworkPlayer>().Length + "/" + maxPlayers);
                if (nm.IsClient && !nm.IsConnectedClient) GUILayout.Label("Connecting ...");
                if (GUILayout.Button("Disconnect")) { nm.Shutdown(); cardsInUse.Clear(); results.Clear(); status = ""; }
            }
            if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
            GUILayout.EndArea();
        }
    }
}
