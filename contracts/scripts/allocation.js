// Snapshot allocation rule (SPEC.md): highest tier first, subtract its threshold, at most 2 cards per wallet.
// 2026-11-07 (user): Common 150k / Rare 500k / Legendary 1M (was 150k / 250k / 500k: nearly every holder got 2 Legendaries).
const THRESHOLDS = [
  { tier: "legendaries", min: 1_000_000n },
  { tier: "rares", min: 500_000n },
  { tier: "commons", min: 150_000n },
];
const MAX_PER_WALLET = 2;

/** balance in whole CMBG (bigint, decimals already removed) -> { commons, rares, legendaries } */
function allocate(balance) {
  const out = { commons: 0, rares: 0, legendaries: 0 };
  let rest = balance;
  for (let n = 0; n < MAX_PER_WALLET; n++) {
    const t = THRESHOLDS.find((x) => rest >= x.min);
    if (!t) break;
    out[t.tier]++;
    rest -= t.min;
  }
  return out;
}

module.exports = { allocate, THRESHOLDS, MAX_PER_WALLET };
