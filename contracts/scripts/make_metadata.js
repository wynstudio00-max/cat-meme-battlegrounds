// Writes the metadata JSON files that CMBGPassCard.tokenURI points at:
//   <baseURI>sealed/<tier>.json  and  <baseURI>character/<characterId>.json
// usage: node scripts/make_metadata.js <imagesCID | https base>      -> nft/metadata/
//   images are referenced as ipfs://<imagesCID>/sealed/<tier>.png and ipfs://<imagesCID>/character/<id>.png
// Upload order: 1) nft/images folder -> imagesCID, 2) run this, 3) nft/metadata folder -> metadataCID,
//               4) card.setBaseURI("ipfs://<metadataCID>/")  (trailing slash!)
const fs = require("fs");
const path = require("path");

const SITE = "https://catbattlegrounds.com";
const TIERS = { 1: "Common", 2: "Rare", 3: "Legendary" };
// characterId -> catalog id, name, tier, in-game weapon (CatBattleground Weapons.ForCharacter)
// Names = the names printed on the card art (user, 2026-11-05); the game (CatalogBuilder.DisplayNames) and the
// website roster use the same names - keep all three in sync.
const CHARACTERS = {
  1: { id: "gold", name: "Golden Cat", tier: 1, weapon: "Light Machine Gun" },
  2: { id: "soldier", name: "V.", tier: 1, weapon: "Heavy Rifle" },
  3: { id: "ninja", name: "Ninja Cat", tier: 2, weapon: "Sniper" },
  4: { id: "baby", name: "Baby Cash Cat", tier: 1, weapon: "Burst Pistol" },
  5: { id: "nobi", name: "Nobi", tier: 2, weapon: "Rapid SMG" },
  6: { id: "black", name: "Hmm Cat", tier: 2, weapon: "Silent Carbine" },
  7: { id: "white", name: "Cash Cat", tier: 3, weapon: "Golden Cash Sniper" },
  8: { id: "knight", name: "Bundle Cat", tier: 3, weapon: "Combat Shotgun" },
};

function imageBase(arg) {
  if (!arg) throw new Error("usage: node scripts/make_metadata.js <imagesCID | https://.../images/>");
  if (/^https?:\/\//.test(arg)) return arg.endsWith("/") ? arg : arg + "/";
  return `ipfs://${arg.replace(/^ipfs:\/\//, "").replace(/\/$/, "")}/`;
}

function main() {
  const img = imageBase(process.argv[2]);
  const out = path.join(__dirname, "..", "nft", "metadata");
  fs.mkdirSync(path.join(out, "sealed"), { recursive: true });
  fs.mkdirSync(path.join(out, "character"), { recursive: true });

  for (const [t, tier] of Object.entries(TIERS)) {
    const pool = Object.values(CHARACTERS).filter((c) => c.tier === Number(t)).map((c) => c.name);
    const meta = {
      name: `CMBG ${tier} Pass Card (Sealed)`,
      description:
        `A sealed ${tier} pass card for Cat Meme Battlegrounds. Open it on ${SITE} to reveal one ${tier} cat ` +
        `(${pool.join(", ")}). The cat is drawn once, stored on-chain and stays with the card. ` +
        `Play it in the game with the wallet that holds the card.`,
      image: `${img}sealed/${t}.png`,
      external_url: SITE,
      attributes: [
        { trait_type: "Tier", value: tier },
        { trait_type: "State", value: "Sealed" },
      ],
    };
    fs.writeFileSync(path.join(out, "sealed", `${t}.json`), JSON.stringify(meta, null, 2));
  }

  for (const [cid, c] of Object.entries(CHARACTERS)) {
    const tier = TIERS[c.tier];
    const meta = {
      name: `${c.name} - CMBG ${tier} Pass Card`,
      description:
        `${c.name}, a ${tier} cat of Cat Meme Battlegrounds, revealed from a ${tier} pass card. ` +
        `The wallet holding this card can play ${c.name} in the game (${c.weapon}). ${SITE}`,
      image: `${img}character/${cid}.png`,
      external_url: SITE,
      attributes: [
        { trait_type: "Tier", value: tier },
        { trait_type: "State", value: "Opened" },
        { trait_type: "Character", value: c.name },
        { trait_type: "Weapon", value: c.weapon },
      ],
    };
    fs.writeFileSync(path.join(out, "character", `${cid}.json`), JSON.stringify(meta, null, 2));
  }
  console.log("metadata written to", out, "(images from", img + ")");
}

if (require.main === module) main();
module.exports = { CHARACTERS, TIERS };
