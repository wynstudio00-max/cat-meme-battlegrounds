# Cat Battleground leaderboard (Cloudflare Worker + D1)

API for the offline beta's leaderboard. Code: `src/index.js`, database: `schema.sql`, tests: `test.mjs`.
Game side: `CatBattleground/Assets/Scripts/Online/LeaderboardApi.cs` (+ `LeaderboardReporter.cs`, `UI/LeaderboardPanel.cs`).

## Test on this PC
```
cd Backend/leaderboard
npm install
npm run db:local          # once: creates the tables in the local database
npm run dev               # http://127.0.0.1:8787  (the Unity editor uses this address)
node test.mjs             # in a second terminal: rules + full API run-through
```
`.dev.vars` holds the local secrets (SUBMIT_KEY = the game's key, ADMIN_KEY, ACCEPT_DEV=1 so editor runs count locally).
Restart `npm run dev` after editing `.dev.vars`.

## Put it online - easy way
Double-click `deploy.bat` (or `node deploy.mjs`). It logs in (browser, once), creates the database + tables, sets the secrets
(new ADMIN_KEY saved to `admin_key.txt`), deploys with `api.catbattlegrounds.com` as custom domain (falls back to the
workers.dev address), checks `/v1/health` and writes the address into `LeaderboardApi.DefaultUrl`. Run it again after code changes.

## Put it online by hand
```
npx wrangler login                                   # opens the browser, log in to Cloudflare
npx wrangler d1 create cmbg-leaderboard              # copy the database_id into wrangler.toml
npm run db:remote                                    # tables in the real database
npx wrangler secret put SUBMIT_KEY                   # paste the SubmitKey from LeaderboardApi.cs
npx wrangler secret put ADMIN_KEY                    # make up a long private password
npx wrangler secret put IP_SALT                      # any random text
npx wrangler deploy                                  # prints https://cmbg-leaderboard.<account>.workers.dev
```
Then either put that address in `LeaderboardApi.DefaultUrl`, or add the custom domain `api.catbattlegrounds.com`
(Cloudflare dashboard > Workers > cmbg-leaderboard > Settings > Domains & Routes) which `DefaultUrl` already uses.
A build can also be pointed elsewhere with `?api=https://...` in the page address.

## Before paying prizes
```
curl -H "X-Admin-Key: <ADMIN_KEY>" "https://api.catbattlegrounds.com/v1/admin/runs?mode=br"            # top runs this week
curl -H "X-Admin-Key: <ADMIN_KEY>" "https://api.catbattlegrounds.com/v1/admin/runs?mode=br&flagged=1"  # runs kept off the board
curl -X POST -H "X-Admin-Key: <ADMIN_KEY>" -H "Content-Type: application/json" -d "{\"playerId\":\"...\"}" https://api.catbattlegrounds.com/v1/admin/ban
```
The game runs in the browser, so a determined cheater can still fake results that pass the checks. Check the winners by hand
(time per match, kills, how fast their score rose, many accounts from one IP hash) before paying, and publish contest rules.
