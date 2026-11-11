// Owner: hands the reveal job to another wallet (e.g. the auto-reveal Worker's operator).
// usage: $env:OPERATOR = "0x..."; npx hardhat run scripts/set_operator.js --network robinhoodTestnet
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const file = path.join(__dirname, "..", "deployments", `${network.name}.json`);
  const dep = JSON.parse(fs.readFileSync(file, "utf8"));
  const [me] = await ethers.getSigners();
  if (!me) throw new Error("Set $env:RH_TESTNET_KEY first (contract owner).");
  if (!ethers.isAddress(process.env.OPERATOR || "")) throw new Error('Set $env:OPERATOR = "0x..." (address printed by Backend/reveal/deploy)');
  const op = ethers.getAddress(process.env.OPERATOR);
  const src = await ethers.getContractAt("HashChainRandomSource", dep.HashChainRandomSource, me);
  if ((await src.owner()) !== me.address) throw new Error("Signer is not the random source owner");
  const tx = await src.setOperator(op);
  console.log("setOperator tx", tx.hash);
  await tx.wait();
  dep.operator = op;
  fs.writeFileSync(file, JSON.stringify(dep, null, 2));
  console.log("operator is now", await src.operator());
}

main().catch((e) => {
  console.error(e.shortMessage || e.message || e);
  process.exitCode = 1;
});
