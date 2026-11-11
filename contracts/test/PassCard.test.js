const { expect } = require("chai");
const { ethers } = require("hardhat");
const { time } = require("@nomicfoundation/hardhat-network-helpers");
const { StandardMerkleTree } = require("@openzeppelin/merkle-tree");
const { chain } = require("../scripts/hashchain");
const { allocate } = require("../scripts/allocation");

const BASE = "ipfs://CID/";
const SEED = "0x" + "11".repeat(32);
const LEN = 50;

async function deploy() {
  const [owner, operator, alice, bob, carol, eve] = await ethers.getSigners();
  const values = [
    [alice.address, 1, 0, 1], // Legendary + Common
    [bob.address, 0, 2, 0], // 2 Rare
    [carol.address, 1, 0, 0], // Common
  ];
  const tree = StandardMerkleTree.of(values, ["address", "uint8", "uint8", "uint8"]);
  const proofOf = (addr) => {
    for (const [i, v] of tree.entries()) if (v[0] === addr) return tree.getProof(i);
  };
  const Card = await ethers.getContractFactory("CMBGPassCard");
  const card = await Card.deploy(owner.address, tree.root, BASE);
  const hc = chain(SEED, LEN);
  const Src = await ethers.getContractFactory("HashChainRandomSource");
  const src = await Src.deploy(owner.address, await card.getAddress(), operator.address, hc[LEN]);
  await card.setRandomSource(await src.getAddress());
  let pos = LEN; // index of the current anchor in hc
  const nextPreimage = () => hc[--pos];
  return { owner, operator, alice, bob, carol, eve, card, src, tree, proofOf, nextPreimage, hc };
}

describe("allocation rule", () => {
  const cases = [
    // thresholds 2026-11-07: Common 150k / Rare 500k / Legendary 1M, highest first, cumulative, max 2
    [149_999n, [0, 0, 0]], [150_000n, [1, 0, 0]], [299_999n, [1, 0, 0]], [300_000n, [2, 0, 0]],
    [499_999n, [2, 0, 0]], [500_000n, [0, 1, 0]], [649_999n, [0, 1, 0]], [650_000n, [1, 1, 0]],
    [999_999n, [1, 1, 0]], [1_000_000n, [0, 0, 1]], [1_150_000n, [1, 0, 1]], [1_500_000n, [0, 1, 1]],
    [1_999_999n, [0, 1, 1]], [2_000_000n, [0, 0, 2]], [9_000_000n, [0, 0, 2]],
  ];
  for (const [bal, [c, r, l]] of cases) {
    it(`${bal} CMBG -> C${c} R${r} L${l}`, () => {
      expect(allocate(bal)).to.deep.equal({ commons: c, rares: r, legendaries: l });
    });
  }
});

describe("CMBGPassCard", () => {
  it("claims the snapshot allocation once", async () => {
    const { card, alice, proofOf } = await deploy();
    await expect(card.connect(alice).claim(1, 0, 1, proofOf(alice.address)))
      .to.emit(card, "Claimed").withArgs(alice.address, 1, 0, 1);
    expect(await card.balanceOf(alice.address)).to.equal(2);
    const [ids, tiers, chars, states] = await card.cardsOf(alice.address);
    expect(ids.map(Number)).to.deep.equal([1, 2]);
    expect(tiers.map(Number)).to.deep.equal([3, 1]); // legendary minted first
    expect(chars.map(Number)).to.deep.equal([0, 0]);
    expect(states.map(Number)).to.deep.equal([1, 1]); // Sealed
    await expect(card.connect(alice).claim(1, 0, 1, proofOf(alice.address)))
      .to.be.revertedWithCustomError(card, "AlreadyClaimed");
  });

  it("rejects wrong amounts, someone else's proof and > 2 cards", async () => {
    const { card, alice, bob, eve, proofOf } = await deploy();
    await expect(card.connect(alice).claim(0, 0, 2, proofOf(alice.address)))
      .to.be.revertedWithCustomError(card, "BadProof");
    await expect(card.connect(eve).claim(1, 0, 1, proofOf(alice.address)))
      .to.be.revertedWithCustomError(card, "BadProof");
    await expect(card.connect(bob).claim(1, 2, 0, proofOf(bob.address)))
      .to.be.revertedWithCustomError(card, "BadAmount");
    await expect(card.connect(bob).claim(0, 0, 0, []))
      .to.be.revertedWithCustomError(card, "BadAmount");
  });

  it("respects the claim deadline and pause", async () => {
    const { card, owner, alice, carol, proofOf } = await deploy();
    await card.connect(owner).pause();
    await expect(card.connect(carol).claim(1, 0, 0, proofOf(carol.address)))
      .to.be.revertedWithCustomError(card, "EnforcedPause");
    await card.connect(owner).unpause();
    const now = await time.latest();
    await card.connect(owner).setClaimDeadline(now + 100);
    await card.connect(carol).claim(1, 0, 0, proofOf(carol.address));
    await time.increase(200);
    await expect(card.connect(alice).claim(1, 0, 1, proofOf(alice.address)))
      .to.be.revertedWithCustomError(card, "ClaimClosed");
  });

  it("opens a card once with a character of its own tier", async () => {
    const { card, src, operator, alice, eve, proofOf, nextPreimage } = await deploy();
    await card.connect(alice).claim(1, 0, 1, proofOf(alice.address));
    expect(await card.tokenURI(1)).to.equal(BASE + "sealed/3.json");
    await expect(card.connect(eve).open(1)).to.be.revertedWithCustomError(card, "NotCardOwner");
    await expect(card.connect(alice).open(1)).to.emit(src, "Requested").withArgs(0, 1);
    await expect(card.connect(alice).open(1)).to.be.revertedWithCustomError(card, "WrongState");
    expect(await card.tokenURI(1)).to.equal(BASE + "sealed/3.json"); // still sealed while opening

    const pre = nextPreimage();
    await expect(src.connect(eve).reveal(pre)).to.be.revertedWithCustomError(src, "NotOperator");
    await expect(src.connect(operator).reveal("0x" + "22".repeat(32))).to.be.revertedWithCustomError(src, "BadPreimage");
    await src.connect(operator).reveal(pre);
    const [tier, ch, state] = await card.cardOf(1);
    expect(Number(tier)).to.equal(3);
    expect([7, 8]).to.include(Number(ch));
    expect(Number(state)).to.equal(3);
    expect(await card.characterOf(1)).to.equal(ch);
  });

  it("full open flow, ordering and metadata", async () => {
    const { card, src, operator, alice, bob, proofOf, hc } = await deploy();
    await card.connect(alice).claim(1, 0, 1, proofOf(alice.address)); // ids 1 (L), 2 (C)
    await card.connect(bob).claim(0, 2, 0, proofOf(bob.address)); // ids 3, 4 (R)
    await card.connect(alice).open(2);
    await card.connect(bob).open(4);
    await card.connect(alice).open(1);
    expect(await src.pending()).to.equal(3);

    await expect(card.connect(alice).fulfillRandomness(2, 0)).to.be.revertedWithCustomError(card, "NotRandomSource");

    let pos = LEN;
    await expect(src.connect(operator).reveal(hc[--pos])).to.emit(card, "CardOpened");
    await src.connect(operator).revealBatch([hc[--pos], hc[--pos]]);
    await expect(src.connect(operator).reveal(hc[--pos])).to.be.revertedWithCustomError(src, "NothingPending");

    const pools = { 1: [1, 2, 4], 2: [3, 5, 6], 3: [7, 8] };   // 2026-11-07: baby (4) Common, ninja (3) Rare
    for (const id of [1, 2, 4]) {
      const [tier, ch, state] = await card.cardOf(id);
      expect(Number(state)).to.equal(3); // Opened
      expect(pools[Number(tier)]).to.include(Number(ch));
      expect(await card.tokenURI(id)).to.equal(`${BASE}character/${ch}.json`);
    }
    // the character travels with the card
    const [, chBefore] = await card.cardOf(1);
    await card.connect(alice).transferFrom(alice.address, bob.address, 1);
    const [, chAfter] = await card.cardOf(1);
    expect(chAfter).to.equal(chBefore);
    await expect(card.connect(bob).open(1)).to.be.revertedWithCustomError(card, "WrongState");
  });

  it("results are deterministic from the published chain (operator cannot choose)", async () => {
    const a = await deploy();
    const b = await deploy();
    for (const d of [a, b]) {
      await d.card.connect(d.alice).claim(1, 0, 1, d.proofOf(d.alice.address));
      await d.card.connect(d.alice).open(1);
      await d.card.connect(d.alice).open(2);
      await d.src.connect(d.operator).revealBatch([d.hc[LEN - 1], d.hc[LEN - 2]]);
    }
    expect((await a.card.cardOf(1))[1]).to.equal((await b.card.cardOf(1))[1]);
    expect((await a.card.cardOf(2))[1]).to.equal((await b.card.cardOf(2))[1]);
  });

  it("can switch random source; waiting cards re-request", async () => {
    const { card, src, owner, operator, alice, eve, proofOf, hc } = await deploy();
    await card.connect(alice).claim(1, 0, 1, proofOf(alice.address));
    await card.connect(alice).open(1);
    await expect(card.connect(alice).reRequest(1)).to.be.revertedWithCustomError(card, "WrongState");

    const Src = await ethers.getContractFactory("HashChainRandomSource");
    const hc2 = chain("0x" + "33".repeat(32), 10);
    const src2 = await Src.deploy(owner.address, await card.getAddress(), operator.address, hc2[10]);
    await card.connect(owner).setRandomSource(await src2.getAddress());

    // the old source can no longer deliver
    await expect(src.connect(operator).reveal(hc[LEN - 1])).to.be.revertedWithCustomError(card, "NotRandomSource");
    await expect(card.connect(eve).reRequest(1)).to.be.revertedWithCustomError(card, "NotCardOwner");
    await card.connect(alice).reRequest(1);
    await src2.connect(operator).reveal(hc2[9]);
    expect(Number((await card.cardOf(1))[2])).to.equal(3);
  });

  it("anchor can only be replaced with an empty queue", async () => {
    const { card, src, owner, alice, proofOf } = await deploy();
    await card.connect(alice).claim(1, 0, 1, proofOf(alice.address));
    await card.connect(alice).open(1);
    await expect(src.connect(owner).setAnchor("0x" + "44".repeat(32))).to.be.revertedWithCustomError(src, "QueueNotEmpty");
    await expect(src.connect(alice).setOperator(alice.address)).to.be.revertedWithCustomError(src, "OwnableUnauthorizedAccount");
  });

  it("admin: only owner, metadata freeze, royalty off by default, interfaces", async () => {
    const { card, owner, eve } = await deploy();
    await expect(card.connect(eve).setMerkleRoot(ethers.ZeroHash)).to.be.revertedWithCustomError(card, "OwnableUnauthorizedAccount");
    await expect(card.connect(eve).setRandomSource(eve.address)).to.be.revertedWithCustomError(card, "OwnableUnauthorizedAccount");
    const [recv, amount] = await card.royaltyInfo(1, 10000);
    expect(recv).to.equal(ethers.ZeroAddress);
    expect(amount).to.equal(0);
    await card.connect(owner).setBaseURI("ipfs://NEW/");
    await card.connect(owner).freezeMetadata();
    await expect(card.connect(owner).setBaseURI("x")).to.be.revertedWithCustomError(card, "Frozen");
    expect(await card.supportsInterface("0x80ac58cd")).to.equal(true); // ERC-721
    expect(await card.supportsInterface("0x780e9d63")).to.equal(true); // Enumerable
    expect(await card.supportsInterface("0x2a55205a")).to.equal(true); // ERC-2981
    expect(await card.supportsInterface("0x49064906")).to.equal(true); // ERC-4906
    expect((await card.poolOf(3)).map(Number)).to.deep.equal([7, 8]);
  });

  it("metadata character table matches the contract pools", async () => {
    const { card } = await deploy();
    const { CHARACTERS } = require("../scripts/make_metadata");
    for (const tier of [1, 2, 3]) {
      const fromMeta = Object.entries(CHARACTERS).filter(([, c]) => c.tier === tier).map(([id]) => Number(id));
      expect((await card.poolOf(tier)).map(Number)).to.deep.equal(fromMeta);
    }
  });

  it("draw is spread over the pool (statistical sanity)", async () => {
    // only a few cards per deploy, so check the pick formula (word % pool size) on many hash words
    const counts = [0, 0, 0];
    for (let i = 0; i < 3000; i++) {
      const w = BigInt(ethers.keccak256(ethers.toBeHex(i, 32)));
      counts[Number(w % 3n)]++;
    }
    for (const c of counts) expect(c).to.be.within(900, 1100);
  });
});
