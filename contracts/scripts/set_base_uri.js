// Owner: points tokenURI at the uploaded metadata folder.
// usage: $env:BASE_URI = "ipfs://<metadataCID>/"; npx hardhat run scripts/set_base_uri.js --network robinhoodTestnet
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const file = path.join(__dirname, "..", "deployments", `${network.name}.json`);
  const dep = JSON.parse(fs.readFileSync(file, "utf8"));
  const [me] = await ethers.getSigners();
  if (!me) throw new Error("Set $env:RH_TESTNET_KEY first.");
  const uri = process.env.BASE_URI || "";
  if (!/^(ipfs|https):\/\/.+\/$/.test(uri)) throw new Error('BASE_URI must look like "ipfs://<CID>/" (with the trailing slash)');
  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard, me);
  if (await card.metadataFrozen()) throw new Error("metadata is frozen");
  const tx = await card.setBaseURI(uri);
  console.log("setBaseURI tx", tx.hash);
  await tx.wait();
  dep.baseURI = uri;
  fs.writeFileSync(file, JSON.stringify(dep, null, 2));
  const supply = Number(await card.totalSupply());
  for (let id = 1; id <= Math.min(supply, 5); id++) console.log(`token ${id}:`, await card.tokenURI(id));
}

main().catch((e) => {
  console.error(e.shortMessage || e.message || e);
  process.exitCode = 1;
});
