// Test helper: the signer claims its snapshot cards (proof from snapshot/out/claims.json) and opens every sealed card.
// usage: npx hardhat run scripts/claim_open.js --network robinhoodTestnet     (signer = RH_TESTNET_KEY)
// then the operator runs scripts/reveal.js, and scripts/status.js shows the characters.
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const dep = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "deployments", `${network.name}.json`), "utf8"));
  const [me] = await ethers.getSigners();
  if (!me) throw new Error("Set $env:RH_TESTNET_KEY first.");
  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard, me);

  if (!(await card.claimed(me.address))) {
    const claims = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "snapshot", "out", "claims.json"), "utf8"));
    const c = claims[ethers.getAddress(me.address)];
    if (!c) throw new Error(`${me.address} is not in snapshot/out/claims.json`);
    const tx = await card.claim(c.commons, c.rares, c.legendaries, c.proof);
    console.log("claim tx", tx.hash);
    await tx.wait();
  } else {
    console.log("already claimed");
  }

  const [ids, , , states] = await card.cardsOf(me.address);
  for (let i = 0; i < ids.length; i++) {
    if (Number(states[i]) !== 1) continue; // only Sealed
    const tx = await card.open(ids[i]);
    console.log(`open card #${ids[i]} tx`, tx.hash);
    await tx.wait();
  }
  console.log("done - now run scripts/reveal.js (operator)");
}

main().catch((e) => {
  console.error(e.shortMessage || e.message || e);
  process.exitCode = 1;
});
