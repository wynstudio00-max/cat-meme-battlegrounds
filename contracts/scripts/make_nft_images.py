# Builds the NFT images (square PNG) for IPFS.
#   sealed/<tier>.png      <- Website/card/{comnmon,rare,legendary}.png (the user's sealed pack renders, 1920 px)
#   character/<id>.png     <- nft_src/renders/<id>_<name>.png (render_nft_cards.py, transparent card) on a tier-coloured
#                             radial gradient sampled from the matching pack image, with a soft drop shadow.
# usage: python Contracts/scripts/make_nft_images.py   (from the repo root)   -> Contracts/nft/images/
import os
from PIL import Image, ImageFilter, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(ROOT, "Contracts", "nft_src", "renders")
OUT = os.path.join(ROOT, "Contracts", "nft", "images")
SIZE = 1500
PACKS = {1: "comnmon.png", 2: "rare.png", 3: "legendary.png"}
CHARS = {1: ("gold", 1), 2: ("soldier", 1), 3: ("ninja", 2), 4: ("baby", 1), 5: ("nobi", 2), 6: ("black", 2),
         7: ("white", 3), 8: ("knight", 3)}


def pack(tier):
    return Image.open(os.path.join(ROOT, "Website", "card", PACKS[tier])).convert("RGB")


def gradient(tier):
    """Radial gradient with the pack image's own colours: centre glow -> corner."""
    im = pack(tier)
    w, h = im.size
    edge = im.crop((0, 0, int(w * 0.06), int(h * 0.06))).resize((1, 1), Image.BOX).getpixel((0, 0))
    glow = im.crop((int(w * 0.12), int(h * 0.42), int(w * 0.2), int(h * 0.58))).resize((1, 1), Image.BOX).getpixel((0, 0))
    g = Image.new("RGB", (SIZE, SIZE))
    px = g.load()
    c = SIZE / 2
    rmax = (2 * c * c) ** 0.5
    for y in range(SIZE):
        for x in range(SIZE):
            t = min(1.0, (((x - c) ** 2 + (y - c) ** 2) ** 0.5) / rmax) ** 1.3
            px[x, y] = tuple(int(glow[i] + (edge[i] - glow[i]) * t) for i in range(3))
    return g


def main():
    os.makedirs(os.path.join(OUT, "sealed"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "character"), exist_ok=True)
    for tier in PACKS:
        pack(tier).resize((SIZE, SIZE), Image.LANCZOS).save(os.path.join(OUT, "sealed", f"{tier}.png"), optimize=True)
    bgs = {t: gradient(t) for t in PACKS}
    for cid, (name, tier) in CHARS.items():
        card = Image.open(os.path.join(SRC, f"{cid}_{name}.png")).convert("RGBA")
        bbox = card.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
        card = card.crop(bbox)
        scale = SIZE * 0.82 / card.height
        card = card.resize((int(card.width * scale), int(card.height * scale)), Image.LANCZOS)
        x, y = (SIZE - card.width) // 2, (SIZE - card.height) // 2
        canvas = bgs[tier].convert("RGBA")
        shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
        mask = card.getchannel("A").point(lambda a: int(a * 0.55))
        shadow.paste((0, 0, 0, 255), (x + 14, y + 26), mask)
        shadow = shadow.filter(ImageFilter.GaussianBlur(28))
        canvas.alpha_composite(shadow)
        canvas.alpha_composite(card, (x, y))
        canvas.convert("RGB").save(os.path.join(OUT, "character", f"{cid}.png"), optimize=True)
        print("character", cid, name)
    print("done ->", OUT)


if __name__ == "__main__":
    main()
