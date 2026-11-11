// Local end-to-end check of deploy.js + reveal.js logic on the in-process Hardhat network (no keys, no RPC).
// usage: npx hardhat run scripts/localflow.js
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");
const { ethers } = require("hardhat");
const { StandardMerkleTree } = require("@openzeppelin/merkle-tree");
const { chain } = require("./hashchain");

async function main() {
  const [deployer, user] = await ethers.getSigners();
  const seedFile = path.join(os.tmpdir(), `cmbg_local_${Date.now()}.seed.json`);
  execFileSync(process.execPath, [path.join(__dirname, "hashchain.js"), "new", seedFile, "100"]);
  const { seed, length } = JSON.parse(fs.readFileSync(seedFile, "utf8"));
  const links = chain(seed, length);
  const tree = StandardMerkleTree.of([[user.address, 1, 1, 0]], ["address", "uint8", "uint8", "uint8"]);

  const card = await (await ethers.getContractFactory("CMBGPassCard")).deploy(deployer.address, tree.root, "ipfs://T/");
  const src = await (await ethers.getContractFactory("HashChainRandomSource"))
    .deploy(deployer.address, await card.getAddress(), deployer.address, links[length]);
  await card.setRandomSource(await src.getAddress());

  await card.connect(user).claim(1, 1, 0, tree.getProof(0));
  await card.connect(user).open(1);
  await card.connect(user).open(2);
  let i = links.indexOf(await src.anchor());
  await src.revealBatch([links[--i], links[--i]]);
  const [ids, tiers, chars, states] = await card.cardsOf(user.address);
  console.log("ids", ids.map(Number), "tiers", tiers.map(Number), "chars", chars.map(Number), "states", states.map(Number));
  console.log("uri 1", await card.tokenURI(1));
  fs.unlinkSync(seedFile);
}

main().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
