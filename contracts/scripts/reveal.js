// Operator: serves every waiting open request (reveals the next hash-chain links in one revealBatch).
// usage: $env:SEED_FILE = "<path outside the repo>"; npx hardhat run scripts/reveal.js --network robinhoodTestnet
// The signer (RH_TESTNET_KEY) must be the operator set at deploy time. Reads addresses from deployments/<network>.json.
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");
const { chain } = require("./hashchain");

async function main() {
  const dep = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "deployments", `${network.name}.json`), "utf8"));
  const seedFile = process.env.SEED_FILE;
  if (!seedFile || !fs.existsSync(seedFile)) throw new Error("Set $env:SEED_FILE to the secret seed file");
  const { seed, length } = JSON.parse(fs.readFileSync(seedFile, "utf8"));
  const links = chain(seed, length);

  const src = await ethers.getContractAt("HashChainRandomSource", dep.HashChainRandomSource);
  const pending = Number(await src.pending());
  console.log("pending requests:", pending);
  if (pending === 0) return;

  let i = links.indexOf(await src.anchor());
  if (i < 0) {
    throw new Error(
      "current anchor is not in this seed's chain: wrong SEED_FILE, or the seed was lost / changed. " +
        "If lost: create a new seed and run scripts/replace_source.js (cards in Opening get re-requested)."
    );
  }
  const batch = [];
  for (let n = 0; n < Math.min(pending, 50); n++) {
    if (i === 0) throw new Error("chain exhausted: create a new seed and setAnchor (queue must be empty)");
    batch.push(links[--i]);
  }
  const tx = await src.revealBatch(batch);
  const rc = await tx.wait();
  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard);
  for (const log of rc.logs) {
    try {
      const ev = card.interface.parseLog(log);
      if (ev && ev.name === "CardOpened") console.log(`card ${ev.args.tokenId} tier ${ev.args.tier} -> character ${ev.args.character}`);
    } catch { /* other contract's log */ }
  }
  console.log(`revealed ${batch.length}, links left ${i}`);
}

main().catch((e) => {
  console.error(e.message || e);
  process.exitCode = 1;
});
