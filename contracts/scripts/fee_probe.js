// Read-only: current gas price + L1 data component on Robinhood Chain (Arbitrum Orbit NodeInterface at 0xC8),
// plus the real receipts of the testnet claim / open / reveal transactions. No keys, no transactions.
// usage: node scripts/fee_probe.js
const { JsonRpcProvider, Contract, ZeroAddress, hexlify, randomBytes } = require("ethers");
const { artifacts } = require("hardhat");

const NODE_INTERFACE = "0x00000000000000000000000000000000000000C8";
const NI_ABI = ["function gasEstimateL1Component(address to, bool contractCreation, bytes data) payable returns (uint64 gasEstimateForL1, uint256 baseFee, uint256 l1BaseFeeEstimate)"];

async function probe(name, rpc, id) {
  const p = new JsonRpcProvider(rpc, id, { staticNetwork: true });
  const ni = new Contract(NODE_INTERFACE, NI_ABI, p);
  const fee = await p.getFeeData();
  const block = await p.getBlock("latest");
  const card = await artifacts.readArtifact("CMBGPassCard");
  const sizes = { deployCard: 13187 + 160, deploySource: 2623 + 128, claim: 484, open: 36, reveal1: 100, reveal40: 68 + 40 * 32 };
  const l1 = {};
  for (const [k, bytes] of Object.entries(sizes)) {
    const r = await ni.gasEstimateL1Component.staticCall(k.startsWith("deploy") ? ZeroAddress : "0x000000000000000000000000000000000000dEaD", k.startsWith("deploy"), hexlify(randomBytes(bytes)));
    l1[k] = Number(r[0]);
    l1._baseFeeGwei = Number(r[1]) / 1e9;
    l1._l1BaseFeeGwei = Number(r[2]) / 1e9;
  }
  return { name, gasPriceGwei: Number(fee.gasPrice) / 1e9, baseFeeGwei: Number(block.baseFeePerGas) / 1e9, l1GasUnits: l1 };
}

async function receipts() {
  const p = new JsonRpcProvider("https://rpc.testnet.chain.robinhood.com", 46630, { staticNetwork: true });
  const txs = {
    claim2_proof1: "0xfe3bc173c7af688c1493f9d2f21d0f66ef57f652747f18153283f12aab8ecc4f",
    open: "0x7784a02fd01fd4796ed61e9842ff74c6d6cca42d7e0217db278fb1544f5680e4",
  };
  const out = {};
  for (const [k, h] of Object.entries(txs)) {
    const r = await p.send("eth_getTransactionReceipt", [h]);
    out[k] = { gasUsed: parseInt(r.gasUsed, 16), gasUsedForL1: r.gasUsedForL1 ? parseInt(r.gasUsedForL1, 16) : null, priceGwei: parseInt(r.effectiveGasPrice, 16) / 1e9, feeEth: (parseInt(r.gasUsed, 16) * parseInt(r.effectiveGasPrice, 16)) / 1e18 };
  }
  return out;
}

(async () => {
  const res = {
    mainnet: await probe("mainnet", "https://rpc.mainnet.chain.robinhood.com", 4663),
    testnet: await probe("testnet", "https://rpc.testnet.chain.robinhood.com", 46630),
    testnetReceipts: await receipts(),
  };
  console.log(JSON.stringify(res, null, 1));
})().catch((e) => { console.error(e.shortMessage || e.message); process.exitCode = 1; });
