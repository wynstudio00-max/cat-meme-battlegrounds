// One-step deploy of the leaderboard to Cloudflare (2026-10-28).   node deploy.mjs   (or double-click deploy.bat)
// Safe to run again: it reuses the database and secrets that already exist and just uploads the new code.
//   1. log in to Cloudflare (browser opens once)          4. secrets SUBMIT_KEY (from the game), ADMIN_KEY (new, saved to admin_key.txt), IP_SALT
//   2. create the D1 database cmbg-leaderboard + id       5. deploy, with api.catbattlegrounds.com as custom domain when that zone is in the account
//   3. create the tables (schema.sql)                     6. check /v1/health and tell the game the address (LeaderboardApi.DefaultUrl)
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync, unlinkSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
process.chdir(here);
const DB = 'cmbg-leaderboard';
const DOMAIN = 'api.catbattlegrounds.com';
const ZONE = 'catbattlegrounds.com';
const API_CS = join(here, '..', '..', 'CatBattleground', 'Assets', 'Scripts', 'Online', 'LeaderboardApi.cs');

function wrangler(args, { show = false, input } = {}) {
  const r = spawnSync('npx', ['wrangler', ...args], { shell: true, encoding: 'utf8', input, stdio: show ? 'inherit' : ['pipe', 'pipe', 'pipe'] });
  return { ok: r.status === 0, out: (r.stdout || '') + (r.stderr || '') };
}
const step = (n, text) => console.log(`\n[${n}/6] ${text}`);
function stop(text, detail) { console.error('\nSTOPPED: ' + text + (detail ? '\n' + detail.trim().split('\n').slice(-15).join('\n') : '')); process.exit(1); }

// 1. login
step(1, 'Cloudflare login');
let who = wrangler(['whoami']);
if (!/associated with the email|You are logged in/i.test(who.out)) {
  console.log('A browser window opens: log in to Cloudflare and press "Allow".');
  if (!wrangler(['login'], { show: true }).ok) stop('login did not finish');
  who = wrangler(['whoami']);
}
const email = (/email ([^\s]+@[^\s]+?)\.?\s/i.exec(who.out) || [])[1];
console.log('   logged in' + (email ? ' as ' + email : ''));

// 2. database
step(2, 'database ' + DB);
const findDb = () => { const l = wrangler(['d1', 'list', '--json']); try { return JSON.parse(l.out.slice(l.out.indexOf('['))).find((d) => d.name === DB); } catch { return null; } };
let db = findDb();
if (!db) {
  const c = wrangler(['d1', 'create', DB]);
  if (!c.ok) stop('could not create the database', c.out);
  db = findDb();
}
const dbId = db && (db.uuid || db.database_id || db.id);
if (!dbId) stop('database id not found');
let toml = readFileSync('wrangler.toml', 'utf8').replace(/database_id = "[^"]*"/, `database_id = "${dbId}"`);
writeFileSync('wrangler.toml', toml);
console.log('   id ' + dbId);

// 3. tables (CREATE ... IF NOT EXISTS: harmless when they exist)
step(3, 'tables');
// one-time column additions for a database made before them (2026-11-08 wallet): "duplicate column" = already done
for (const m of ['migrations/2026-11-08_wallet.sql']) {
  const r = wrangler(['d1', 'execute', DB, '--remote', '--yes', `--file=${m}`]);
  if (!r.ok && !/duplicate column|no such table/i.test(r.out)) stop('migration ' + m + ' failed', r.out);
  console.log('   ' + m + (r.ok ? ' applied' : ' (already applied / new database)'));
}
const s = wrangler(['d1', 'execute', DB, '--remote', '--yes', '--file=schema.sql']);
if (!s.ok) stop('could not create the tables', s.out);
console.log('   ok');

// 4. secrets
step(4, 'secrets');
const listed = wrangler(['secret', 'list', '--format', 'json']);
const have = new Set(); try { JSON.parse(listed.out.slice(listed.out.indexOf('['))).forEach((x) => have.add(x.name)); } catch { /* first deploy: none */ }
const submitKey = (/const string SubmitKey = "([0-9a-f]+)"/.exec(existsSync(API_CS) ? readFileSync(API_CS, 'utf8') : '') || [])[1];
if (!submitKey) stop('SubmitKey not found in ' + API_CS);
const secrets = { SUBMIT_KEY: submitKey };   // always re-sent: must match the game
if (!have.has('ADMIN_KEY')) {
  const adminKey = randomBytes(18).toString('hex');
  secrets.ADMIN_KEY = adminKey;
  writeFileSync('admin_key.txt', adminKey + '\n');
  console.log('   new ADMIN_KEY saved in Backend/leaderboard/admin_key.txt (keep it private)');
}
if (!have.has('IP_SALT')) secrets.IP_SALT = randomBytes(12).toString('hex');
// the Worker must exist before secrets can be added: a first deploy without the domain
if (!have.size) { const d0 = wrangler(['deploy']); if (!d0.ok) stop('first upload failed', d0.out); }
writeFileSync('.secrets.tmp.json', JSON.stringify(secrets));
const sb = wrangler(['secret', 'bulk', '.secrets.tmp.json']);
unlinkSync('.secrets.tmp.json');
if (!sb.ok) stop('could not set the secrets', sb.out);
console.log('   ' + Object.keys(secrets).join(', ') + ' set');

// 5. deploy (+ custom domain when the zone is on this account)
step(5, 'upload');
let url = null;
toml = readFileSync('wrangler.toml', 'utf8');
if (!/custom_domain/.test(toml)) toml = toml.replace('[[d1_databases]]', `routes = [{ pattern = "${DOMAIN}", custom_domain = true }]\n\n[[d1_databases]]`);   // top level: before the first [[table]]
// with routes wrangler switches the workers.dev address off; older game builds still call it
if (!/^workers_dev\s*=/m.test(toml)) toml = toml.replace('[[d1_databases]]', 'workers_dev = true\n\n[[d1_databases]]');
writeFileSync('wrangler.toml', toml);
let d = wrangler(['deploy']);
if (d.ok) url = 'https://' + DOMAIN;
else {
  console.log(`   ${DOMAIN} could not be attached (is ${ZONE} in this Cloudflare account?) - deploying without it`);
  writeFileSync('wrangler.toml', readFileSync('wrangler.toml', 'utf8').replace(/routes = \[\{ pattern = "[^"]+", custom_domain = true \}\]\r?\n\r?\n/, ''));
  d = wrangler(['deploy']);
  if (!d.ok) stop('upload failed', d.out);
}
const dev = (/https:\/\/[a-z0-9.-]+\.workers\.dev/.exec(d.out) || [])[0];
url = url || dev;
console.log('   ' + (url || '(address not found in the output)') + (dev && url !== dev ? '   (also ' + dev + ')' : ''));

// 6. check + tell the game
step(6, 'check');
let healthy = false;
for (let i = 0; i < 12 && url && !healthy; i++) {
  try { healthy = (await fetch(url + '/v1/health')).ok; } catch { /* DNS / certificate for a new domain can take a minute */ }
  if (!healthy && dev && url !== dev && i === 5) { try { if ((await fetch(dev + '/v1/health')).ok) { console.log('   custom domain not answering yet, using ' + dev); url = dev; } } catch {} }
  if (!healthy) await new Promise((r) => setTimeout(r, 5000));
}
if (!healthy) stop('the API does not answer at ' + url + ' yet. Wait a few minutes and run deploy again.');
const cs = readFileSync(API_CS, 'utf8');
const newCs = cs.replace(/public const string DefaultUrl = "[^"]*";/, `public const string DefaultUrl = "${url}";`);
if (newCs !== cs) { writeFileSync(API_CS, newCs); console.log('   LeaderboardApi.DefaultUrl = ' + url + ' (rebuild WebGL to use it)'); }
console.log(`\nDONE. Leaderboard online at ${url}\n  check:  ${url}/v1/leaderboard?mode=br&period=week`);
