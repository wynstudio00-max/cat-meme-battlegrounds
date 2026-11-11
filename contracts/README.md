# CMBG Pass Card contracts (draft, NOT audited, testnet only)

Spec: [SPEC.md](SPEC.md).

- `contracts/CMBGPassCard.sol` - ERC-721 sealed tier cards, Merkle claim, on-chain open -> character, ERC-2981 (off), ERC-4906.
- `contracts/HashChainRandomSource.sol` - stand-in randomness (no VRF found on Robinhood Chain yet), swappable.
- `scripts/allocation.js` - snapshot rule (150k / 500k / 1M, cumulative, max 2).
- `scripts/snapshot.js` - balances CSV (+ exclude list) -> Merkle root, claims.json with proofs for the website.
- `scripts/hashchain.js` - operator seed / anchor / next preimage. The seed file is SECRET: keep it outside the repo.

```
npm install
npx hardhat test
node scripts/snapshot.js snapshot/example_balances.csv snapshot/example_exclude.txt
```

`npx hardhat run scripts/localflow.js` = deploy + claim + open + reveal on the local in-process network.

## Deploy ke testnet Robinhood (46630) - langkah untuk user (PowerShell, di folder Contracts)

1. Buat **wallet baru khusus testnet** (mis. akun baru di MetaMask). Jangan pakai wallet yang memegang CMBG / dana asli.
   Tambahkan jaringan: RPC `https://rpc.testnet.chain.robinhood.com`, chain id `46630`, explorer
   `https://explorer.testnet.chain.robinhood.com`. Isi ETH testnet dari faucet Robinhood Chain (lihat docs.robinhood.com/chain).
2. Buat rantai acak. File seed RAHASIA, simpan **di luar folder proyek**:
   ```
   node scripts/hashchain.js new C:\safe-offline-folder\cmbg_testnet.seed.json
   ```
   Salin nilai `anchor` yang dicetak.
3. (Opsional) Merkle root testnet: isi CSV dengan wallet testnet-mu sendiri, lalu
   `node scripts/snapshot.js <balances.csv> <exclude.txt>` -> `snapshot/out/root.txt` dipakai otomatis.
4. Deploy (private key hanya di variabel sesi ini, ketik sendiri, jangan dikirim ke chat):
   ```
   $env:RH_TESTNET_KEY = "<private key wallet testnet>"
   $env:ANCHOR = "<anchor dari langkah 2>"
   $env:BASE_URI = "ipfs://TODO/"
   npx hardhat run scripts/deploy.js --network robinhoodTestnet
   ```
   Alamat kontrak tersimpan di `deployments/robinhoodTestnet.json`. Mainnet (4663) hanya lewat langkah di `MAINNET.md`
   (deploy.js menolak tanpa konfirmasi audit, OWNER multisig, OPERATOR, ROOT, BASE_URI dan seed baru).
5. Verifikasi di explorer: jalankan dua perintah `npx hardhat verify ...` yang dicetak oleh deploy.
6. Coba: `claim` dan `open` dari wallet yang ada di snapshot (lewat explorer "Write contract"), lalu operator:
   ```
   $env:SEED_FILE = "C:\safe-offline-folder\cmbg_testnet.seed.json"
   npx hardhat run scripts/reveal.js --network robinhoodTestnet
   ```
7. Tutup PowerShell (hapus `$env:RH_TESTNET_KEY` dari sesi).

## Metadata + gambar (IPFS)

Gambar: `nft/images/sealed/<tier>.png` (pack Common / Rare / Legendary dari `Website/card/`) dan
`nft/images/character/<1..8>.png` (render depan kartu holo dari `card_pack/cards/card_<id>.blend` oleh
`Tools/BlenderAnim/render_nft_cards.py`, di atas latar warna tier). Buat ulang: render ke `nft_src/renders/`, lalu
`python scripts/make_nft_images.py`.

1. Buat akun IPFS pinning sendiri (mis. Pinata, paket gratis cukup). Upload **folder** `nft/images` (sebagai folder,
   bukan file satu-satu). Salin CID folder itu.
2. `node scripts/make_metadata.js <CID gambar>` -> `nft/metadata/sealed/*.json` + `character/*.json`.
3. Upload **folder** `nft/metadata`. Salin CID-nya.
4. Pasang di kontrak (owner):
   ```
   $env:BASE_URI = "ipfs://<CID metadata>/"
   npx hardhat run scripts/set_base_uri.js --network robinhoodTestnet
   ```
   Garis miring di akhir wajib. Cek: `scripts/status.js` menampilkan tokenURI baru.
5. `freezeMetadata()` baru di mainnet setelah semuanya final (tidak bisa dibatalkan).

Mainnet only after an external audit, owner = multisig (`$env:OWNER`, then `acceptOwnership()` on both contracts).
