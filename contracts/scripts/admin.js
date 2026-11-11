// Owner actions with a PLAIN-WALLET owner (no multisig; user decision 2026-11-07). The signer is the owner key:
//   mainnet $env:RH_MAINNET_KEY, testnet $env:RH_TESTNET_KEY. Addresses from deployments/<network>.json.
//
// usage (PowerShell, in G:\PROJECT-GAME-WAR-CAT\Contracts):
//   $env:ACTION = "setBaseURI"; $env:ARG = "ipfs://<CID>/"; npx hardhat run scripts/admin.js --network robinhoodMainnet
//   actions (ARG / ARG2):
//     status                                  read-only: owner, pending owner, root, base URI, frozen, paused, operator
//     acceptOwnership                         both contracts (only when deploy.js offered ownership to another wallet)
//     setBaseURI        ipfs://<CID>/         card
//     setMerkleRoot     0x<root>              card
//     setClaimDeadline  <unix seconds>        card (0 = open-ended)
//     setOperator       0x<wallet>            random source
//     setRandomSource   0x<source>            card
//     pause | unpause                         card
//     setDefaultRoyalty 0x<receiver> / <bps>  card (ARG2 = 500 for 5 %)
//     freezeMetadata                          card - FINAL. Needs $env:CONFIRM = "FREEZE"
// On mainnet every sending action waits 8 s after printing what it will do (Ctrl+C to stop).
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

async function main() {
  const action = process.env.ACTION;
  const arg = process.env.ARG;
  const arg2 = process.env.ARG2;
  if (!action) throw new Error("set $env:ACTION (see the top of scripts/admin.js)");
  const dep = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "deployments", `${network.name}.json`), "utf8"));
  const [me] = await ethers.getSigners();
  const card = await ethers.getContractAt("CMBGPassCard", dep.CMBGPassCard, me);
  const src = await ethers.getContractAt("HashChainRandomSource", dep.HashChainRandomSource, me);
  const { chainId } = await ethers.provider.getNetwork();

  if (action === "status") {
    const [owner, pending, root, frozen, paused, op, srcOwner, deadline] = await Promise.all([
      card.owner(), card.pendingOwner(), card.merkleRoot(), card.metadataFrozen(), card.paused(), src.operator(), src.owner(),
      card.claimDeadline(),
    ]);
    console.log({ network: network.name, card: dep.CMBGPassCard, owner, pendingOwner: pending, sourceOwner: srcOwner, operator: op,
      merkleRoot: root, metadataFrozen: frozen, paused, claimDeadline: Number(deadline), tokenURI1: await card.tokenURI(1).catch(() => "(no token 1)") });
    return;
  }
  if (!me) throw new Error("no signer: set the owner key ($env:RH_MAINNET_KEY / $env:RH_TESTNET_KEY)");

  let target, call, label;
  switch (action) {
    case "acceptOwnership": {
      for (const [name, c] of [["CMBGPassCard", card], ["HashChainRandomSource", src]]) {
        if ((await c.pendingOwner()).toLowerCase() !== me.address.toLowerCase()) { console.log(name + ": no ownership offer for " + me.address); continue; }
        await (await c.acceptOwnership()).wait();
        console.log(name + ": owner is now " + me.address);
      }
      return;
    }
    case "setBaseURI":
      if (!/^ipfs:\/\/.+\/$/.test(arg || "")) throw new Error("ARG must be ipfs://<CID>/ with the trailing slash");
      [target, call, label] = [card, () => card.setBaseURI(arg), `setBaseURI("${arg}")`]; break;
    case "setMerkleRoot":
      if (!/^0x[0-9a-fA-F]{64}$/.test(arg || "")) throw new Error("ARG must be 0x + 64 hex");
      [target, call, label] = [card, () => card.setMerkleRoot(arg), `setMerkleRoot(${arg})`]; break;
    case "setClaimDeadline":
      [target, call, label] = [card, () => card.setClaimDeadline(BigInt(arg)), `setClaimDeadline(${arg} = ${Number(arg) ? new Date(Number(arg) * 1000).toISOString() : "open-ended"})`]; break;
    case "setOperator":
      [target, call, label] = [src, () => src.setOperator(ethers.getAddress(arg)), `setOperator(${ethers.getAddress(arg)})`]; break;
    case "setRandomSource":
      [target, call, label] = [card, () => card.setRandomSource(ethers.getAddress(arg)), `setRandomSource(${ethers.getAddress(arg)})`]; break;
    case "pause":
    case "unpause":
      [target, call, label] = [card, () => card[action](), action + "()"]; break;
    case "setDefaultRoyalty":
      [target, call, label] = [card, () => card.setDefaultRoyalty(ethers.getAddress(arg), BigInt(arg2)), `setDefaultRoyalty(${arg}, ${arg2} bps)`]; break;
    case "freezeMetadata":
      if (process.env.CONFIRM !== "FREEZE") throw new Error('freezeMetadata is FINAL: set $env:CONFIRM = "FREEZE"');
      [target, call, label] = [card, () => card.freezeMetadata(), "freezeMetadata()  <-- FINAL"]; break;
    default:
      throw new Error("unknown ACTION " + action);
  }
  const owner = await target.owner();
  if (owner.toLowerCase() !== me.address.toLowerCase()) throw new Error(`signer ${me.address} is not the owner (${owner})`);
  console.log(`${network.name}: ${label}  on ${await target.getAddress()}  from ${me.address}`);
  if (chainId === 4663n) { console.log("sending in 8 s - Ctrl+C to stop"); await new Promise((r) => setTimeout(r, 8000)); }
  const tx = await call();
  const rc = await tx.wait();
  console.log("done, tx " + rc.hash);
}

main().catch((e) => { console.error(e.shortMessage || e.message || e); process.exitCode = 1; });
