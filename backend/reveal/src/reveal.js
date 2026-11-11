// Auto-reveal for HashChainRandomSource (Contracts/contracts/HashChainRandomSource.sol).
// The secret hash chain h(0) = seed, h(i) = keccak256(h(i-1)), anchor = h(length) is NOT stored whole: CHAIN_CHECKPOINTS holds
// every `step`-th link (h(0), h(step), h(2*step), ...), so any link costs < step hashes (fits the Workers CPU limit).
// The current anchor of the source is h(length - head) (one link is used per reveal, anchor set once at construction).
import { Contract, JsonRpcProvider, Wallet, hexlify, getBytes, formatEther } from "ethers";
import { keccak_256 } from "@noble/hashes/sha3";

const SOURCE_ABI = [
  "function anchor() view returns (bytes32)",
  "function head() view returns (uint256)",
  "function pending() view returns (uint256)",
  "function operator() view returns (address)",
  "function revealBatch(bytes32[] preimages)",
];

/** { v, source, length, step, cps: base64 of the concatenated 32-byte checkpoints } */
export function parseCheckpoints(json) {
  const c = typeof json === "string" ? JSON.parse(json) : json;
  const raw = Uint8Array.from(atob(c.cps), (ch) => ch.charCodeAt(0));
  const cps = [];
  for (let i = 0; i < raw.length; i += 32) cps.push(raw.slice(i, i + 32));
  return { source: c.source, length: c.length, step: c.step, cps };
}

/** links h(from) .. h(to) (inclusive, from <= to, same or different segments), as Uint8Arrays */
function segment(chain, from, to) {
  const out = [];
  let j = Math.floor(from / chain.step);
  let h = chain.cps[j];
  for (let i = j * chain.step; i <= to; i++) {
    if (i > j * chain.step) h = keccak_256(h);
    if (i >= from) out.push(h);
  }
  return out;
}

export function linkAt(chain, k) {
  return segment(chain, k, k)[0];
}

/** the next `count` preimages to reveal when the anchor is h(idx): h(idx-1), h(idx-2), ... */
export function nextLinks(chain, idx, count) {
  const n = Math.min(count, idx);
  if (n <= 0) return [];
  return segment(chain, idx - n, idx - 1).reverse();
}

// 2026-11-08: the public mainnet RPC answers 429 (Too Many Requests) to Cloudflare's shared IPs, so every reveal failed with
// "exceeded maximum retry limit". RPC_URL may now list several endpoints (comma separated); the secret RPC_PRIVATE (e.g. an
// Alchemy URL with key) goes first when set. The first endpoint that answers eth_chainId with the right chain within 6 s is used.
async function pickRpc(env) {
  const urls = [env.RPC_PRIVATE, ...String(env.RPC_URL || "").split(",")].map((s) => (s || "").trim()).filter(Boolean);
  const want = "0x" + Number(env.CHAIN_ID).toString(16);
  for (const url of urls) {
    try {
      const r = await fetch(url, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "eth_chainId", params: [] }),
        signal: AbortSignal.timeout(6000),
      });
      if (r.ok && (await r.json()).result === want) return url;
    } catch { /* next */ }
  }
  return urls[urls.length - 1];   // nothing answered: let the real call report the error
}

async function setup(env) {
  const provider = new JsonRpcProvider(await pickRpc(env), Number(env.CHAIN_ID), { staticNetwork: true, batchMaxCount: 1 });
  const wallet = new Wallet(env.OPERATOR_KEY, provider);
  const source = new Contract(env.SOURCE, SOURCE_ABI, wallet);
  return { provider, wallet, source };
}

export async function status(env) {
  const { provider, wallet, source } = await setup(env);
  const [pending, head, operator, balance] = await Promise.all([
    source.pending(), source.head(), source.operator(), provider.getBalance(wallet.address),
  ]);
  return {
    source: env.SOURCE,
    pending: Number(pending),
    revealed: Number(head),
    operator: wallet.address,
    operatorIsSet: operator.toLowerCase() === wallet.address.toLowerCase(),
    operatorBalanceEth: formatEther(balance),
  };
}

/** Reveals everything that is waiting (up to maxBatch per transaction). Safe to call often: does nothing when idle. */
export async function runReveal(env, { maxBatch = 40, wait = false } = {}) {
  const { source } = await setup(env);
  const pending = Number(await source.pending());
  if (pending === 0) return { pending: 0 };

  const chain = parseCheckpoints(env.CHAIN_CHECKPOINTS);
  if (chain.source && chain.source.toLowerCase() !== env.SOURCE.toLowerCase()) throw new Error("checkpoints belong to another source");
  const [head, anchor] = await Promise.all([source.head(), source.anchor()]);
  const idx = chain.length - Number(head);
  if (idx <= 0) throw new Error("hash chain exhausted: deploy a new source / anchor");
  if (hexlify(linkAt(chain, idx)) !== anchor.toLowerCase()) throw new Error("on-chain anchor is not where this chain expects it");

  const batch = nextLinks(chain, idx, Math.min(pending, maxBatch)).map((b) => hexlify(b));
  // a parallel run (cron + poke) may have revealed these already: estimate first, skip quietly if it no longer fits
  try {
    await source.revealBatch.estimateGas(batch);
  } catch (e) {
    return { pending, skipped: "estimate failed (probably revealed by a parallel run)", reason: e.shortMessage || e.message };
  }
  const tx = await source.revealBatch(batch);
  if (wait) await tx.wait();
  return { pending, revealed: batch.length, tx: tx.hash, linksLeft: idx - batch.length };
}

/** builds the CHAIN_CHECKPOINTS secret from a seed file's { seed, length } */
export function makeCheckpoints(seedHex, length, step, source) {
  const parts = [];
  let h = getBytes(seedHex);
  for (let i = 0; i <= length; i++) {
    if (i > 0) h = keccak_256(h);
    if (i % step === 0) parts.push(h);
  }
  const all = new Uint8Array(parts.length * 32);
  parts.forEach((p, i) => all.set(p, i * 32));
  let bin = "";
  all.forEach((b) => { bin += String.fromCharCode(b); });
  return { v: 1, source, length, step, cps: btoa(bin), anchor: hexlify(h) };
}
