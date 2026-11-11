// Replaces the random source (new hash chain) and re-requests the signer's cards stuck in Opening.
// Use when the old seed is lost / damaged, or when moving to a VRF adapter later.
// usage: $env:ANCHOR = "0x..." (from hashchain.js new <newSeedFile>)
//        npx hardhat run scripts/replace_source.js --network robinhoodTestnet     (signer = card owner + contract owner)
// Cards held by OTHER wallets must call reRequest(tokenId) themselves (website button later).
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const file = path.join(__dirname, "..", "deployments", `${network.name}.json`);
  const dep = JSON.parse(fs.readFileSync(file, "utf8"));
  const [me] = await ethers.getSigners();
  if (!me) throw new Error("Set $env:RH_TESTNET_KEY first.");
  const anchor = process.env.ANCHOR;
  if (!/^0x[0-9a-fA-F]{64}$/.test(anchor || "")) throw new Error("Set $env:ANCHOR (0x + 64 hex) from hashchain.js new");

  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard, me);
  if ((await card.owner()) !== me.address) throw new Error("Signer is not the card contract owner");

  const Src = await ethers.getContractFactory("HashChainRandomSource", me);
  const src = await Src.deploy(me.address, dep.CMBGPassCard, dep.operator, anchor);
  await src.waitForDeployment();
  const srcAddr = await src.getAddress();
  console.log("new HashChainRandomSource", srcAddr);
  await (await card.setRandomSource(srcAddr)).wait();
  console.log("randomSource set");

  const [ids, , , states] = await card.cardsOf(me.address);
  for (let i = 0; i < ids.length; i++) {
    if (Number(states[i]) !== 2) continue; // Opening
    const tx = await card.reRequest(ids[i]);
    console.log(`reRequest card #${ids[i]} tx`, tx.hash);
    await tx.wait();
  }

  dep.retiredSources = [...(dep.retiredSources || []), { address: dep.HashChainRandomSource, anchor: dep.anchor }];
  dep.HashChainRandomSource = srcAddr;
  dep.anchor = anchor;
  dep.verify = [
    dep.verify[0],
    `npx hardhat verify --network ${network.name} ${srcAddr} ${me.address} ${dep.CMBGPassCard} ${dep.operator} ${anchor}`,
  ];
  fs.writeFileSync(file, JSON.stringify(dep, null, 2));
  console.log("deployments updated - now run scripts/reveal.js with the NEW seed file");
}

main().catch((e) => {
  console.error(e.shortMessage || e.message || e);
  process.exitCode = 1;
});
