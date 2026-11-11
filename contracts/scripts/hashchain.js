// Hash chain for HashChainRandomSource. KEEP the seed file secret and off the repo (never commit it).
// usage:
//   node scripts/hashchain.js new <seedFile> [length=20000]   -> creates a random seed, prints the anchor
//   node scripts/hashchain.js next <seedFile> <currentAnchor> -> prints the preimage to reveal next
const fs = require("fs");
const crypto = require("crypto");
const { keccak256 } = require("ethers");

function chain(seedHex, length) {
  if (!/^0x[0-9a-fA-F]{64}$/.test(seedHex || "")) {
    throw new Error("seed file is damaged: 'seed' must be 0x + 64 hex (do not edit the seed file by hand)");
  }
  const out = [seedHex];
  for (let i = 1; i <= length; i++) out.push(keccak256(out[i - 1]));
  return out; // out[length] = anchor
}

const [cmd, seedFile, arg] = require.main === module ? process.argv.slice(2) : [];
if (require.main !== module) {
  // imported (tests): no CLI
} else if (cmd === "new") {
  if (fs.existsSync(seedFile)) throw new Error("seed file exists, refusing to overwrite: " + seedFile);
  const length = Number(arg || 20000);
  const seed = "0x" + crypto.randomBytes(32).toString("hex");
  fs.writeFileSync(seedFile, JSON.stringify({ seed, length }), { mode: 0o600 });
  console.log("anchor", chain(seed, length)[length]);
} else if (cmd === "next") {
  const { seed, length } = JSON.parse(fs.readFileSync(seedFile, "utf8"));
  const c = chain(seed, length);
  const i = c.indexOf(arg);
  if (i <= 0) throw new Error(i === 0 ? "chain exhausted" : "anchor not in this chain");
  console.log(c[i - 1]);
} else {
  console.log("usage: hashchain.js new <seedFile> [length] | next <seedFile> <anchor>");
}

module.exports = { chain };
