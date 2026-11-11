// Gas units for every pass-card transaction with a realistic 1,000-wallet snapshot (local Hardhat network).
// usage: npx hardhat run scripts/gas_report.js        -> prints JSON (execution gas only; L1 data fee is added separately)
const { ethers, network } = require("hardhat");
const { StandardMerkleTree } = require("@openzeppelin/merkle-tree");
const { chain } = require("./hashchain");

async function main() {
  const [owner] = await ethers.getSigners();
  const gas = async (txp) => Number((await (await txp).wait()).gasUsed);
  const out = {};

  const Card = await ethers.getContractFactory("CMBGPassCard");
  const card = await Card.deploy(owner.address, ethers.ZeroHash, "ipfs://bafybeidyggvcs6tjernwx72ydqjtadvrnpv5e2dhs5qx4uzfjkn2f4buc4/");
  out.deployCard = Number((await card.deploymentTransaction().wait()).gasUsed);
  out.cardInitcodeBytes = (Card.bytecode.length - 2) / 2;
  const hc = chain("0x" + "11".repeat(32), 3000);
  const Src = await ethers.getContractFactory("HashChainRandomSource");
  const src = await Src.deploy(owner.address, await card.getAddress(), owner.address, hc[3000]);
  out.deploySource = Number((await src.deploymentTransaction().wait()).gasUsed);
  out.sourceInitcodeBytes = (Src.bytecode.length - 2) / 2;
  out.setRandomSource = await gas(card.setRandomSource(await src.getAddress()));

  // 1,000 wallets: 70% 1 card, 30% 2 cards (mix of tiers); the first 20 are local signers we can impersonate
  const wallets = [];
  for (let i = 0; i < 1000; i++) wallets.push(ethers.Wallet.createRandom().address);
  const alloc = (i) => (i % 10 < 7 ? [1, 0, 0] : i % 2 ? [1, 1, 0] : [0, 1, 1]);
  const values = wallets.map((a, i) => [a, ...alloc(i)]);
  const tree = StandardMerkleTree.of(values, ["address", "uint8", "uint8", "uint8"]);
  out.proofDepth = tree.getProof(0).length;
  out.setMerkleRoot = await gas(card.setMerkleRoot(tree.root));

  async function signer(addr) {
    await network.provider.send("hardhat_impersonateAccount", [addr]);
    await network.provider.send("hardhat_setBalance", [addr, "0x56BC75E2D63100000"]);
    return ethers.getSigner(addr);
  }
  const one = [], two = [];
  for (let i = 0; i < 40; i++) {
    const s = await signer(wallets[i]);
    const [c, r, l] = alloc(i);
    const g = await gas(card.connect(s).claim(c, r, l, tree.getProof(i)));
    (c + r + l === 1 ? one : two).push(g);
  }
  const avg = (a) => Math.round(a.reduce((x, y) => x + y, 0) / a.length);
  out.claim1Card = avg(one);
  out.claim2Cards = avg(two);
  out.claimCalldataBytes = (card.interface.encodeFunctionData("claim", [1, 0, 0, tree.getProof(0)]).length - 2) / 2;

  // open: every card of the first 40 wallets
  const opens = [];
  let reqs = 0;
  for (let i = 0; i < 40; i++) {
    const s = await signer(wallets[i]);
    const [ids] = await card.cardsOf(wallets[i]);
    for (const id of ids) { opens.push(await gas(card.connect(s).open(id))); reqs++; }
  }
  out.open = avg(opens);
  out.openCalldataBytes = (card.interface.encodeFunctionData("open", [1]).length - 2) / 2;

  // reveal: 1 alone, then batches
  let pos = 3000;
  out.reveal1 = await gas(src.revealBatch([hc[--pos]]));
  const b10 = []; for (let i = 0; i < 10; i++) b10.push(hc[--pos]);
  out.revealBatch10 = await gas(src.revealBatch(b10));
  const rest = []; while (rest.length < reqs - 11) rest.push(hc[--pos]);
  out.revealBatchN = { n: rest.length, gas: await gas(src.revealBatch(rest)) };
  out.revealPerCardInBatch = Math.round(out.revealBatchN.gas / rest.length);
  out.revealCalldataBytesPerCard = 32;

  out.setBaseURI = await gas(card.setBaseURI("ipfs://bafybeidyggvcs6tjernwx72ydqjtadvrnpv5e2dhs5qx4uzfjkn2f4buc4/"));
  out.transfer = await gas(card.connect(await signer(wallets[0])).transferFrom(wallets[0], wallets[1], 1));
  console.log(JSON.stringify(out, null, 1));
}

main().catch((e) => { console.error(e); process.exitCode = 1; });
