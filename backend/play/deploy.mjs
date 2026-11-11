// Put the WebGL build online (2026-10-28).   node deploy.mjs          (or double-click deploy.bat)
//                                            node deploy.mjs --copy   only refresh ./public (for `npm run dev`)
// 1. copies CatBattleground/Builds/WebGL -> ./public (without the Burst debug folder), checks every file is under Cloudflare's 25 MiB
// 2. logs in if needed, deploys the Worker cmbg-play with play.catbattlegrounds.com (falls back to the workers.dev address)
// 3. checks the page and that the wasm arrives Brotli-encoded. Run it again after every new WebGL build.
import { spawnSync } from 'node:child_process';
import { cpSync, rmSync, readdirSync, statSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
process.chdir(here);
const BUILD = join(here, '..', '..', 'CatBattleground', 'Builds', 'WebGL');
const PUBLIC = join(here, 'public');
const DOMAIN = 'play.catbattlegrounds.com';
const LIMIT = 25 * 1024 * 1024;

function wrangler(args, show = false) {
  const r = spawnSync('npx', ['wrangler', ...args], { shell: true, encoding: 'utf8', stdio: show ? 'inherit' : ['pipe', 'pipe', 'pipe'] });
  return { ok: r.status === 0, out: (r.stdout || '') + (r.stderr || '') };
}
function stop(text, detail) { console.error('\nSTOPPED: ' + text + (detail ? '\n' + detail.trim().split('\n').slice(-15).join('\n') : '')); process.exit(1); }
const files = (dir) => readdirSync(dir, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? files(join(dir, e.name)) : [join(dir, e.name)]);

// 1. copy
console.log('[1/3] copying the build');
if (!existsSync(join(BUILD, 'index.html'))) stop('no WebGL build in ' + BUILD + ' (Unity: CatBattleground > Build > WebGL Client)');
rmSync(PUBLIC, { recursive: true, force: true });
cpSync(BUILD, PUBLIC, { recursive: true, filter: (src) => !/DoNotShip/i.test(src) });
let total = 0;
for (const f of files(PUBLIC)) {
  const size = statSync(f).size; total += size;
  if (size > LIMIT) stop(`${relative(PUBLIC, f)} is ${(size / 1048576).toFixed(1)} MB; Cloudflare allows 25 MB per file. Make the build smaller (or host Build/ on R2).`);
}
const built = statSync(join(BUILD, 'Build')).mtime;
console.log(`      ${(total / 1048576).toFixed(1)} MB, build from ${built.toLocaleString()}`);
if (process.argv.includes('--copy')) process.exit(0);

// 2. deploy
console.log('[2/3] uploading to Cloudflare');
if (!/associated with the email|You are logged in/i.test(wrangler(['whoami']).out)) {
  console.log('      A browser window opens: log in to Cloudflare and press "Allow".');
  if (!wrangler(['login'], true).ok) stop('login did not finish');
}
let toml = readFileSync('wrangler.toml', 'utf8');
if (!/custom_domain/.test(toml)) writeFileSync('wrangler.toml', toml.replace('[assets]', `routes = [{ pattern = "${DOMAIN}", custom_domain = true }]\n\n[assets]`));
let d = wrangler(['deploy']);
let url = d.ok ? 'https://' + DOMAIN : null;
if (!d.ok) {
  console.log(`      ${DOMAIN} could not be attached - deploying on the workers.dev address`);
  writeFileSync('wrangler.toml', readFileSync('wrangler.toml', 'utf8').replace(/routes = \[\{ pattern = "[^"]+", custom_domain = true \}\]\r?\n\r?\n/, ''));
  d = wrangler(['deploy']);
  if (!d.ok) stop('upload failed', d.out);
}
const dev = (/https:\/\/[a-z0-9.-]+\.workers\.dev/.exec(d.out) || [])[0];
url = url || dev;

// 3. check
console.log('[3/3] checking ' + url);
let ok = false;
for (let i = 0; i < 12 && !ok; i++) {
  try {
    const page = await fetch(url + '/');
    const wasm = await fetch(url + '/Build/WebGL.wasm.unityweb', { method: 'HEAD', headers: { 'Accept-Encoding': 'br' } });
    ok = page.ok && wasm.ok;
    if (ok) console.log(`      page ${page.status}, wasm ${wasm.status} ${wasm.headers.get('content-type')}`);
  } catch { /* a new domain can take a minute */ }
  if (!ok && i === 5 && dev && url !== dev) { console.log('      domain not answering yet, trying ' + dev); url = dev; }
  if (!ok) await new Promise((r) => setTimeout(r, 5000));
}
if (!ok) stop('the game does not answer at ' + url + ' yet. Wait a few minutes and open it in the browser.');
console.log(`\nDONE. Play at ${url}`);
