require("@nomicfoundation/hardhat-toolbox");

// Keys only from the environment (never in this file / the repo).
//   RH_TESTNET_KEY  testnet-only wallet
//   RH_MAINNET_KEY  FRESH mainnet deployer (never the testnet wallet 0x93C1...e33D, its key was exposed). Only the deployer:
//                   ownership goes to a multisig (OWNER) right after the deploy, see scripts/deploy.js.
const testnetKey = process.env.RH_TESTNET_KEY;
const mainnetKey = process.env.RH_MAINNET_KEY;

module.exports = {
  solidity: {
    version: "0.8.28",
    settings: {
      optimizer: { enabled: true, runs: 500 },
      // Robinhood Chain's EVM level is not documented; checked 2026-11-05 by eth_call on testnet + mainnet RPC:
      // PUSH0, MCOPY and TSTORE execute (INVALID is rejected) -> cancun. OpenZeppelin 5.6 needs mcopy.
      evmVersion: "cancun",
    },
  },
  networks: {
    robinhoodTestnet: {
      url: "https://rpc.testnet.chain.robinhood.com",
      chainId: 46630,
      accounts: testnetKey ? [testnetKey] : [],
    },
    // RPC + explorer from docs.robinhood.com/chain/connecting + deploy-smart-contracts (checked 2026-11-07; RPC answers 0x1237 = 4663)
    robinhoodMainnet: {
      url: "https://rpc.mainnet.chain.robinhood.com",
      chainId: 4663,
      accounts: mainnetKey ? [mainnetKey] : [],
    },
  },
  // Contract verification on Blockscout (Robinhood docs: --verifier blockscout); Blockscout needs no real API key.
  etherscan: {
    apiKey: { robinhoodTestnet: "blockscout", robinhoodMainnet: "blockscout" },
    customChains: [
      {
        network: "robinhoodTestnet",
        chainId: 46630,
        urls: {
          apiURL: "https://explorer.testnet.chain.robinhood.com/api",
          browserURL: "https://explorer.testnet.chain.robinhood.com",
        },
      },
      {
        network: "robinhoodMainnet",
        chainId: 4663,
        urls: {
          apiURL: "https://robinhoodchain.blockscout.com/api",
          browserURL: "https://robinhoodchain.blockscout.com",
        },
      },
    ],
  },
  sourcify: { enabled: false },
};
