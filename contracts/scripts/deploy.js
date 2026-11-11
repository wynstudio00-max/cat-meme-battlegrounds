// Deploys CMBGPassCard + HashChainRandomSource and wires them together.
// usage (PowerShell):
//   $env:RH_TESTNET_KEY = "<testnet-only private key>"   # never put it in a file
//   $env:ANCHOR = "0x..."        # from: node scripts/hashchain.js new <seedFile>
//   $env:ROOT = "0x..."          # optional, else snapshot/out/root.txt, else zero (claims closed until setMerkleRoot)
//   $env:BASE_URI = "ipfs://<CID>/"   # optional, can be changed later with setBaseURI
//   $env:OPERATOR = "0x..."      # optional, wallet that calls reveal(); default = deployer
//   $env:OWNER = "0x..."         # optional, e.g. a multisig; gets a 2-step ownership offer (must call acceptOwnership)
//   npx hardhat run scripts/deploy.js --network robinhoodTestnet
//
// MAINNET (--network robinhoodMainnet, key in $env:RH_MAINNET_KEY) runs only when every guard below passes:
//   $env:MAINNET_CONFIRM = "DEPLOY CMBG MAINNET AFTER AUDIT"   exact text, set by hand
//   $env:OWNER     a multisig CONTRACT (code on chain), not the deployer
//                  OR a plain wallet with $env:OWNER_PLAIN_WALLET = "I ACCEPT A SINGLE-KEY OWNER" (OWNER empty = the deployer
//                  stays owner; another plain wallet must then run scripts/admin.js acceptOwnership with its own key)
//   $env:OPERATOR  the reveal Worker's wallet: not the deployer, not the owner
//   $env:ROOT      the real snapshot root, given explicitly (no file fallback, not zero)
//   $env:BASE_URI  ipfs://<metadataCID>/ (not TODO)
//   $env:ANCHOR    from a NEW mainnet seed (never a testnet anchor listed in deployments/robinhoodTestnet.json)
//   deployer       a fresh wallet, never the exposed testnet wallet
// writes deployments/<network>.json
const fs = require("fs");
const path = require("path");
const { ethers, network } = require("hardhat");

const MAINNET_ID = 4663n;
const MAINNET_CONFIRM = "DEPLOY CMBG MAINNET AFTER AUDIT";
// 2026-11-07, user: "siapkan saja tanpa multisig wallet biasa" -> a plain-wallet owner is allowed with this exact text
const PLAIN_CONFIRM = "I ACCEPT A SINGLE-KEY OWNER";
// key shown in a chat screenshot on 2026-11-05: testnet only, forever
const EXPOSED = ["0x93C1cb5bB2Ba98745776292f280468af0105e33D"].map((a) => a.toLowerCase());
const isHex32 = (v) => /^0x[0-9a-fA-F]{64}$/.test(v || "");

function testnetAnchors() {
  const f = path.join(__dirname, "..", "deployments", "robinhoodTestnet.json");
  if (!fs.existsSync(f)) return [];
  // every 32-byte value in the testnet record (anchors of the current + retired sources, roots): never reuse any of them
  return (fs.readFileSync(f, "utf8").match(/0x[0-9a-fA-F]{64}/g) || []).map((a) => a.toLowerCase());
}

async function mainnetGuards(deployer) {
  const fail = (m) => { throw new Error("MAINNET REFUSED: " + m); };
  if (process.env.MAINNET_CONFIRM !== MAINNET_CONFIRM) fail(`set $env:MAINNET_CONFIRM = "${MAINNET_CONFIRM}" (only after the audit)`);
  if (EXPOSED.includes(deployer.address.toLowerCase())) fail("the deployer is the exposed testnet wallet - use a fresh wallet");
  const plain = process.env.OWNER_PLAIN_WALLET === PLAIN_CONFIRM;
  const owner = process.env.OWNER ? ethers.getAddress(process.env.OWNER) : plain ? deployer.address : null;
  if (!owner) fail(`set $env:OWNER to the multisig address, or (single-key owner) $env:OWNER_PLAIN_WALLET = "${PLAIN_CONFIRM}"`);
  if (EXPOSED.includes(owner.toLowerCase())) fail("OWNER is the exposed testnet wallet");
  const isContract = (await ethers.provider.getCode(owner)) !== "0x";
  if (!isContract && !plain) fail(`OWNER is a plain wallet, not a multisig. To accept that risk set $env:OWNER_PLAIN_WALLET = "${PLAIN_CONFIRM}"`);
  if (isContract && owner.toLowerCase() === deployer.address.toLowerCase()) fail("OWNER must not be the deployer");
  if (!isContract) console.log(`WARNING: single-key owner ${owner} (user decision 2026-11-07). Keep its seed phrase offline; use it only for admin.`);
  const operator = process.env.OPERATOR && ethers.getAddress(process.env.OPERATOR);
  if (!operator) fail("set $env:OPERATOR (reveal Worker wallet)");
  if ([deployer.address, owner].map((a) => a.toLowerCase()).includes(operator.toLowerCase())) fail("OPERATOR must be its own wallet");
  if (!isHex32(process.env.ROOT) || /^0x0+$/.test(process.env.ROOT)) fail("set $env:ROOT explicitly to the real snapshot root");
  if (!process.env.BASE_URI || /TODO/.test(process.env.BASE_URI) || !process.env.BASE_URI.endsWith("/")) fail("set $env:BASE_URI = ipfs://<metadataCID>/");
  if (testnetAnchors().includes((process.env.ANCHOR || "").toLowerCase())) fail("ANCHOR is a TESTNET anchor - make a new mainnet seed");
}

async function main() {
  const { chainId } = await ethers.provider.getNetwork();
  const [deployer] = await ethers.getSigners();
  if (!deployer) throw new Error(chainId === MAINNET_ID ? "No deployer key. Set $env:RH_MAINNET_KEY (fresh wallet)." : "No deployer key. Set $env:RH_TESTNET_KEY (testnet-only wallet).");
  if (chainId === MAINNET_ID) await mainnetGuards(deployer);

  const anchor = process.env.ANCHOR;
  if (!isHex32(anchor)) throw new Error("Set $env:ANCHOR (0x + 64 hex) from scripts/hashchain.js new");

  let root = process.env.ROOT;
  const rootFile = path.join(__dirname, "..", "snapshot", "out", "root.txt");
  if (!root && fs.existsSync(rootFile)) root = fs.readFileSync(rootFile, "utf8").trim();
  if (!root) root = ethers.ZeroHash;
  if (!isHex32(root)) throw new Error("ROOT must be 0x + 64 hex");

  const baseURI = process.env.BASE_URI || "ipfs://TODO/";
  const operator = ethers.getAddress(process.env.OPERATOR || deployer.address);
  const finalOwner = process.env.OWNER ? ethers.getAddress(process.env.OWNER) : deployer.address;

  const bal = await ethers.provider.getBalance(deployer.address);
  console.log(`network ${network.name} (${chainId})`);
  console.log(`deployer ${deployer.address}  balance ${ethers.formatEther(bal)} ETH`);
  if (bal === 0n) throw new Error(chainId === MAINNET_ID ? "Deployer has no ETH on Robinhood Chain mainnet." : "Deployer has no ETH for gas (get testnet ETH from the Robinhood Chain faucet).");
  if (chainId === MAINNET_ID) {
    console.log(`MAINNET deploy: owner ${finalOwner}, operator ${operator}, root ${root}, baseURI ${baseURI}`);
    console.log("starting in 10 s - press Ctrl+C to stop");
    await new Promise((r) => setTimeout(r, 10000));
  }

  const Card = await ethers.getContractFactory("CMBGPassCard");
  const card = await Card.deploy(deployer.address, root, baseURI);
  await card.waitForDeployment();
  const cardAddr = await card.getAddress();
  console.log("CMBGPassCard", cardAddr);

  const Src = await ethers.getContractFactory("HashChainRandomSource");
  const src = await Src.deploy(deployer.address, cardAddr, operator, anchor);
  await src.waitForDeployment();
  const srcAddr = await src.getAddress();
  console.log("HashChainRandomSource", srcAddr);

  await (await card.setRandomSource(srcAddr)).wait();
  console.log("randomSource set");

  if (finalOwner !== deployer.address) {
    await (await card.transferOwnership(finalOwner)).wait();
    await (await src.transferOwnership(finalOwner)).wait();
    console.log(`ownership offered to ${finalOwner}: it must call acceptOwnership() on both contracts`);
  }

  // sanity read-back
  if ((await card.randomSource()) !== srcAddr) throw new Error("randomSource mismatch");
  if ((await src.anchor()) !== anchor) throw new Error("anchor mismatch");

  const out = {
    network: network.name,
    chainId: Number(chainId),
    deployedAt: new Date().toISOString(),
    deployer: deployer.address,
    owner: finalOwner,
    operator,
    merkleRoot: root,
    baseURI,
    anchor,
    CMBGPassCard: cardAddr,
    HashChainRandomSource: srcAddr,
    verify: [
      `npx hardhat verify --network ${network.name} ${cardAddr} ${deployer.address} ${root} "${baseURI}"`,
      `npx hardhat verify --network ${network.name} ${srcAddr} ${deployer.address} ${cardAddr} ${operator} ${anchor}`,
    ],
  };
  const dir = path.join(__dirname, "..", "deployments");
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${network.name}.json`);
  fs.writeFileSync(file, JSON.stringify(out, null, 2));
  console.log("saved", file);
  console.log("verify on the explorer with:\n  " + out.verify.join("\n  "));
}

main().catch((e) => {
  console.error(e.message || e);
  process.exitCode = 1;
});
