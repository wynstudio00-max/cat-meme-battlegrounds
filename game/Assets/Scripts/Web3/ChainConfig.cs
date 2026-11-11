using System.Collections.Generic;

namespace CatBattleground
{
    /// <summary>
    /// Where the pass cards live (Contracts/, CMBGPassCard). ONE switch for the launch: set <see cref="UseMainnet"/> = true,
    /// fill <see cref="MainnetCardContract"/> from Contracts/deployments/robinhoodMainnet.json and rebuild. The game only READS
    /// the chain (eth_call cardsOf): first the public RPC, then our own read proxy (some ISPs block *.robinhood.com).
    /// </summary>
    public static class ChainConfig
    {
        public const bool UseMainnet = true;   // 2026-11-08: mainnet launch

        // testnet (deployed 2026-11-05)
        const long TestnetChainId = 46630;
        const string TestnetRpc = "https://rpc.testnet.chain.robinhood.com";
        const string TestnetCardContract = "0xc6f7DF85195613f0514Dc9f3Eec2eD32Fa6110A7";
        // mainnet: RPC from docs.robinhood.com/chain/connecting (checked 2026-11-07, answers chain 0x1237 = 4663)
        const long MainnetChainId = 4663;
        const string MainnetRpc = "https://rpc.mainnet.chain.robinhood.com";
        const string MainnetCardContract = "0x2bD993948bd2A202B0d8904062fadf6f521Fb533";   // deployed 2026-11-08

        public const bool Testnet = !UseMainnet;
        public const long ChainId = UseMainnet ? MainnetChainId : TestnetChainId;
        public const string Rpc = UseMainnet ? MainnetRpc : TestnetRpc;
        public const string CardContract = UseMainnet ? MainnetCardContract : TestnetCardContract;
        /// <summary>Read-only proxy on our own domains (Backend/leaderboard POST /v1/chain?net=...): only eth_call to the card
        /// contract. Tried when the public RPC does not answer.</summary>
        public static readonly string[] ProxyRpcs =
        {
            "https://api.catbattlegrounds.com/v1/chain?net=" + (UseMainnet ? "mainnet" : "testnet"),
            "https://cmbg-leaderboard.wynstudio00.workers.dev/v1/chain?net=" + (UseMainnet ? "mainnet" : "testnet"),
        };
        /// <summary>$CMBG token on Robinhood Chain mainnet (18 decimals). The lobby coin shows the wallet's balance (2026-11-10).
        /// Read on mainnet even while the cards run on testnet: the coin only exists there.</summary>
        public const string CmbgToken = "0x5031600a85a0176d440f2662bca3190e025f1a2f";
        /// <summary>Lobby coin click: buy CMBG on the Pons launchpad (same link as the website's BUY button).</summary>
        public const string BuyCmbgUrl = "https://ponsfamily.com/launchpad/0x5031600a85a0176d440f2662bca3190e025f1a2f";

        /// <summary>Website page where cards are claimed and opened (sealed cards must be opened there first).</summary>
        public const string CardsPage = "https://catbattlegrounds.com/cards.html";

        /// <summary>characterId stored on-chain (CMBGPassCard pools) -> catalog id. Same table as Contracts/scripts/make_metadata.js.</summary>
        public static readonly Dictionary<int, string> Characters = new Dictionary<int, string>
        {
            { 1, "gold" }, { 2, "soldier" }, { 3, "ninja" },   // 1 2 Common, 3 Rare (tiers swapped 2026-11-07, ids kept)
            { 4, "baby" }, { 5, "nobi" }, { 6, "black" },      // 4 Common, 5 6 Rare
            { 7, "white" }, { 8, "knight" },                   // Legendary
        };
    }
}
