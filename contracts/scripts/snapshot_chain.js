// Builds the CMBG balance snapshot straight from the chain: every Transfer event of the token from block 0 up to the snapshot
// block, summed per wallet. (The public Robinhood RPC keeps no old state, so balanceOf at an old block is not possible; the
// transfer history gives the exact balances at any block.)
//
// usage:  node scripts/snapshot_chain.js <time | block | latest> [outDir]
//   time  = ISO UTC, e.g. 2026-11-08T13:00:00Z (= 8 Nov 2026 20:00 WIB): uses the LAST block with timestamp <= that time
//   block = a block number;  latest = now (a dry run)
// writes <outDir>/balances.csv (address,balance raw - input of scripts/snapshot.js), holders.json (report: block, time,
// every holder with balance + contract flag + tier preview), and prints the summary.
// When the snapshot block is the latest block it also checks every computed balance against balanceOf (current state).
const fs = require("fs");
const path = require("path");
const { JsonRpcProvider, Interface, getAddress, formatUnits } = require("ethers");
const { allocate } = require("./allocation");

const RPC = process.env.RPC || "https://rpc.mainnet.chain.robinhood.com";
const TOKEN = getAddress(process.env.TOKEN || "0x5031600A85A0176d440f2662bCa3190E025f1A2f");   // CMBG on Robinhood Chain
const TRANSFER = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
const MAX_SPAN = 9_999_999;   // the RPC allows 10,000,000 blocks per getLogs
const erc20 = new Interface(["function balanceOf(address) view returns (uint256)", "function totalSupply() view returns (uint256)", "function decimals() view returns (uint8)"]);

const provider = new JsonRpcProvider(RPC, undefined, { staticNetwork: true, batchMaxCount: 1 });

async function blockAtOrBefore(ts) {
  const latest = await provider.getBlock("latest");
  if (ts >= latest.timestamp) throw new Error(`that time is in the future (chain time now ${new Date(latest.timestamp * 1000).toISOString()})`);
  let lo = 0, hi = latest.number;            // invariant: block lo has timestamp <= ts
  while (lo < hi) {
    const mid = Math.floor((lo + hi + 1) / 2);
    const b = await provider.getBlock(mid);
    if (b.timestamp <= ts) lo = mid; else hi = mid - 1;
  }
  return lo;
}

async function logs(from, to) {
  try {
    return await provider.getLogs({ address: TOKEN, topics: [TRANSFER], fromBlock: from, toBlock: to });
  } catch (e) {
    if (to - from < 1000) throw e;
    const mid = Math.floor((from + to) / 2);   // too many results / too wide: split
    return [...(await logs(from, mid)), ...(await logs(mid + 1, to))];
  }
}

async function main() {
  const arg = process.argv[2];
  const outDir = process.argv[3] || path.join(__dirname, "..", "snapshot", "mainnet");
  if (!arg) throw new Error("usage: node scripts/snapshot_chain.js <2026-11-08T13:00:00Z | block | latest> [outDir]");
  const latest = await provider.getBlockNumber();
  let block;
  if (arg === "latest") block = latest;
  else if (/^\d+$/.test(arg)) block = Number(arg);
  else {
    const ts = Math.floor(Date.parse(arg) / 1000);
    if (!Number.isFinite(ts)) throw new Error("bad time " + arg + " (use ISO UTC, e.g. 2026-11-08T13:00:00Z)");
    block = await blockAtOrBefore(ts);
  }
  const b = await provider.getBlock(block);
  const next = block < latest ? await provider.getBlock(block + 1) : null;
  console.log(`snapshot block ${block}  time ${new Date(b.timestamp * 1000).toISOString()}` + (next ? `  (next block ${new Date(next.timestamp * 1000).toISOString()})` : "  (latest)"));

  const dec = Number(erc20.decodeFunctionResult("decimals", await provider.call({ to: TOKEN, data: erc20.encodeFunctionData("decimals") }))[0]);
  const bal = new Map();
  let n = 0, minted = 0n, burned = 0n;
  for (let from = 0; from <= block; from += MAX_SPAN + 1) {
    const to = Math.min(block, from + MAX_SPAN);
    const part = await logs(from, to);
    for (const l of part) {
      const src = getAddress("0x" + l.topics[1].slice(26)), dst = getAddress("0x" + l.topics[2].slice(26));
      const v = BigInt(l.data);
      if (/^0x0{40}$/i.test(src)) minted += v; else bal.set(src, (bal.get(src) || 0n) - v);
      if (/^0x0{40}$/i.test(dst)) burned += v; else bal.set(dst, (bal.get(dst) || 0n) + v);
      n++;
    }
    process.stdout.write(`\r  transfers read: ${n} (up to block ${to})        `);
  }
  console.log();
  const negative = [...bal].filter(([, v]) => v < 0n);
  if (negative.length) throw new Error("negative balance for " + negative[0][0] + " - log history incomplete");
  const holders = [...bal].filter(([, v]) => v > 0n).sort((x, y) => (y[1] > x[1] ? 1 : y[1] < x[1] ? -1 : 0));
  const sum = holders.reduce((s, [, v]) => s + v, 0n);
  if (sum !== minted - burned) throw new Error("sum of balances != minted - burned");

  // checks against the live contract
  const supplyNow = BigInt(await provider.call({ to: TOKEN, data: erc20.encodeFunctionData("totalSupply") }));
  console.log(`holders ${holders.length}, supply from transfers ${formatUnits(minted - burned, dec)} (totalSupply now ${formatUnits(supplyNow, dec)})`);
  if (block === latest) {
    let bad = 0;
    for (const [a, v] of holders) {
      const live = BigInt(await provider.call({ to: TOKEN, data: erc20.encodeFunctionData("balanceOf", [a]) }));
      if (live !== v) { bad++; console.log("  MISMATCH", a, formatUnits(v, dec), "live", formatUnits(live, dec)); }
    }
    console.log(bad ? `balance check: ${bad} mismatches (new transfers while reading?)` : `balance check: all ${holders.length} balances equal balanceOf now`);
  }

  // report: contracts are probably pools / lockers / the launchpad -> candidates for the exclude list
  const unit = 10n ** BigInt(dec);
  const rows = [];
  for (const [a, v] of holders) {
    const code = await provider.getCode(a);
    const whole = v / unit;
    const al = allocate(whole);
    rows.push({ address: a, balance: formatUnits(v, dec), contract: code !== "0x", cards: al });
  }
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, "balances.csv"), "address,balance\n" + holders.map(([a, v]) => `${a},${v}`).join("\n") + "\n");
  fs.writeFileSync(path.join(outDir, "holders.json"), JSON.stringify({ token: TOKEN, block, time: new Date(b.timestamp * 1000).toISOString(), rpc: RPC, holders: rows }, null, 1));

  const elig = rows.filter((r) => r.cards.commons + r.cards.rares + r.cards.legendaries > 0);
  const t = elig.reduce((s, r) => ({ c: s.c + r.cards.commons, r: s.r + r.cards.rares, l: s.l + r.cards.legendaries }), { c: 0, r: 0, l: 0 });
  console.log(`eligible wallets (before the exclude list): ${elig.length}  cards: Common ${t.c}, Rare ${t.r}, Legendary ${t.l}`);
  console.log("top holders (contract = probably a pool / locker / launchpad -> exclude?):");
  for (const r of rows.slice(0, 15))
    console.log(`  ${r.address}  ${Number(r.balance).toLocaleString("en-US", { maximumFractionDigits: 0 }).padStart(14)} CMBG  ${r.contract ? "CONTRACT" : "wallet  "}  ${r.cards.legendaries}L ${r.cards.rares}R ${r.cards.commons}C`);
  console.log("written", path.join(outDir, "balances.csv"), "+ holders.json");
}

main().catch((e) => { console.error("\n" + (e.shortMessage || e.message || e)); process.exitCode = 1; });
