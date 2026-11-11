// node test.mjs [baseUrl]   - rule tests, then (if a server answers at baseUrl, default http://localhost:8787) an API run-through.
// The API part needs `npm run db:local` once and `npm run dev` in another terminal (.dev.vars with SUBMIT_KEY=test-submit-key, ADMIN_KEY=test-admin).
import { createHmac } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { judge, zombiesToClear } from './src/index.js';

let failed = 0;
const check = (what, ok) => { console.log((ok ? 'ok   ' : 'FAIL ') + what); if (!ok) failed++; };

// ---- rules
check('br win 3 kills after 10 min = 130 pts', judge('br', { placement: 1, kills: 3, matchSize: 10, seconds: 590 }, 600).points === 130 && !judge('br', { placement: 1, kills: 3, matchSize: 10, seconds: 590 }, 600).flag);
check('br 5th with 6 kills is impossible', !!judge('br', { placement: 5, kills: 6, matchSize: 10, seconds: 200 }, 300).flag);
check('br win in 40 s is flagged', !!judge('br', { placement: 1, kills: 0, matchSize: 10, seconds: 30 }, 40).flag);
check('br reported time > real time flagged', !!judge('br', { placement: 4, kills: 1, matchSize: 10, seconds: 900 }, 300).flag);
check('zombies to clear 5 waves = 11+16+21+26+31+1 = 106', zombiesToClear(5) === 106);
check('zombies 5 waves 110 kills 6 min fine', !judge('zombies', { waves: 5, kills: 110, seconds: 350 }, 360).flag && judge('zombies', { waves: 5, kills: 110 }, 360).points === 5110);
check('zombies 5 waves 20 kills flagged', !!judge('zombies', { waves: 5, kills: 20, seconds: 350 }, 360).flag);
check('zombies 5 waves in 60 s flagged', !!judge('zombies', { waves: 5, kills: 106, seconds: 50 }, 60).flag);

// ---- API
const base = process.argv[2] || 'http://localhost:8787';
const devVars = existsSync('.dev.vars') ? Object.fromEntries(readFileSync('.dev.vars', 'utf8').split(/\r?\n/).filter((l) => l.includes('=')).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)])) : {};
const key = process.env.SUBMIT_KEY || devVars.SUBMIT_KEY || 'test-submit-key';
let up = false;
try { up = (await fetch(base + '/v1/health')).ok; } catch { /* not running */ }
if (!up) { console.log(`(no server at ${base}: API part skipped)`); process.exit(failed ? 1 : 0); }

async function call(method, path, body, auth) {
  const r = await fetch(base + path, { method, headers: { 'Content-Type': 'application/json', ...(auth ? { Authorization: auth } : {}) }, body: body ? JSON.stringify(body) : undefined });
  return { status: r.status, data: await r.json() };
}
const sign = (t, mode, r) => createHmac('sha256', key).update([t, mode, r.kills, r.placement, r.waves, r.damage, r.seconds, r.matchSize].join('|')).digest('hex');

const tag = Math.random().toString(36).slice(2, 7).toUpperCase();   // names are unique: a fresh one per test run
const NAME = 'TEST ' + tag, NAME2 = 'WHISKER ' + tag;
const reg = await call('POST', '/v1/register', { name: NAME.toLowerCase() });
check('register', reg.data.ok && reg.data.name === NAME);
const dup = await call('POST', '/v1/register', { name: NAME });
check('same name on a new player refused + suggestions', dup.status === 409 && dup.data.suggestions.length > 0);
const lookAlike = NAME.replace(' ', '_').replace(/O/g, '0').replace(/I/g, '1');
check('look-alike name refused (' + lookAlike + ')', (await call('POST', '/v1/register', { name: lookAlike })).status === 409);
check('look-alike without space refused', (await call('POST', '/v1/register', { name: NAME.replace(' ', '') })).status === 409);
const auth = `Bearer ${reg.data.playerId}.${reg.data.secret}`;
const chk = await call('GET', '/v1/name/check?name=' + encodeURIComponent(NAME.toLowerCase()));
check('check: taken for others', chk.data.ok && chk.data.available === false && chk.data.reason === 'taken' && chk.data.suggestions.length > 0);
const sugg = chk.data.suggestions[0];
check('check: a suggestion is free (' + sugg + ')', (await call('GET', '/v1/name/check?name=' + encodeURIComponent(sugg))).data.available === true);
check('check: own name free for me', (await call('GET', '/v1/name/check?name=' + encodeURIComponent(NAME), null, auth)).data.available === true);
check('check: GUEST CAT reserved', (await call('GET', '/v1/name/check?name=GUEST_CAT')).data.available === false);
check('check: invalid', (await call('GET', '/v1/name/check?name=a!')).data.reason === 'invalid');
check('wrong secret refused', (await call('POST', '/v1/run/start', { mode: 'br' }, `Bearer ${reg.data.playerId}.${'0'.repeat(64)}`)).status === 401);

// 2026-11-08: ranked matches need a connected wallet; the board is one row per wallet
const WALLET = '0x' + [...Array(40)].map(() => '0123456789abcdef'[Math.floor(Math.random() * 16)]).join('');
check('run start without a wallet refused (403)', (await call('POST', '/v1/run/start', { mode: 'zombies' }, auth)).status === 403);
check('run start with a bad wallet refused', (await call('POST', '/v1/run/start', { mode: 'zombies', wallet: '0x1234' }, auth)).status === 403);
const start = await call('POST', '/v1/run/start', { mode: 'zombies', wallet: WALLET.toUpperCase().replace('0X', '0x') }, auth);
check('run start with a wallet', start.data.ok && start.data.token);
check('second start within 20 s refused', (await call('POST', '/v1/run/start', { mode: 'br', wallet: WALLET }, auth)).status === 429);
// a second device (new player) playing with the SAME wallet: its run must add up into the same board row
const reg2 = await call('POST', '/v1/register', { name: 'PHONE ' + tag });
const auth2 = reg2.data.ok ? `Bearer ${reg2.data.playerId}.${reg2.data.secret}` : null;
const start2 = auth2 ? await call('POST', '/v1/run/start', { mode: 'zombies', wallet: WALLET }, auth2) : null;
console.log('     waiting 11 s (minimum match time)...');
await new Promise((r) => setTimeout(r, 11000));
const res = { kills: 7, placement: 0, waves: 0, damage: 640, seconds: 10, matchSize: 1 };
check('bad signature refused', (await call('POST', '/v1/run/finish', { token: start.data.token, ...res, sig: 'x' }, auth)).status === 400);
const fin = await call('POST', '/v1/run/finish', { token: start.data.token, ...res, sig: sign(start.data.token, 'zombies', res) }, auth);
check('finish counted: ' + JSON.stringify(fin.data), fin.data.ok && fin.data.counted && fin.data.points === 7 && fin.data.weekRank >= 1);
check('same token twice refused', (await call('POST', '/v1/run/finish', { token: start.data.token, ...res, sig: sign(start.data.token, 'zombies', res) }, auth)).status === 409);

const board = await call('GET', '/v1/leaderboard?mode=zombies&period=week', null, auth);
const shortW = WALLET.slice(0, 6) + '…' + WALLET.slice(-4);
check('board has me, by wallet ' + shortW, board.data.ok && board.data.me && board.data.me.name === NAME && board.data.me.wallet === shortW && board.data.rows.some((r) => r.you));
check('public rows show only the short wallet', board.data.rows.every((r) => !r.wallet || r.wallet.length === 11));
if (start2 && start2.data.ok) {
  const res2 = { kills: 9, placement: 0, waves: 0, damage: 100, seconds: 10, matchSize: 1 };
  await call('POST', '/v1/run/finish', { token: start2.data.token, ...res2, sig: sign(start2.data.token, 'zombies', res2) }, auth2);
  const b2 = await call('GET', '/v1/leaderboard?mode=zombies&period=week&wallet=' + WALLET);
  const mine = b2.data.rows.filter((r) => r.wallet === shortW);
  check('two devices, one wallet = one row (best 9, 2 matches, latest name)', mine.length === 1 && b2.data.me.score === 9 && b2.data.me.matches === 2 && b2.data.me.name === 'PHONE ' + tag);
} else console.log('     (second device skipped: register rate limit on this network)');
const all = await call('GET', '/v1/leaderboard?mode=br&period=all');
check('all-time br board answers', all.data.ok && Array.isArray(all.data.rows));
const brw = await call('GET', '/v1/leaderboard?mode=br&period=week');
check('weekly br board answers', brw.data.ok && Array.isArray(brw.data.rows));
check('rename', (await call('POST', '/v1/name', { name: NAME2.toLowerCase() }, auth)).data.name === NAME2);
check('rename to own name ok', (await call('POST', '/v1/name', { name: NAME2 }, auth)).status === 200);
check('rename to GUEST CAT refused', (await call('POST', '/v1/name', { name: 'guest cat' }, auth)).status === 400);
const adm = await fetch(base + '/v1/admin/runs?mode=zombies', { headers: { 'X-Admin-Key': process.env.ADMIN_KEY || devVars.ADMIN_KEY || 'test-admin' } });
check('admin list', adm.ok && (await adm.json()).rows.length >= 1);
check('admin without key refused', (await fetch(base + '/v1/admin/runs')).status === 403);
const admB = await fetch(base + '/v1/admin/board?mode=zombies&format=csv', { headers: { 'X-Admin-Key': process.env.ADMIN_KEY || devVars.ADMIN_KEY || 'test-admin' } });
check('admin board csv has the FULL wallet', admB.ok && (await admB.text()).includes(WALLET));
check('admin board without key refused', (await fetch(base + '/v1/admin/board')).status === 403);

console.log(failed ? `${failed} FAILED` : 'all passed');
process.exit(failed ? 1 : 0);
