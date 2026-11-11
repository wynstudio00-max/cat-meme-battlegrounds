// Owner: points the claim at a new snapshot (snapshot/out/root.txt from scripts/snapshot.js).
// usage: npx hardhat run scripts/set_merkle_root.js --network robinhoodTestnet     (env ROOT overrides the file)
// Wallets that already claimed stay claimed (the contract remembers them), whatever the new root says.
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const file = path.join(__dirname, "..", "deployments", `${network.name}.json`);
  const dep = JSON.parse(fs.readFileSync(file, "utf8"));
  const [me] = await ethers.getSigners();
  if (!me) throw new Error("Set $env:RH_TESTNET_KEY first (contract owner).");
  const root = (process.env.ROOT || fs.readFileSync(path.join(__dirname, "..", "snapshot", "out", "root.txt"), "utf8")).trim();
  if (!/^0x[0-9a-fA-F]{64}$/.test(root)) throw new Error("root must be 0x + 64 hex");
  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard, me);
  if ((await card.merkleRoot()) === root) { console.log("root already set:", root); return; }
  const tx = await card.setMerkleRoot(root);
  console.log("setMerkleRoot tx", tx.hash);
  await tx.wait();
  dep.merkleRoot = root;
  fs.writeFileSync(file, JSON.stringify(dep, null, 2));
  console.log("merkleRoot is now", await card.merkleRoot());
  console.log("remember: copy snapshot/out/claims.json to Website/nft/claims_testnet.json (the page reads the proofs there)");
}

main().catch((e) => {
  console.error(e.shortMessage || e.message || e);
  process.exitCode = 1;
});
