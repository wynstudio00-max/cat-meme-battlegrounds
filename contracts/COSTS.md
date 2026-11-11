# Perkiraan biaya gas (2026-11-05)

Sumber: gas diukur dengan `scripts/gas_report.js` (Hardhat, snapshot simulasi 1.000 wallet: 70% 1 kartu, 30% 2 kartu =
1.300 kartu, proof Merkle 10 hash) + receipt testnet nyata (klaim 2 kartu 338.366 gas, open 106.646 gas).
Harga gas mainnet Robinhood (4663) saat diukur: **0,0214 gwei**; komponen data L1 = **0** (NodeInterface 0xC8 dan
`gasUsedForL1` di receipt testnet) - `scripts/fee_probe.js`. Skenario ramai: 0,1 gwei. Harga ETH: $2.500 / $4.000 (asumsi).

| Transaksi | Gas | ETH @0,0214 gwei | USD ($2.5k / $4k) | ETH @0,1 gwei |
|---|---:|---:|---:|---:|
| Setup tim (deploy 2 kontrak + konfigurasi + serah owner) | 3.908.369 | 0,000084 | $0,21 / $0,34 | 0,00039 |
| setMerkleRoot (snapshot 1.000 wallet) | 47.229 | 0,000001 | < $0,01 | 0,000005 |
| Klaim 1 kartu (dibayar user) | 210.929 | 0,0000045 | $0,011 / $0,018 | 0,000021 |
| Klaim 2 kartu (dibayar user) | 348.682 | 0,0000075 | $0,019 / $0,030 | 0,000035 |
| Buka 1 kartu (dibayar user) | 106.646 | 0,0000023 | $0,006 / $0,009 | 0,000011 |
| Reveal 1.300 kartu, terburuk (operator, tim) | 100.323.600 | 0,0022 | $5,37 / $8,59 | 0,010 |
| Reveal 1.300 kartu, ter-batch (operator, tim) | 23.094.500 | 0,0005 | $1,24 / $1,98 | 0,0023 |
| Total semua klaim 1.000 wallet (users) | 252.254.900 | 0,0054 | $13,50 / $21,59 | 0,025 |
| Total semua buka 1.300 kartu (users) | 138.639.800 | 0,0030 | $7,42 / $11,87 | 0,014 |

Snapshot sendiri gratis (off-chain: baca saldo lewat RPC / explorer, hitung Merkle di PC). Biaya tim = setup + reveal
(+ airdrop kalau tim yang membayar mint: kira-kira sama dengan "total semua klaim", perlu fungsi batch-mint baru).
Biaya terbesar sebenarnya bukan gas: audit kontrak.
