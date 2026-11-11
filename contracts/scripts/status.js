// Read-only status of the deployed contracts (no key needed).
// usage: npx hardhat run scripts/status.js --network robinhoodTestnet   [env WALLET=0x... to list its cards]
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const dep = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "deployments", `${network.name}.json`), "utf8"));
  const p = ethers.provider;
  for (const k of ["CMBGPassCard", "HashChainRandomSource"]) {
    const code = await p.getCode(dep[k]);
    console.log(k, dep[k], "code bytes", (code.length - 2) / 2);
  }
  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard);
  const src = await ethers.getContractAt("HashChainRandomSource", dep.HashChainRandomSource);
  console.log("name", await card.name(), "/", await card.symbol(), " owner", await card.owner());
  console.log("merkleRoot", await card.merkleRoot(), " randomSource", await card.randomSource());
  console.log("paused", await card.paused(), " supply", (await card.totalSupply()).toString());
  console.log("source: consumer", await src.consumer(), " operator", await src.operator(), " pending", (await src.pending()).toString());
  console.log("anchor", await src.anchor());
  const { CHARACTERS } = require("./make_metadata");
  const names = ["-", ...Object.keys(CHARACTERS).sort((a, b) => a - b).map((k) => CHARACTERS[k].name)];
  const tiers = ["-", "Common", "Rare", "Legendary"];
  const states = ["None", "Sealed", "Opening", "Opened"];
  const wallet = process.env.WALLET || dep.deployer;
  console.log("claimed by", wallet, await card.claimed(wallet));
  const [ids, t, c, s] = await card.cardsOf(wallet);
  for (let i = 0; i < ids.length; i++) {
    console.log(`  card #${ids[i]} ${tiers[t[i]]} ${states[s[i]]} -> ${names[c[i]]}  ${await card.tokenURI(ids[i])}`);
  }
}

main().catch((e) => {
  console.error(e.message || e);
  process.exitCode = 1;
});
