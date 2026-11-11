-- 2026-11-08: leaderboard for connected wallets only. Run ONCE on the live database, BEFORE deploying the new Worker:
--   npx wrangler d1 execute cmbg-leaderboard --remote --file=migrations/2026-11-08_wallet.sql
-- (running it twice fails with "duplicate column name: wallet" - that is harmless, it means it is already done)
ALTER TABLE run_tokens ADD COLUMN wallet TEXT;
ALTER TABLE runs ADD COLUMN wallet TEXT;
CREATE INDEX IF NOT EXISTS runs_wallet ON runs (mode, week, valid, wallet, points);
