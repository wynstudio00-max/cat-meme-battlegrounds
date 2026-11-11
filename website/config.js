// Site settings. Edit these before launch; empty values show a "coming soon" toast instead of a dead link.
window.CMBG_CONFIG = {
  // Contract address. Leave empty until launch (the page shows "[CA — DROPPING AT LAUNCH]").
  ca: "0x5031600A85A0176d440f2662bCa3190E025f1A2f",

  // Burned amount shown in the tokenomics block. Number, or "" to keep the placeholder.
  burned: "",

  links: {
    buy: "https://ponsfamily.com/launchpad/0x5031600a85a0176d440f2662bca3190e025f1a2f",        // Pons trade page for CMBG
    claim: "cards.html",   // NFT claim page (pass cards, mainnet since 2026-11-08)
    play: "https://play.catbattlegrounds.com",   // WebGL beta (Worker cmbg-play, Backend/play)
    trailer: "https://x.com/Cmbg_RH/status/2116125427525943625",   // WATCH TRAILER buttons open the teaser post on X (2026-11-08)
    x: "https://x.com/Cmbg_RH",   // official profile (user, 2026-11-08; was CMBG_HOOD)
    telegram: "",
    dexscreener: ""
  },

  // Buttons that pop a padlock + "locked" toast instead of working. Keys: "play" (PLAY / PLAY BETA), "trailer" (WATCH TRAILER),
  // "pack" (OPEN A PACK). Remove a key when that part is ready.
  locked: [],   // 2026-11-08: "pack" plays the Legendary opening film, "trailer" opens links.trailer (X post)

  // Live leaderboard section: the game's leaderboard API (Backend/leaderboard). Tried in order; the first one that answers is kept.
  leaderboard: {
    api: ["https://api.catbattlegrounds.com", "https://cmbg-leaderboard.wynstudio00.workers.dev"],
    refreshSeconds: 20,
    rows: 20
  },

  // Pass-card page (cards.html, cards.js). deploy: false keeps cards.html out of Website_deploy (Tools/Website/make_deploy.py)
  // until the cards go live. `network` picks one of `networks` (2026-11-07: one switch for testnet -> mainnet).
  // Addresses from Contracts/deployments/<network>.json; claims = a copy of Contracts/snapshot/out/claims.json.
  nft: {
    deploy: true,
    network: "mainnet",         // "testnet" | "mainnet" (make_deploy.py refuses mainnet while its card address is empty)
    ipfsGateway: "https://gateway.pinata.cloud/ipfs/",
    pollSeconds: 4,
    networks: {
      testnet: {
        chainId: 46630,
        chainName: "Robinhood Chain Testnet",
        rpc: "https://rpc.testnet.chain.robinhood.com",
        explorer: "https://explorer.testnet.chain.robinhood.com",
        card: "0xc6f7DF85195613f0514Dc9f3Eec2eD32Fa6110A7",
        claims: "nft/claims_testnet.json",
        // auto-reveal Worker (Backend/reveal): the page POSTs /poke after an open
        revealApi: "https://cmbg-reveal.wynstudio00.workers.dev"
      },
      // RPC + explorer: docs.robinhood.com/chain/connecting (checked 2026-11-07). Fill card after the mainnet deploy
      // (Contracts/deployments/robinhoodMainnet.json) and claims after the real snapshot.
      mainnet: {
        chainId: 4663,
        chainName: "Robinhood Chain",
        rpc: "https://rpc.mainnet.chain.robinhood.com",   // also given to wallets when they add the chain
        // reads try these in order (2026-11-09: some phones / ISPs could not reach the public RPC -> "Could not read the chain")
        // (Pinax is not usable from a browser: it sends "access-control-allow-origin: *,*", which browsers reject; our proxy
        // uses Pinax on the server side instead.)
        rpcs: ["https://rpc.mainnet.chain.robinhood.com", "https://api.catbattlegrounds.com/v1/chain?net=mainnet",
               "https://cmbg-leaderboard.wynstudio00.workers.dev/v1/chain?net=mainnet"],
        explorer: "https://robinhoodchain.blockscout.com",
        card: "0x2bD993948bd2A202B0d8904062fadf6f521Fb533",   // deployed 2026-11-08 (snapshot block 109243673)
        claims: "nft/claims_mainnet.json",
        revealApi: "https://reveal.catbattlegrounds.com"
      }
    }
  },

  // Block explorer for the "VIEW ON EXPLORER" link under the CA (shown once `ca` is set).
  // = the mainnet explorer in docs.robinhood.com/chain/connecting (checked 2026-11-07).
  network: {
    explorerUrl: "https://robinhoodchain.blockscout.com"
  }
};
