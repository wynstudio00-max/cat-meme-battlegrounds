-- Cat Battleground leaderboard (Cloudflare D1 / SQLite), 2026-10-28.
-- Apply:  npx wrangler d1 execute cmbg-leaderboard --remote --file=schema.sql   (use --local for wrangler dev)

CREATE TABLE IF NOT EXISTS players (
  id           TEXT PRIMARY KEY,              -- random uuid, kept on the device (PlayerPrefs)
  secret_hash  TEXT NOT NULL,                 -- sha256 of the device secret (the secret itself is never stored)
  name         TEXT NOT NULL,
  created_at   INTEGER NOT NULL,              -- unix ms
  last_seen    INTEGER NOT NULL,
  banned       INTEGER NOT NULL DEFAULT 0,
  wallet       TEXT                           -- later: bound wallet address (prize claims)
);

-- One token per started match: a result is only accepted with a token the server handed out, once, in a believable time.
CREATE TABLE IF NOT EXISTS run_tokens (
  token      TEXT PRIMARY KEY,
  player_id  TEXT NOT NULL,
  mode       TEXT NOT NULL,                   -- 'br' | 'zombies'
  issued_at  INTEGER NOT NULL,
  used       INTEGER NOT NULL DEFAULT 0,
  wallet     TEXT                             -- 2026-11-08: wallet connected when the match started (lower case)
);
CREATE INDEX IF NOT EXISTS players_name ON players (name);   -- unique-name check (2026-10-31)
-- look-alike key (2026-11-02): same expression as KEY_SQL in src/index.js
CREATE INDEX IF NOT EXISTS players_name_key ON players (REPLACE(REPLACE(REPLACE(REPLACE(name, ' ', ''), '_', ''), '0', 'O'), '1', 'I'));
CREATE INDEX IF NOT EXISTS run_tokens_player ON run_tokens (player_id, issued_at);

CREATE TABLE IF NOT EXISTS runs (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  player_id    TEXT NOT NULL,
  mode         TEXT NOT NULL,                 -- 'br' | 'zombies'
  week         INTEGER NOT NULL,              -- weeks since Monday 1970-01-05 00:00 UTC
  points       INTEGER NOT NULL,              -- br: match points; zombies: wave * 1000 + kills
  kills        INTEGER NOT NULL,
  placement    INTEGER NOT NULL,              -- br 1..10, zombies 0
  waves        INTEGER NOT NULL,              -- zombies: waves cleared
  damage       INTEGER NOT NULL,
  seconds      INTEGER NOT NULL,              -- time in the match reported by the game
  elapsed      INTEGER NOT NULL,              -- server time between the token and the result
  character    TEXT,
  build        TEXT,
  valid        INTEGER NOT NULL DEFAULT 1,    -- 0 = kept for review but not on the boards
  flag         TEXT,                          -- why it is not valid / looks odd
  ip_hash      TEXT,
  created_at   INTEGER NOT NULL,
  wallet       TEXT                           -- 2026-11-08: boards are per wallet; runs without one never count
);
CREATE INDEX IF NOT EXISTS runs_board ON runs (mode, week, valid, player_id, points);
CREATE INDEX IF NOT EXISTS runs_wallet ON runs (mode, week, valid, wallet, points);
CREATE INDEX IF NOT EXISTS runs_player ON runs (player_id, created_at);

-- register rate limit per IP (hashed)
CREATE TABLE IF NOT EXISTS register_log (
  ip_hash  TEXT NOT NULL,
  at       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS register_log_ip ON register_log (ip_hash, at);
