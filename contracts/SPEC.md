# CMBG Pass Card — spesifikasi kontrak (draf v0.1, 2026-11-05)

Status: DRAF untuk testnet Robinhood Chain (46630). Belum diaudit. Jangan deploy ke mainnet (4663) sebelum audit.

## Keputusan (user, 2026-11-05: "gunakan seluruh saran kamu")
- Standar **ERC-721** (+ Enumerable), satu token = satu kartu.
- Hasil buka kartu **disimpan di kontrak**: `characterOf(tokenId)`; metadata berganti dari kartu tersegel ke karakternya.
- Kelayakan dari **snapshot saldo $CMBG** -> Merkle root. Kontrak tidak membaca saldo token.
- Ambang (snapshot): **Common 150.000, Rare 500.000, Legendary 1.000.000 CMBG** (user, 2026-11-07; sebelumnya
  150k / 250k / 500k), kumulatif, **maks 2 kartu per wallet**.
- Royalti + marketplace: **belum diputuskan** (user mempelajari dulu). Kontrak sudah mendukung ERC-2981, default TANPA royalti.

## Tier dan karakter
| tier (uint8) | nama | pool karakter (characterId) |
|---|---|---|
| 1 | Common | 1 Golden Cat (gold), 2 V. (soldier), 4 Baby Cash Cat (baby) |
| 2 | Rare | 3 Ninja Cat (ninja), 5 Nobi (nobi), 6 Hmm Cat (black) |
| 3 | Legendary | 7 Cash Cat (white), 8 Bundle Cat (knight) |

Nama = nama yang tercetak di kartu, sama di game, website dan metadata NFT (user, 2026-11-05).

Nomor tier sama dengan enum di Unity (`CharacterCatalog`: 0 Original, 1 Common, 2 Rare, 3 Legendary). Army Cat (Original) gratis,
tidak ada di kontrak. characterId 0 = belum dibuka. Pool ditetapkan di constructor dan **tidak bisa diubah** (peluang tetap:
Common/Rare 1/3 per karakter, Legendary 1/2).

## Aturan alokasi snapshot (off-chain, `scripts/snapshot.js`)
Saldo dihitung per wallet pada blok snapshot. Kartu dipilih dari tier tertinggi yang terjangkau, saldo dikurangi ambangnya, ulang
sampai 2 kartu atau saldo sisa < 150k:

| saldo | kartu |
|---|---|
| < 150k | tidak ada |
| 150k - 299.999 | Common |
| 300k - 499.999 | Common + Common |
| 500k - 649.999 | Rare |
| 650k - 999.999 | Rare + Common |
| 1.000.000 - 1.149.999 | Legendary |
| 1.150.000 - 1.499.999 | Legendary + Common |
| 1.500.000 - 1.999.999 | Legendary + Rare |
| >= 2.000.000 | Legendary + Legendary |

Daftar alamat yang dikecualikan (pool likuiditas, kontrak token, wallet tim/treasury, burn) wajib diisi sebelum snapshot final.
Leaf Merkle = `(address, commons, rares, legendaries)` dengan format `@openzeppelin/merkle-tree` StandardMerkleTree
(`["address","uint8","uint8","uint8"]`, hash ganda), jadi aturan ini bisa diubah tanpa mengubah kontrak (cukup root baru).

## Alur
1. **Klaim** `claim(commons, rares, legendaries, proof)`: hanya wallet itu sendiri, sekali per wallet, total 1..2, sebelum
   `claimDeadline` (0 = tanpa batas). Mint kartu tersegel, id berurutan mulai 1. Pembeli harus membayar gas sendiri (ETH di
   Robinhood Chain); tidak ada harga mint.
2. **Buka** `open(tokenId)`: pemilik kartu, status Sealed -> Opening, minta angka acak ke `randomSource`.
3. **Hasil** `fulfillRandomness(tokenId, word)`: hanya dari `randomSource` aktif. characterId = pool[tier][word % n],
   status Opened, event `CardOpened` + `MetadataUpdate` (ERC-4906, supaya market memperbarui gambar).
4. Kartu (tersegel atau sudah dibuka) bebas ditransfer/dijual; karakter ikut kartunya.
5. **Game / website** membaca `cardsOf(wallet)` (id, tier, characterId, status). Server game wajib memverifikasi kepemilikan
   on-chain sendiri (jangan percaya data dari klien).

## Sumber acak (randomSource, bisa ditukar)
Robinhood Chain: `block.prevrandao`/`difficulty` konstan, `blockhash` tidak boleh dipakai (docs Robinhood, "Differences from
Ethereum"). Tidak ditemukan VRF yang terpasang di chain ini per 2026-11-05. Karena itu:
- Kontrak kartu hanya mengenal antarmuka `IRandomSource.request(tokenId)` dan callback `fulfillRandomness`.
- Sekarang: `HashChainRandomSource` — operator membuat rantai hash rahasia `h0 = seed, h(i) = keccak256(h(i-1))` dan
  mempublikasikan ujungnya (`anchor = hN`) di kontrak SEBELUM kartu bisa dibuka. Setiap permintaan masuk antrean berurutan;
  operator membuka rantai satu langkah per permintaan (`reveal(h(N-1))`, lalu `h(N-2)`, ...), word = keccak(preimage, tokenId).
  - Operator **tidak bisa memilih** hasil (preimage sudah terkunci oleh anchor, urutan antrean tetap).
  - Kelemahan yang diketahui: operator **bisa tahu lebih dulu** hasil permintaan berikutnya, dan bisa menunda. Mitigasi:
    wallet tim tidak memegang kartu, dicatat terbuka di website; ganti ke VRF kalau tersedia.
  - Anchor baru hanya bisa dipasang saat antrean kosong.
- Nanti: adapter Chainlink VRF (atau penyedia lain) cukup memanggil `fulfillRandomness`; owner mengganti `randomSource`.
  Kartu yang sedang Opening di sumber lama bisa diminta ulang oleh pemiliknya (`reRequest`).

## Metadata
- `tokenURI`: belum dibuka -> `<baseURI>sealed/<tier>.json`, sudah dibuka -> `<baseURI>character/<characterId>.json`.
- File JSON + gambar (kartu tersegel 3 tier + 8 kartu karakter dari `card_pack/`) di IPFS; `freezeMetadata()` mengunci baseURI
  setelah final.

## Admin (owner)
- Owner = **multisig** (Safe, kalau tersedia di Robinhood Chain — perlu dicek), transfer owner 2 langkah (`Ownable2Step`).
- Bisa: setMerkleRoot, setClaimDeadline, pause/unpause (klaim + buka), setRandomSource, setBaseURI (sebelum freeze),
  royalti ERC-2981 (set / hapus). Tidak bisa: mint di luar Merkle, mengubah pool / tier / karakter yang sudah keluar.

## Belum diputuskan / perlu dicek
- Royalti (persen, penerima) dan marketplace di Robinhood Chain.
- Blok/tanggal snapshot, daftar alamat yang dikecualikan, batas waktu klaim.
- Ketersediaan Safe multisig dan VRF di Robinhood Chain. (Versi EVM sudah dicek 2026-11-05 lewat eth_call ke RPC testnet +
  mainnet: PUSH0 / MCOPY / TSTORE jalan -> kompilasi `cancun`.)
- Audit eksternal sebelum mainnet.
