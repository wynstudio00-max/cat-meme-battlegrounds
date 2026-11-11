// Cat Battleground leaderboard API (Cloudflare Worker + D1), 2026-10-28.
//
// The beta runs offline in the browser, so the game itself reports its results. Every score is therefore checked here:
//   * a player is a random id + device secret (sha256 stored), made by POST /v1/register (5 per IP per hour)
//   * each match needs a run token from POST /v1/run/start (one per 20 s), usable once, 25 s .. 3 h later
//   * POST /v1/run/finish recomputes the points on the server and checks they are possible (placement / kills / waves / time);
//     odd runs are stored with valid = 0 and a flag for review instead of on the boards
//   * an HMAC of the result with SUBMIT_KEY (also in the game build) - only friction, a browser build can be read
//   * prizes: review the top runs by hand (GET /v1/admin/runs) before paying; ban with POST /v1/admin/ban
//
// Boards (GET /v1/leaderboard?mode=br|zombies&period=week|all[&week=N][&limit=50]), with your own row when you send auth:
//   br week     sum of your best 10 match points this week      br all      sum of all your match points
//   zombies     your best run (wave * 1000 + kills)              (week and all time)
// Weeks start Monday 00:00 UTC.

const WEEK_MS = 7 * 24 * 3600 * 1000;
const WEEK_EPOCH = Date.UTC(1970, 0, 5);          // a Monday
const BR_BEST_PER_WEEK = 10;
const BR_PLACE_POINTS = [0, 100, 70, 55, 45, 35, 28, 22, 16, 10, 5];
const BR_KILL_POINTS = 10;
const MAX_MATCH = 10;
const NAME_RE = /^[A-Z0-9 _]{3,16}$/;

const weekOf = (ms) => Math.floor((ms - WEEK_EPOCH) / WEEK_MS);
const weekStart = (w) => WEEK_EPOCH + w * WEEK_MS;

// Zombie Survival wave sizes, same formula as ZombieWaveManager.StartWave (+1 boss every 5th wave).
function zombiesInWave(n) { return Math.min(11 + 5 * (n - 1) + Math.max(0, n - 8) * 2, 130) + (n % 5 === 0 ? 1 : 0); }
function zombiesToClear(waves) { let s = 0; for (let n = 1; n <= waves; n++) s += zombiesInWave(n); return s; }

// ------------------------------------------------------------------ helpers
const enc = new TextEncoder();
async function sha256(text) {
  const d = await crypto.subtle.digest('SHA-256', enc.encode(text));
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
async function hmac(key, text) {
  const k = await crypto.subtle.importKey('raw', enc.encode(key), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const s = await crypto.subtle.sign('HMAC', k, enc.encode(text));
  return [...new Uint8Array(s)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
function randomHex(bytes) { const a = new Uint8Array(bytes); crypto.getRandomValues(a); return [...a].map((b) => b.toString(16).padStart(2, '0')).join(''); }
function equalSafe(a, b) { if (a.length !== b.length) return false; let r = 0; for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b.charCodeAt(i); return r === 0; }

function cors(env) {
  return {
    'Access-Control-Allow-Origin': env.ALLOW_ORIGIN || '*',
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type, Authorization, X-Admin-Key',
    'Access-Control-Max-Age': '86400',
  };
}
function json(env, data, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json', ...cors(env) } });
}
const fail = (env, status, error) => json(env, { ok: false, error }, status);
const int = (v, lo, hi) => { const n = Number(v); return Number.isFinite(n) ? Math.max(lo, Math.min(hi, Math.trunc(n))) : lo; };

async function body(req) { try { return await req.json(); } catch { return null; } }
async function ipHash(req, env) { return sha256((req.headers.get('CF-Connecting-IP') || 'local') + '|' + (env.IP_SALT || 'cmbg')); }

// "Authorization: Bearer <playerId>.<secret>" -> player row (or null)
async function auth(req, env) {
  const h = req.headers.get('Authorization') || '';
  const m = /^Bearer ([0-9a-f-]{36})\.([0-9a-f]{64})$/.exec(h);
  if (!m) return null;
  const row = await env.DB.prepare('SELECT * FROM players WHERE id = ?').bind(m[1]).first();
  if (!row || !equalSafe(row.secret_hash, await sha256(m[2]))) return null;
  return row;
}

function cleanName(name) {
  const n = String(name || '').toUpperCase().replace(/\s+/g, ' ').trim();
  return NAME_RE.test(n) ? n : null;
}

// Names are unique (2026-10-31: every player must create one before playing). GUEST CAT is the old default and stays shared.
// 2026-11-02: compared by a look-alike key, so "CAT KING", "CAT_KING", "CATKING" and "CAT K1NG" are one name (spaces and _
// dropped, 0 -> O, 1 -> I). The SQL expression must stay identical to the index in schema.sql.
const DEFAULT_NAME = 'GUEST CAT';
const KEY_SQL = "REPLACE(REPLACE(REPLACE(REPLACE(name, ' ', ''), '_', ''), '0', 'O'), '1', 'I')";
const nameKey = (n) => n.replace(/[ _]/g, '').replace(/0/g, 'O').replace(/1/g, 'I');
const DEFAULT_KEY = nameKey(DEFAULT_NAME);
// Another player holds this name. Names given out before the uniqueness rule may exist twice: the oldest holder keeps it
// (`me` = the asking player's row, so a younger duplicate is asked to pick a new one).
const TAKEN_SQL = `SELECT id FROM players WHERE ${KEY_SQL} = ?1 AND id != ?2 AND (?3 = 0 OR created_at <= ?4) LIMIT 1`;
function takenArgs(key, me) {
  const own = me && nameKey(me.name) === key ? 1 : 0;
  return [key, me ? me.id : '', own, me ? me.created_at : 0];
}
async function nameTaken(env, name, me) {
  const key = nameKey(name);
  if (key === DEFAULT_KEY) return name !== DEFAULT_NAME || !!me;   // GUEST CAT only as the unnamed default
  return !!(await env.DB.prepare(TAKEN_SQL).bind(...takenArgs(key, me)).first());
}

// Free names close to a taken one: NAME7, NAME_42, THE NAME, NAME CAT ... (each checked against the database).
async function suggestNames(env, name) {
  const base = name.replace(/[ _]?\d+$/, '').slice(0, 12) || name.slice(0, 12);
  const pool = new Set();
  const rnd = (lo, hi) => lo + Math.floor(Math.random() * (hi - lo + 1));
  for (const extra of ['_' + rnd(2, 99), ' ' + rnd(100, 999), String(rnd(2, 9)), ' CAT', ' PAW', '_X']) {
    const n = cleanName((base + extra).slice(0, 16));
    if (n) pool.add(n);
  }
  for (const pre of ['THE ', 'SIR ', 'MR ']) { const n = cleanName((pre + base).slice(0, 16)); if (n) pool.add(n); }
  const list = [...pool].filter((n) => nameKey(n) !== nameKey(name) && nameKey(n) !== DEFAULT_KEY);
  if (!list.length) return [];
  const keys = list.map(nameKey);
  const used = await env.DB.prepare(`SELECT DISTINCT ${KEY_SQL} AS k FROM players WHERE ${KEY_SQL} IN (${keys.map(() => '?').join(',')})`)
    .bind(...keys).all();
  const usedKeys = new Set((used.results || []).map((r) => r.k));
  return list.filter((n) => !usedKeys.has(nameKey(n))).slice(0, 3);
}

// GET /v1/name/check?name=... (auth optional: your own name counts as free) -> { available, name, suggestions }
async function checkName(req, env, url) {
  const name = cleanName(url.searchParams.get('name'));
  if (!name) return json(env, { ok: true, available: false, reason: 'invalid', suggestions: [] });
  const p = await auth(req, env);
  if (nameKey(name) === DEFAULT_KEY) return json(env, { ok: true, name, available: false, reason: 'reserved', suggestions: await suggestNames(env, name) });
  const taken = await nameTaken(env, name, p);
  return json(env, { ok: true, name, available: !taken, reason: taken ? 'taken' : '', suggestions: taken ? await suggestNames(env, name) : [] });
}

// ------------------------------------------------------------------ routes
async function register(req, env) {
  const b = (await body(req)) || {};
  const name = cleanName(b.name) || DEFAULT_NAME;
  if (await nameTaken(env, name)) return json(env, { ok: false, error: 'name taken', suggestions: await suggestNames(env, name) }, 409);
  const now = Date.now();
  const ip = await ipHash(req, env);
  const recent = await env.DB.prepare('SELECT COUNT(*) AS n FROM register_log WHERE ip_hash = ? AND at > ?').bind(ip, now - 3600e3).first();
  if (recent.n >= 5) return fail(env, 429, 'too many new players from this network, try later');
  const id = crypto.randomUUID();
  const secret = randomHex(32);
  // insert only if nobody took the name in the meantime (one statement = no race between two players)
  const insert = name === DEFAULT_NAME
    ? env.DB.prepare('INSERT INTO players (id, secret_hash, name, created_at, last_seen) VALUES (?, ?, ?, ?, ?)').bind(id, await sha256(secret), name, now, now)
    : env.DB.prepare(`INSERT INTO players (id, secret_hash, name, created_at, last_seen) SELECT ?, ?, ?, ?, ?
        WHERE NOT EXISTS (SELECT 1 FROM players WHERE ${KEY_SQL} = ?)`).bind(id, await sha256(secret), name, now, now, nameKey(name));
  const [ins] = await env.DB.batch([insert, env.DB.prepare('INSERT INTO register_log (ip_hash, at) VALUES (?, ?)').bind(ip, now)]);
  if (!ins.meta || !ins.meta.changes) return json(env, { ok: false, error: 'name taken', suggestions: await suggestNames(env, name) }, 409);
  return json(env, { ok: true, playerId: id, secret, name });
}

async function rename(req, env) {
  const p = await auth(req, env);
  if (!p) return fail(env, 401, 'unknown player');
  const name = cleanName(((await body(req)) || {}).name);
  if (!name) return fail(env, 400, 'name: 3-16 letters, digits, space or _');
  if (nameKey(name) === DEFAULT_KEY) return fail(env, 400, 'pick your own name');
  const taken = async () => json(env, { ok: false, error: 'name taken', suggestions: await suggestNames(env, name) }, 409);
  if (await nameTaken(env, name, p)) return taken();
  const args = takenArgs(nameKey(name), p);
  const r = await env.DB.prepare(`UPDATE players SET name = ?5, last_seen = ?6 WHERE id = ?2
      AND NOT EXISTS (SELECT 1 FROM players WHERE ${KEY_SQL} = ?1 AND id != ?2 AND (?3 = 0 OR created_at <= ?4))`)
    .bind(...args, name, Date.now()).run();
  if (!r.meta || !r.meta.changes) return taken();
  return json(env, { ok: true, name });
}

// 2026-11-08 (user): the leaderboard is for CONNECTED WALLETS only ("biar mudah pendataan"). A match counts only when it was
// started with a wallet; that wallet is kept with the run and the boards are one row per wallet (all devices of a wallet add up).
// The address is not signature-checked: pretending to be another wallet only gives points to THAT wallet, never to yourself.
const WALLET_RE = /^0x[0-9a-fA-F]{40}$/;
const cleanWallet = (w) => (WALLET_RE.test(String(w || '')) ? String(w).toLowerCase() : null);
const shortWallet = (w) => (w ? w.slice(0, 6) + '…' + w.slice(-4) : '');

async function runStart(req, env) {
  const p = await auth(req, env);
  if (!p) return fail(env, 401, 'unknown player');
  if (p.banned) return fail(env, 403, 'banned');
  const b = (await body(req)) || {};
  const mode = b.mode;
  if (mode !== 'br' && mode !== 'zombies') return fail(env, 400, 'mode');
  const wallet = cleanWallet(b.wallet);
  if (!wallet) return fail(env, 403, 'connect a wallet to play ranked');
  const now = Date.now();
  const last = await env.DB.prepare('SELECT MAX(issued_at) AS t FROM run_tokens WHERE player_id = ?').bind(p.id).first();
  if (last.t && now - last.t < 20e3) return fail(env, 429, 'slow down');
  const token = randomHex(16);
  await env.DB.batch([
    env.DB.prepare('INSERT INTO run_tokens (token, player_id, mode, issued_at, wallet) VALUES (?, ?, ?, ?, ?)').bind(token, p.id, mode, now, wallet),
    env.DB.prepare('UPDATE players SET last_seen = ?, wallet = ? WHERE id = ?').bind(now, wallet, p.id),
    env.DB.prepare('DELETE FROM run_tokens WHERE issued_at < ?').bind(now - 2 * 24 * 3600e3),   // housekeeping
  ]);
  return json(env, { ok: true, token, serverTime: now });
}

// Points and plausibility. Returns { points, flag } (flag null = fine).
function judge(mode, r, elapsedS) {
  if (mode === 'br') {
    const size = int(r.matchSize, 2, MAX_MATCH);
    const place = int(r.placement, 1, size);
    const points = BR_PLACE_POINTS[Math.min(place, 10)] + BR_KILL_POINTS * r.kills;
    if (r.placement < 1 || r.placement > size) return { points: 0, flag: 'placement out of range' };
    if (r.kills > size - place) return { points, flag: 'more kills than players knocked out before you' };
    if (elapsedS < 25) return { points, flag: 'too quick' };
    if (place === 1 && elapsedS < 90) return { points, flag: 'win too quick' };
    if (r.seconds > elapsedS + 15) return { points, flag: 'reported time longer than real time' };
    if (r.kills > 0 && elapsedS < 25 + 6 * r.kills) return { points, flag: 'kills too fast' };
    return { points, flag: null };
  }
  const waves = r.waves;
  const points = waves * 1000 + Math.min(r.kills, 999);
  const need = zombiesToClear(waves);
  if (r.kills < Math.floor(need * 0.9)) return { points, flag: 'not enough kills for those waves' };
  if (r.kills > need + zombiesInWave(waves + 1) + 2) return { points, flag: 'more kills than zombies' };
  if (elapsedS < 10 + 20 * waves) return { points, flag: 'waves too quick' };
  if (r.seconds > elapsedS + 15) return { points, flag: 'reported time longer than real time' };
  return { points, flag: null };
}

async function runFinish(req, env) {
  const p = await auth(req, env);
  if (!p) return fail(env, 401, 'unknown player');
  const b = await body(req);
  if (!b || typeof b.token !== 'string') return fail(env, 400, 'token');
  const tok = await env.DB.prepare('SELECT * FROM run_tokens WHERE token = ?').bind(b.token).first();
  if (!tok || tok.player_id !== p.id) return fail(env, 400, 'unknown token');
  if (tok.used) return fail(env, 409, 'already submitted');
  const now = Date.now();
  const elapsedS = Math.floor((now - tok.issued_at) / 1000);
  if (elapsedS > 3 * 3600) return fail(env, 410, 'token expired');

  const r = {
    kills: int(b.kills, 0, 999), placement: int(b.placement, 0, 99), waves: int(b.waves, 0, 999), damage: int(b.damage, 0, 999999),
    seconds: int(b.seconds, 0, 99999), matchSize: int(b.matchSize, 0, 99),
    character: String(b.character || '').slice(0, 16), build: String(b.build || '').slice(0, 24),
  };
  // signature over the numbers (friction only: the key ships with the game)
  if (env.SUBMIT_KEY) {
    const msg = [b.token, tok.mode, r.kills, r.placement, r.waves, r.damage, r.seconds, r.matchSize].join('|');
    if (typeof b.sig !== 'string' || !equalSafe(b.sig, await hmac(env.SUBMIT_KEY, msg))) return fail(env, 400, 'bad signature');
  }

  let { points, flag } = judge(tok.mode, r, elapsedS);
  if (p.banned) flag = 'banned player';
  if (b.dev && env.ACCEPT_DEV !== '1') flag = flag || 'development build';   // editor / dev builds count only on a test server
  if (!tok.wallet) flag = flag || 'no wallet';                               // token from before the wallet rule
  const week = weekOf(now);
  await env.DB.batch([
    env.DB.prepare('UPDATE run_tokens SET used = 1 WHERE token = ?').bind(b.token),
    env.DB.prepare(`INSERT INTO runs (player_id, mode, week, points, kills, placement, waves, damage, seconds, elapsed, character, build,
                    valid, flag, ip_hash, created_at, wallet) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
      .bind(p.id, tok.mode, week, points, r.kills, r.placement, r.waves, r.damage, r.seconds, elapsedS, r.character, r.build,
            flag ? 0 : 1, flag, await ipHash(req, env), now, tok.wallet || null),
    env.DB.prepare('UPDATE players SET last_seen = ? WHERE id = ?').bind(now, p.id),
  ]);
  const mine = flag ? null : await boardRow(env, tok.mode, 'week', week, tok.wallet);
  return json(env, { ok: true, points, counted: !flag, flag: flag || undefined, week, weekRank: mine ? mine.rank : null, weekScore: mine ? mine.score : null });
}

// Board SQL: score per WALLET, ranked (2026-11-08). ?1 = mode, ?2 = week (ignored for all time). Only runs started with a wallet
// count; runs of banned players never count. The name shown = the name of the player behind that wallet's latest run.
function boardSql(mode, period) {
  const weekCond = period === 'week' ? 'AND r.week = ?2' : 'AND ?2 >= 0';   // ?2 always used (unused bind indexes are an error)
  const runs = `SELECT r.* FROM runs r JOIN players pl ON pl.id = r.player_id
                WHERE r.mode = ?1 AND r.valid = 1 AND r.wallet IS NOT NULL AND pl.banned = 0 ${weekCond}`;
  const nameOf = `(SELECT p.name FROM runs r2 JOIN players p ON p.id = r2.player_id WHERE r2.wallet = per.wallet ORDER BY r2.created_at DESC LIMIT 1)`;
  if (mode === 'br' && period === 'week') {
    return `WITH best AS (
              SELECT r.wallet, r.points, r.kills, ROW_NUMBER() OVER (PARTITION BY r.wallet ORDER BY r.points DESC, r.id) AS rn FROM (${runs}) r),
            per AS (SELECT wallet, SUM(points) AS score, SUM(kills) AS kills, COUNT(*) AS matches FROM best WHERE rn <= ${BR_BEST_PER_WEEK} GROUP BY wallet)
            SELECT per.*, ${nameOf} AS name, RANK() OVER (ORDER BY per.score DESC, per.kills DESC) AS rank FROM per`;
  }
  const score = mode === 'br' ? 'SUM(r.points)' : 'MAX(r.points)';
  return `WITH per AS (
            SELECT r.wallet, ${score} AS score, ${mode === 'br' ? 'SUM(r.kills)' : 'MAX(r.waves)'} AS kills, COUNT(*) AS matches
            FROM (${runs}) r GROUP BY r.wallet)
          SELECT per.*, ${nameOf} AS name, RANK() OVER (ORDER BY per.score DESC, per.kills DESC) AS rank FROM per`;
}

async function boardRow(env, mode, period, week, wallet) {
  if (!wallet) return null;
  return env.DB.prepare(`SELECT * FROM (${boardSql(mode, period)}) WHERE wallet = ?3`).bind(mode, week, wallet).first();
}

async function leaderboard(req, env, url) {
  const mode = url.searchParams.get('mode') === 'zombies' ? 'zombies' : 'br';
  const period = url.searchParams.get('period') === 'all' ? 'all' : 'week';
  const now = Date.now();
  const week = url.searchParams.has('week') ? int(url.searchParams.get('week'), 0, 1e6) : weekOf(now);
  const limit = int(url.searchParams.get('limit') || 50, 1, 100);
  const rows = await env.DB.prepare(`SELECT * FROM (${boardSql(mode, period)}) ORDER BY rank LIMIT ${limit}`).bind(mode, week).all();
  const p = await auth(req, env);
  // "you" = the wallet the asking device played with last (or ?wallet= from the connected game / website)
  const myWallet = cleanWallet(url.searchParams.get('wallet')) || (p && p.wallet) || null;
  const me = myWallet ? await boardRow(env, mode, period, week, myWallet) : null;
  const strip = (r) => r && { rank: r.rank, name: r.name, wallet: shortWallet(r.wallet), score: r.score, kills: r.kills, matches: r.matches,
                              you: myWallet ? r.wallet === myWallet : false };
  return json(env, {
    ok: true, mode, period, week, weekStart: weekStart(week), weekEnd: weekStart(week + 1), serverTime: now,
    rows: rows.results.map(strip), me: strip(me),
  });
}

// ------------------------------------------------------------------ admin (header X-Admin-Key = env.ADMIN_KEY)
function admin(req, env) { const k = req.headers.get('X-Admin-Key'); return env.ADMIN_KEY && k && equalSafe(k, env.ADMIN_KEY); }

async function adminRuns(req, env, url) {
  if (!admin(req, env)) return fail(env, 403, 'admin');
  const mode = url.searchParams.get('mode') === 'zombies' ? 'zombies' : 'br';
  const week = url.searchParams.has('week') ? int(url.searchParams.get('week'), 0, 1e6) : weekOf(Date.now());
  const flagged = url.searchParams.get('flagged') === '1';
  const rows = await env.DB.prepare(`SELECT r.*, p.name, p.created_at AS player_since, p.banned FROM runs r JOIN players p ON p.id = r.player_id
                                     WHERE r.mode = ? AND r.week = ? AND r.valid = ? ORDER BY r.points DESC LIMIT 200`)
    .bind(mode, week, flagged ? 0 : 1).all();
  return json(env, { ok: true, week, rows: rows.results });
}

// GET /v1/admin/board?mode=br|zombies&period=week|all[&week=N][&format=csv] -> the full ranking with FULL wallet addresses
// (the public board only shows 0x1234…abcd). For prize lists / records.
async function adminBoard(req, env, url) {
  if (!admin(req, env)) return fail(env, 403, 'admin');
  const mode = url.searchParams.get('mode') === 'zombies' ? 'zombies' : 'br';
  const period = url.searchParams.get('period') === 'all' ? 'all' : 'week';
  const week = url.searchParams.has('week') ? int(url.searchParams.get('week'), 0, 1e6) : weekOf(Date.now());
  const rows = (await env.DB.prepare(`SELECT rank, wallet, name, score, kills, matches FROM (${boardSql(mode, period)}) ORDER BY rank`)
    .bind(mode, week).all()).results;
  if (url.searchParams.get('format') === 'csv') {
    const csv = 'rank,wallet,name,score,kills,matches\n' + rows.map((r) => [r.rank, r.wallet, `"${r.name}"`, r.score, r.kills, r.matches].join(',')).join('\n') + '\n';
    return new Response(csv, { headers: { 'Content-Type': 'text/csv', 'Content-Disposition': `attachment; filename="cmbg_${mode}_${period}_${week}.csv"`, ...cors(env) } });
  }
  return json(env, { ok: true, mode, period, week, rows });
}

async function adminBan(req, env) {
  if (!admin(req, env)) return fail(env, 403, 'admin');
  const b = (await body(req)) || {};
  await env.DB.prepare('UPDATE players SET banned = ? WHERE id = ?').bind(b.unban ? 0 : 1, String(b.playerId || '')).run();
  return json(env, { ok: true });
}

// ------------------------------------------------------------------ chain read proxy (2026-11-07)
// Some ISPs (seen: XL Axiata, Indonesia) block *.robinhood.com and *.workers.dev. The game (ChainConfig.ProxyRpcs) falls back to
// this read-only proxy on our own domain. Narrow on purpose: eth_chainId, eth_blockNumber and eth_call to the pass-card
// contract of the chosen network only. POST /v1/chain?net=testnet|mainnet with a JSON-RPC body.
const CHAINS = {
  testnet: { rpc: "https://rpc.testnet.chain.robinhood.com", card: "0xc6f7DF85195613f0514Dc9f3Eec2eD32Fa6110A7" },
  // card: env CARD_MAINNET (wrangler.toml [vars]) after the mainnet deploy
  // 2026-11-09: the public mainnet RPC answers 429 to Cloudflare's shared IPs, so Pinax (docs.robinhood.com/chain/connecting) goes
  // first; secret RPC_MAINNET_PRIVATE (e.g. Alchemy with key) before both when set.
  mainnet: { rpcs: ["https://robinhood.rpc.pinax.network", "https://rpc.mainnet.chain.robinhood.com"], card: "" },
};

const CMBG_TOKEN = "0x5031600a85a0176d440f2662bca3190e025f1a2f";   // $CMBG on Robinhood mainnet (balanceOf only)

// first upstream that delivers an answer without an HTTP / rate-limit error
async function upstreamCall(urls, payload) {
  let last = null;
  for (const u of urls) {
    try {
      const r = await fetch(u, { method: "POST", headers: { "Content-Type": "application/json" }, body: payload, signal: AbortSignal.timeout(8000) });
      const text = await r.text();
      if (r.ok && !/"code"\s*:\s*429/.test(text)) return { ok: true, text };
      last = { ok: false, text };
    } catch (e) { last = { ok: false, text: JSON.stringify({ jsonrpc: "2.0", error: { code: -32000, message: "upstream unreachable" } }) }; }
  }
  return last;
}

async function chainProxy(req, env, url) {
  const net = url.searchParams.get("net") || "testnet";
  const c = CHAINS[net];
  if (!c) return fail(env, 400, "unknown net");
  const card = String((net === "mainnet" ? env.CARD_MAINNET : env.CARD_TESTNET) || c.card).toLowerCase();
  if (!card) return fail(env, 503, "pass cards are not live on " + net);
  const b = await body(req);
  if (!b || b.jsonrpc !== "2.0" || typeof b.method !== "string") return fail(env, 400, "bad request");
  const params = Array.isArray(b.params) ? b.params : [];
  if (b.method === "eth_call") {
    const call = params[0] || {};
    const to = String(call.to || "").toLowerCase();
    // 2026-11-10: also balanceOf(address) on the $CMBG token (mainnet only), for the game's lobby coin
    const cmbgBalance = net === "mainnet" && to === CMBG_TOKEN && typeof call.data === "string" && /^0x70a08231[0-9a-fA-F]{64}$/.test(call.data);
    if ((to !== card && !cmbgBalance) || typeof call.data !== "string" || call.data.length > 2000)
      return fail(env, 403, "call not allowed");
  } else if (b.method !== "eth_chainId" && b.method !== "eth_blockNumber") {
    return fail(env, 403, "method not allowed");
  }
  const urls = net === "mainnet" ? [env.RPC_MAINNET_PRIVATE, ...c.rpcs].filter(Boolean) : [c.rpc];
  const up = await upstreamCall(urls, JSON.stringify({ jsonrpc: "2.0", id: b.id ?? 1, method: b.method, params }));
  return new Response(up.text, { status: up.ok ? 200 : 502, headers: { "Content-Type": "application/json", ...cors(env) } });
}

// ------------------------------------------------------------------ entry
export default {
  async fetch(req, env) {
    if (req.method === 'OPTIONS') return new Response(null, { headers: cors(env) });
    const url = new URL(req.url);
    try {
      switch (`${req.method} ${url.pathname}`) {
        case 'GET /v1/health': return json(env, { ok: true, week: weekOf(Date.now()) });
        case 'POST /v1/register': return await register(req, env);
        case 'POST /v1/name': return await rename(req, env);
        case 'GET /v1/name/check': return await checkName(req, env, url);
        case 'POST /v1/run/start': return await runStart(req, env);
        case 'POST /v1/run/finish': return await runFinish(req, env);
        case 'GET /v1/leaderboard': return await leaderboard(req, env, url);
        case 'GET /v1/admin/runs': return await adminRuns(req, env, url);
        case 'POST /v1/admin/ban': return await adminBan(req, env);
        case 'GET /v1/admin/board': return await adminBoard(req, env, url);
        case 'POST /v1/chain': return await chainProxy(req, env, url);      }
      return fail(env, 404, 'not found');
    } catch (e) {
      return fail(env, 500, 'server error');
    }
  },
};

export { judge, zombiesToClear, weekOf };   // for test.mjs
