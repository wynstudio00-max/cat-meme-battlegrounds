// Builds the claim Merkle tree from a balance snapshot.
// usage: node scripts/snapshot.js <balances.csv> [exclude.txt] [outDir] [decimals=18]
//   balances.csv: address,balance   (balance in raw units, i.e. with decimals, as explorers export it)
//   exclude.txt:  one address per line (LP pools, token contract, team / treasury, burn); # comments allowed
// writes <outDir>/root.txt, tree.json (full dump), claims.json (address -> amounts + proof, for the website), summary.json
const fs = require("fs");
const path = require("path");
const { StandardMerkleTree } = require("@openzeppelin/merkle-tree");
const { getAddress } = require("ethers");
const { allocate } = require("./allocation");

const [csvPath, excludePath, outDirArg, decArg] = process.argv.slice(2);
if (!csvPath) {
  console.error("usage: node scripts/snapshot.js <balances.csv> [exclude.txt] [outDir] [decimals]");
  process.exit(1);
}
const outDir = outDirArg || path.join(__dirname, "..", "snapshot", "out");
const decimals = BigInt(decArg || 18);
const unit = 10n ** decimals;

const exclude = new Set();
if (excludePath && fs.existsSync(excludePath)) {
  for (const line of readText(excludePath).split(/\r?\n/)) {
    const a = line.replace(/#.*/, "").trim();
    if (a) exclude.add(getAddress(a));
  }
}

// CSVs saved by Windows PowerShell / Excel can be UTF-16 (with or without BOM) or UTF-8 with BOM.
function readText(file) {
  const buf = fs.readFileSync(file);
  if (buf[0] === 0xff && buf[1] === 0xfe) return buf.subarray(2).toString("utf16le");
  if (buf.length > 1 && buf[1] === 0 && buf[0] !== 0) return buf.toString("utf16le");
  return buf.toString("utf8").replace(/^﻿/, "");
}

const balances = new Map();
for (const line of readText(csvPath).split(/\r?\n/)) {
  const parts = line.split(",").map((s) => s.trim().replace(/^"|"$/g, ""));
  if (parts.length < 2 || !/^0x[0-9a-fA-F]{40}$/.test(parts[0])) continue; // header / blank
  const addr = getAddress(parts[0]);
  const raw = BigInt(parts[1].split(".")[0]);
  balances.set(addr, (balances.get(addr) || 0n) + raw);
}

const values = [];
const summary = { wallets: 0, excluded: 0, commons: 0, rares: 0, legendaries: 0 };
for (const [addr, raw] of balances) {
  if (exclude.has(addr)) { summary.excluded++; continue; }
  const a = allocate(raw / unit);
  if (a.commons + a.rares + a.legendaries === 0) continue;
  values.push([addr, a.commons, a.rares, a.legendaries]);
  summary.wallets++;
  summary.commons += a.commons;
  summary.rares += a.rares;
  summary.legendaries += a.legendaries;
}
if (values.length === 0) {
  console.error("no wallet qualifies");
  process.exit(1);
}

const tree = StandardMerkleTree.of(values, ["address", "uint8", "uint8", "uint8"]);
const claims = {};
for (const [i, v] of tree.entries()) {
  claims[v[0]] = { commons: v[1], rares: v[2], legendaries: v[3], proof: tree.getProof(i) };
}

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, "root.txt"), tree.root + "\n");
fs.writeFileSync(path.join(outDir, "tree.json"), JSON.stringify(tree.dump(), null, 1));
fs.writeFileSync(path.join(outDir, "claims.json"), JSON.stringify(claims, null, 1));
fs.writeFileSync(path.join(outDir, "summary.json"), JSON.stringify(summary, null, 1));
console.log("root", tree.root);
console.log(summary);
