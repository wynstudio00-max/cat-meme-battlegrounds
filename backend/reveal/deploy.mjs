// One-step deploy of the auto-reveal Worker.        node deploy.mjs             (testnet Worker cmbg-reveal, or deploy.bat)
//                                                   node deploy.mjs --mainnet   (Worker cmbg-reveal-mainnet, wrangler --env mainnet)
// Safe to run again: keeps the operator wallet that already exists, re-sends the chain checkpoints, uploads the code.
//   1. Cloudflare login                 4. secrets OPERATOR_KEY (new wallet on first run) + CHAIN_CHECKPOINTS
//   2. read the secret seed file        5. upload
//   3. check it matches the chain       6. /health, website config.js nft.networks.<net>.revealApi, what is left to do
// env SEED_FILE = the secret seed (testnet default C:\safe-offline-folder\cmbg_testnet2.seed.json; mainnet: REQUIRED, no default).
// The seed file never leaves this PC: only every 256th link goes to Cloudflare as a secret.
//
// MAINNET ORDER (the operator wallet must exist before the contracts are deployed):
//   a) node deploy.mjs --mainnet          while [env.mainnet.vars] SOURCE is still "": only creates the operator wallet
//                                         (secret on Cloudflare) and prints its address -> OPERATOR for Contracts/scripts/deploy.js
//   b) deploy the contracts, put HashChainRandomSource into [env.mainnet.vars] SOURCE
//   c) $env:SEED_FILE = "<mainnet seed>"; node deploy.mjs --mainnet     -> checkpoints, upload, health, website config
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync, existsSync, unlinkSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir, homedir } from "node:os";
import { fileURLToPath } from "node:url";
import { Wallet, JsonRpcProvider, Contract, hexlify } from "ethers";
import { makeCheckpoints, parseCheckpoints, linkAt } from "./src/reveal.js";

const here = dirname(fileURLToPath(import.meta.url));
process.chdir(here);
const MAINNET = process.argv.includes("--mainnet");
const NET = MAINNET ? "mainnet" : "testnet";
const ENV_ARGS = MAINNET ? ["--env", "mainnet"] : [];
const SEED_FILE = process.env.SEED_FILE || (MAINNET ? "" : join(homedir(), "cmbg_testnet2.seed.json"));
const OPERATOR_FILE = MAINNET ? "operator_address_mainnet.txt" : "operator_address.txt";
const CUSTOM_URL = MAINNET ? "https://reveal.catbattlegrounds.com" : "";
const CONFIG_JS = join(here, "..", "..", "Website", "config.js");
const STEP = 256;

function wrangler(args, { show = false } = {}) {
  const r = spawnSync("npx", ["wrangler", ...args], { shell: true, encoding: "utf8", stdio: show ? "inherit" : ["pipe", "pipe", "pipe"] });
  return { ok: r.status === 0, out: (r.stdout || "") + (r.stderr || "") };
}
const step = (n, text) => console.log(`\n[${n}/6] ${text}`);
function stop(text, detail) { console.error("\nSTOPPED: " + text + (detail ? "\n" + detail.trim().split("\n").slice(-15).join("\n") : "")); process.exit(1); }
// [vars] for testnet, [env.mainnet.vars] for mainnet
function tomlVar(name) {
  const toml = readFileSync("wrangler.toml", "utf8");
  const header = MAINNET ? "[env.mainnet.vars]" : "[vars]";
  const start = toml.indexOf("\n" + header);
  if (start < 0) return undefined;
  const rest = toml.slice(start + header.length + 1);
  const section = rest.slice(0, (rest.search(/^\[/m) + 1 || rest.length + 1) - 1);
  return (new RegExp(`^${name}\\s*=\\s*"([^"]*)"`, "m").exec(section) || [])[1];
}
console.log(`reveal Worker: ${NET.toUpperCase()}`);

// 1. login
step(1, "Cloudflare login");
let who = wrangler(["whoami"]);
if (!/associated with the email|You are logged in/i.test(who.out)) {
  console.log("A browser window opens: log in to Cloudflare and press \"Allow\".");
  if (!wrangler(["login"], { show: true }).ok) stop("login did not finish");
}
console.log("   ok");

const SOURCE = tomlVar("SOURCE");
const listed = wrangler(["secret", "list", "--format", "json", ...ENV_ARGS]);
const have = new Set();
try { JSON.parse(listed.out.slice(listed.out.indexOf("["))).forEach((x) => have.add(x.name)); } catch { /* first deploy */ }
let operatorAddress = existsSync(OPERATOR_FILE) ? readFileSync(OPERATOR_FILE, "utf8").trim() : "";

function newOperatorSecret(secrets) {
  const w = Wallet.createRandom();
  secrets.OPERATOR_KEY = w.privateKey;
  operatorAddress = w.address;
  writeFileSync(OPERATOR_FILE, w.address + "\n");
  console.log("   new operator wallet " + w.address + " (its key exists only as a Cloudflare secret)");
}
function putSecrets(secrets) {
  if (!have.size) { const d0 = wrangler(["deploy", ...ENV_ARGS]); if (!d0.ok) stop("first upload failed", d0.out); }
  const tmp = join(tmpdir(), `cmbg_reveal_${process.pid}.json`);
  writeFileSync(tmp, JSON.stringify(secrets), { mode: 0o600 });
  const sb = wrangler(["secret", "bulk", `"${tmp}"`, ...ENV_ARGS]);
  unlinkSync(tmp);
  if (!sb.ok) stop("could not set the secrets", sb.out);
}

// mainnet step (a): no source yet -> only make the operator wallet
if (MAINNET && !SOURCE) {
  step(2, "no SOURCE yet in [env.mainnet.vars]: creating the mainnet operator wallet only");
  if (have.has("OPERATOR_KEY") && operatorAddress) {
    console.log("   operator wallet already exists: " + operatorAddress);
  } else {
    const s = {};
    newOperatorSecret(s);
    putSecrets(s);
  }
  console.log(`\nNEXT: deploy the contracts with  $env:OPERATOR = "${operatorAddress}"  (Contracts/scripts/deploy.js),\n` +
    `      put the HashChainRandomSource address into wrangler.toml [env.mainnet.vars] SOURCE,\n` +
    `      then run  $env:SEED_FILE = "<mainnet seed file>"; node deploy.mjs --mainnet`);
  process.exit(0);
}

// 2. seed -> checkpoints
step(2, "seed file " + (SEED_FILE || "(none)"));
if (!SEED_FILE) stop("set $env:SEED_FILE to the MAINNET seed file (there is no default for mainnet)");
if (!existsSync(SEED_FILE)) stop("seed file not found (set SEED_FILE)");
const { seed, length } = JSON.parse(readFileSync(SEED_FILE, "utf8"));
if (!/^0x[0-9a-fA-F]{64}$/.test(seed || "")) stop("seed file is damaged ('seed' must be 0x + 64 hex)");
const cp = makeCheckpoints(seed, length, STEP, SOURCE);
console.log(`   ${length} links, ${Math.ceil((length + 1) / STEP)} checkpoints, secret size ${JSON.stringify(cp).length} bytes`);

// 3. the seed must be the one behind the source's anchor
step(3, "check against the chain (" + SOURCE + ")");
const provider = new JsonRpcProvider(tomlVar("RPC_URL"), Number(tomlVar("CHAIN_ID")), { staticNetwork: true });
const src = new Contract(SOURCE, ["function anchor() view returns (bytes32)", "function head() view returns (uint256)", "function operator() view returns (address)"], provider);
const [anchor, head, currentOperator] = await Promise.all([src.anchor(), src.head(), src.operator()]);
const chain = parseCheckpoints(cp);
const idx = length - Number(head);
if (hexlify(linkAt(chain, idx)) !== anchor.toLowerCase()) stop("this seed does not belong to the source's current anchor (wrong SEED_FILE or SOURCE)");
console.log(`   ok: anchor = link ${idx}, ${idx} reveals left`);

// 4. secrets
step(4, "secrets");
const secrets = { CHAIN_CHECKPOINTS: JSON.stringify({ v: cp.v, source: cp.source, length: cp.length, step: cp.step, cps: cp.cps }) };
if (!have.has("OPERATOR_KEY")) newOperatorSecret(secrets);
putSecrets(secrets);
console.log("   " + Object.keys(secrets).join(", ") + " set");

// 5. upload
step(5, "upload");
const d = wrangler(["deploy", ...ENV_ARGS]);
if (!d.ok) stop("upload failed", d.out);
const devUrl = (/https:\/\/[a-z0-9.-]+\.workers\.dev/.exec(d.out) || [])[0];
console.log("   " + [CUSTOM_URL, devUrl].filter(Boolean).join("  +  "));

// 6. check + website config (custom domain first: a new custom domain can take a few minutes)
step(6, "check");
let health = null, url = null;
for (let i = 0; i < 12 && !health; i++) {
  for (const u of [CUSTOM_URL, devUrl].filter(Boolean)) {
    try { const r = await fetch(u + "/health"); if (r.ok) { health = await r.json(); url = u; break; } } catch { /* not up yet */ }
  }
  if (!health) await new Promise((r) => setTimeout(r, 5000));
}
if (!health) stop("the Worker does not answer at /health yet. Run deploy again in a minute.");
operatorAddress = health.operator || operatorAddress;
console.log(`   ${url}: pending ${health.pending}, operator ${health.operator}, balance ${health.operatorBalanceEth} ETH`);
const siteUrl = CUSTOM_URL || url;
if (existsSync(CONFIG_JS)) {
  // write revealApi inside nft.networks.<net> (the block that starts with "<net>: {")
  const c = readFileSync(CONFIG_JS, "utf8");
  const re = new RegExp(`(\\n\\s*${NET}:\\s*\\{[\\s\\S]*?revealApi:\\s*")[^"]*(")`);
  const n = c.replace(re, `$1${siteUrl}$2`);
  if (n !== c) { writeFileSync(CONFIG_JS, n); console.log(`   Website/config.js nft.networks.${NET}.revealApi = ${siteUrl}`); }
}

console.log("\nDONE. Worker online at " + url);
const todo = [];
if (currentOperator.toLowerCase() !== operatorAddress.toLowerCase()) {
  todo.push(MAINNET
    ? `the source's operator is ${currentOperator}, the Worker's is ${operatorAddress}: with the owner key (in Contracts)\n     $env:ACTION = "setOperator"; $env:ARG = "${operatorAddress}"; npx hardhat run scripts/admin.js --network robinhoodMainnet\n     (a Safe owner instead: node scripts/safe_calldata.js robinhoodMainnet setOperator ${operatorAddress})`
    : `make it the operator (owner key, in G:\\PROJECT-GAME-WAR-CAT\\Contracts):\n     $env:OPERATOR = "${operatorAddress}"\n     npx hardhat run scripts/set_operator.js --network robinhoodTestnet`);
}
if (Number(health.operatorBalanceEth) < 0.0005) todo.push(`send a little ${MAINNET ? "MAINNET" : "testnet"} ETH (e.g. 0.002) to the operator ${operatorAddress} for gas`);
if (todo.length) console.log("\nStill to do:\n" + todo.map((t, i) => `  ${i + 1}. ${t}`).join("\n"));
