// End-to-end test of the reveal logic + Worker handler against a local Hardhat node (no Cloudflare, no real keys).
// 1) cd Contracts && npx hardhat node      2) cd Backend/reveal && node test_local.mjs
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { randomBytes } from "node:crypto";
import { JsonRpcProvider, ContractFactory, Contract, HDNodeWallet, Mnemonic, AbiCoder, keccak256, concat, hexlify, NonceManager } from "ethers";
import { makeCheckpoints, parseCheckpoints, linkAt, nextLinks, runReveal, status } from "./src/reveal.js";
import worker from "./src/index.js";

const here = dirname(fileURLToPath(import.meta.url));
const art = (n) => JSON.parse(readFileSync(join(here, "..", "..", "Contracts", "artifacts", "contracts", `${n}.sol`, `${n}.json`), "utf8"));
const RPC = "http://127.0.0.1:8545";
const provider = new JsonRpcProvider(RPC, 31337, { staticNetwork: true });
const phrase = Mnemonic.fromPhrase("test test test test test test test test test test test junk");
const acct = (i) => HDNodeWallet.fromMnemonic(phrase, `m/44'/60'/0'/0/${i}`).connect(provider);

let failures = 0;
function check(name, cond, extra) { console.log((cond ? "  ok   " : "  FAIL ") + name + (extra ? "  " + extra : "")); if (!cond) failures++; }

// --- pure chain math (segments crossing checkpoints) ---
const seed = hexlify(randomBytes(32));
const LENGTH = 260, STEP = 256;
const full = [seed];
for (let i = 1; i <= LENGTH; i++) full.push(keccak256(full[i - 1]));
const cpJson = makeCheckpoints(seed, LENGTH, STEP, "0x0000000000000000000000000000000000000000");
check("anchor from checkpoints == full chain end", cpJson.anchor === full[LENGTH]);
const chain = parseCheckpoints(JSON.stringify(cpJson));
check("linkAt matches for 0, 255, 256, 259, 260", [0, 255, 256, 259, 260].every((k) => hexlify(linkAt(chain, k)) === full[k]));
const nl = nextLinks(chain, 258, 5).map(hexlify);
check("nextLinks(258,5) = h257..h253 (crosses 256)", JSON.stringify(nl) === JSON.stringify([257, 256, 255, 254, 253].map((k) => full[k])));

// --- contracts on the local node ---
const owner = new NonceManager(acct(0));
const operator = acct(9);
const card = await new ContractFactory(art("CMBGPassCard").abi, art("CMBGPassCard").bytecode, owner).deploy(acct(0).address, "0x" + "00".repeat(32), "ipfs://T/");
await card.waitForDeployment();
const cardAddr = await card.getAddress();
const src = await new ContractFactory(art("HashChainRandomSource").abi, art("HashChainRandomSource").bytecode, owner)
  .deploy(acct(0).address, cardAddr, operator.address, full[LENGTH]);
await src.waitForDeployment();
const srcAddr = await src.getAddress();
await (await card.setRandomSource(srcAddr)).wait();

// 6 wallets x 2 commons, one Merkle root over all of them (tiny tree built by hand)
const coder = AbiCoder.defaultAbiCoder();
const users = [1, 2, 3, 4, 5, 6].map(acct);
const leaves = users.map((u) => keccak256(concat([keccak256(coder.encode(["address", "uint8", "uint8", "uint8"], [u.address, 2, 0, 0]))])));
const H = (a, b) => (BigInt(a) < BigInt(b) ? keccak256(concat([a, b])) : keccak256(concat([b, a])));
function tree(ls) { const levels = [ls]; while (levels.at(-1).length > 1) { const p = levels.at(-1), n = []; for (let i = 0; i < p.length; i += 2) n.push(i + 1 < p.length ? H(p[i], p[i + 1]) : p[i]); levels.push(n); } return levels; }
const lv = tree(leaves);
function proof(i) { const out = []; for (let l = 0; l < lv.length - 1; l++) { const sib = i ^ 1; if (sib < lv[l].length) out.push(lv[l][sib]); i >>= 1; } return out; }
await (await card.setMerkleRoot(lv.at(-1)[0])).wait();
for (let i = 0; i < users.length; i++) {
  const c = new Contract(cardAddr, art("CMBGPassCard").abi, new NonceManager(users[i]));
  await (await c.claim(2, 0, 0, proof(i))).wait();
  const [ids] = await c.cardsOf(users[i].address);
  for (const id of ids) await (await c.open(id)).wait();
}
check("12 cards waiting", Number(await src.pending()) === 12);

const env = {
  RPC_URL: RPC, CHAIN_ID: "31337", SOURCE: srcAddr, OPERATOR_KEY: operator.privateKey, ALLOW_ORIGIN: "*",
  CHAIN_CHECKPOINTS: JSON.stringify({ ...cpJson, source: srcAddr }),
};
const st = await status(env);
check("status: operator set, 12 pending", st.operatorIsSet && st.pending === 12, JSON.stringify(st));

// batch of 5, then the rest through the Worker's POST /poke handler
const r1 = await runReveal(env, { maxBatch: 5, wait: true });
check("runReveal revealed 5", r1.revealed === 5, JSON.stringify(r1));
const res = await worker.fetch(new Request("https://x/poke", { method: "POST", headers: { Origin: "http://localhost:8090" } }), env, {});
const body = await res.json();
await provider.waitForTransaction(body.tx);
check("POST /poke revealed the other 7", res.status === 200 && body.revealed === 7, JSON.stringify(body));
check("CORS header", res.headers.get("Access-Control-Allow-Origin") === "*");
check("nothing pending now", Number(await src.pending()) === 0);
const r3 = await runReveal(env);
check("idle run does nothing", r3.pending === 0 && !r3.tx);

let allOpened = true;
for (const u of users) { const [, , chars, states] = await card.cardsOf(u.address); states.forEach((s, i) => { if (Number(s) !== 3 || ![1, 2, 4].includes(Number(chars[i]))) allOpened = false; }); }
check("all 12 cards opened with a Common character", allOpened);

// cron path
let cronDone;
await worker.scheduled({}, env, { waitUntil: (p) => { cronDone = p; } });
await cronDone;
check("cron handler runs without error", true);

// wrong checkpoints are refused
const bad = { ...env, CHAIN_CHECKPOINTS: JSON.stringify({ ...makeCheckpoints(hexlify(randomBytes(32)), LENGTH, STEP, srcAddr) }) };
const c7 = new Contract(cardAddr, art("CMBGPassCard").abi, new NonceManager(acct(7)));
let refused = false;
try { await (await card.setMerkleRoot(keccak256(concat([keccak256(coder.encode(["address", "uint8", "uint8", "uint8"], [acct(7).address, 1, 0, 0]))])))).wait();
  await (await c7.claim(1, 0, 0, [])).wait(); await (await c7.open(13)).wait(); await runReveal(bad); } catch (e) { refused = /anchor/.test(e.message); }
check("a seed that does not match the anchor is refused", refused);
const h = await worker.fetch(new Request("https://x/health"), env, {});
check("GET /health", h.status === 200 && (await h.json()).pending === 1);

console.log(failures ? `\n${failures} FAILED` : "\nall passed");
process.exit(failures ? 1 : 0);
