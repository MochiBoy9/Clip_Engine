"""Draws Clip Engine's icon (the mark in the app's navigation rail: an amber plate with its top-left and
bottom-right corners cut off, and a black play triangle) and writes every copy the app and the website use.

    python3 brand/make-icons.py

Needs Pillow. The geometry matches MainWindow.axaml: a 40-unit plate with a cut of 9, holding IconPlay at 20 units
nudged 2 to the right."""

from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ACCENT = (0xFF, 0xC2, 0x1A, 255)
INK = (0x0B, 0x0C, 0x0E, 255)
BOX = 40.0
CUT = 9.0
# IconPlay is "M320-203v-560l440 280-440 280" in a 960 box, drawn at 20 units with its left edge at 11 and top at 10.
PLAY = [(11 + 20 * x / 960, 10 + 20 * (y + 960) / 960) for x, y in [(320, -203), (320, -763), (760, -483)]]
PLATE = [(CUT, 0), (BOX, 0), (BOX, BOX - CUT), (BOX - CUT, BOX), (0, BOX), (0, CUT)]


def svg() -> str:
    pts = lambda ps: " ".join(f"{x:g},{y:g}" for x, y in ((round(x, 3), round(y, 3)) for x, y in ps))
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40">\n'
        f'  <polygon fill="#FFC21A" points="{pts(PLATE)}"/>\n'
        f'  <polygon fill="#0B0C0E" points="{pts(PLAY)}"/>\n'
        "</svg>\n"
    )


def play(size: int):
    # At taskbar and browser-tab sizes the triangle is only a few pixels and blurs away, so it's drawn bigger there.
    grow = 1.5 if size <= 24 else 1.25 if size <= 48 else 1.0
    cx = sum(x for x, _ in PLAY) / 3
    cy = sum(y for _, y in PLAY) / 3
    return [(cx + (x - cx) * grow, cy + (y - cy) * grow) for x, y in PLAY]


def draw(size: int, background=None) -> Image.Image:
    # Drawn 16 times over and scaled down, so the diagonal edges stay smooth at every size.
    big = size * 16
    s = big / BOX
    im = Image.new("RGBA", (big, big), background or (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.polygon([(x * s, y * s) for x, y in PLATE], fill=ACCENT)
    d.polygon([(x * s, y * s) for x, y in play(size)], fill=INK)
    return im.resize((size, size), Image.LANCZOS)


def ico(path: Path, sizes):
    images = [draw(n) for n in sizes]
    images[-1].save(path, format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])


def main():
    (ROOT / "brand/clip-engine-icon.svg").write_text(svg())
    ico(ROOT / "ui/ClipEngine.Desktop/Assets/ClipEngine.ico", [16, 20, 24, 32, 40, 48, 64, 128, 256])
    ico(ROOT / "website/favicon.ico", [16, 24, 32, 48, 64])
    draw(512).save(ROOT / "website/assets/icon.png", optimize=True)
    # iOS fills transparency with black and rounds the corners itself, so give it the page's background.
    draw(180, (0x0B, 0x0C, 0x0E, 255)).save(ROOT / "website/assets/apple-touch-icon.png", optimize=True)


if __name__ == "__main__":
    main()
