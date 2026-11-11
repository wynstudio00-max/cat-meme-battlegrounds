// Tells what kind of address each CONTRACT holder in a snapshot report is, so the exclude list can be decided:
//   delegated wallet = a normal person's wallet with an EIP-7702 smart-account delegation (code 0xef0100 + address) -> KEEP
//   pool             = DEX pair / pool (answers token0 + token1)                                                     -> EXCLUDE
//   safe             = Safe multisig (answers getThreshold + getOwners)                                               -> ask the owner
//   contract         = something else (launchpad, locker, router, vault, ...)                                        -> look it up
// usage: node scripts/classify_holders.js <holders.json> [minBalance=150000]
const fs = require("fs");
const { JsonRpcProvider, Interface } = require("ethers");

const RPC = process.env.RPC || "https://rpc.mainnet.chain.robinhood.com";
const provider = new JsonRpcProvider(RPC, undefined, { staticNetwork: true, batchMaxCount: 1 });
const iface = new Interface([
  "function token0() view returns (address)", "function token1() view returns (address)", "function fee() view returns (uint24)",
  "function getThreshold() view returns (uint256)", "function getOwners() view returns (address[])",
  "function symbol() view returns (string)", "function name() view returns (string)", "function owner() view returns (address)",
]);
async function tryCall(to, fn) {
  try { return iface.decodeFunctionResult(fn, await provider.call({ to, data: iface.encodeFunctionData(fn) })); } catch { return null; }
}
async function symbolOf(a) { const s = await tryCall(a, "symbol"); return s ? s[0] : a.slice(0, 8); }

async function main() {
  const rep = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
  const min = Number(process.argv[3] || 150000);
  const out = [];
  for (const h of rep.holders.filter((x) => x.contract && Number(x.balance) >= min)) {
    const code = await provider.getCode(h.address);
    let kind = "contract", info = `${(code.length - 2) / 2} bytes`;
    if (code.startsWith("0xef0100")) { kind = "delegated wallet"; info = "7702 -> 0x" + code.slice(8); }
    else {
      const t0 = await tryCall(h.address, "token0"), t1 = await tryCall(h.address, "token1");
      if (t0 && t1) {
        const fee = await tryCall(h.address, "fee");
        kind = "pool"; info = `${await symbolOf(t0[0])}/${await symbolOf(t1[0])}${fee ? " fee " + fee[0] : ""}`;
      } else {
        const th = await tryCall(h.address, "getThreshold"), ow = await tryCall(h.address, "getOwners");
        if (th && ow) { kind = "safe"; info = `${th[0]} of ${ow[0].length}: ${ow[0].join(" ")}`; }
        else {
          const nm = await tryCall(h.address, "name"), own = await tryCall(h.address, "owner");
          info += (nm ? ` name "${nm[0]}"` : "") + (own ? ` owner ${own[0]}` : "");
        }
      }
    }
    out.push({ address: h.address, balance: h.balance, kind, info });
    console.log(`${h.address}  ${Number(h.balance).toLocaleString("en-US", { maximumFractionDigits: 0 }).padStart(13)}  ${kind.padEnd(16)} ${info}`);
  }
  return out;
}
main().catch((e) => { console.error(e.message || e); process.exitCode = 1; });
