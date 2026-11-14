"""Derive the Android launcher mipmaps from Resources/Raw/wwwroot/logo.png."""
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Resources" / "Raw" / "wwwroot" / "logo.png"
RES = ROOT / "Platforms" / "Android" / "Resources"

LEGACY = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}
FOREGROUND = {"mdpi": 108, "hdpi": 162, "xhdpi": 216, "xxhdpi": 324, "xxxhdpi": 432}

src = Image.open(SRC).convert("RGBA")
r, g, b, a = src.split()
white = ImageChops.multiply(
    ImageChops.multiply(r.point(lambda v: 255 if v > 200 else 0),
                        g.point(lambda v: 255 if v > 200 else 0)),
    b.point(lambda v: 255 if v > 200 else 0))
mark = Image.merge("RGBA", (r, g, b, ImageChops.multiply(a, white)))
mark = mark.crop(mark.getbbox())
bg = src.getpixel((src.width // 2, 8))[:3]


def centered_mark(canvas_size, fraction):
    side = int(canvas_size * fraction)
    scaled = mark.copy()
    scaled.thumbnail((side, side), Image.LANCZOS)
    return scaled


for density, n in LEGACY.items():
    out = RES / f"mipmap-{density}"
    out.mkdir(parents=True, exist_ok=True)
    src.resize((n, n), Image.LANCZOS).save(out / "ic_launcher.png")

    round_icon = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(round_icon).ellipse([0, 0, n - 1, n - 1], fill=bg + (255,))
    m = centered_mark(n, 0.62)
    round_icon.paste(m, ((n - m.width) // 2, (n - m.height) // 2), m)
    round_icon.save(out / "ic_launcher_round.png")

for density, n in FOREGROUND.items():
    out = RES / f"mipmap-{density}"
    out.mkdir(parents=True, exist_ok=True)
    fg = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    m = centered_mark(n, 0.60)
    fg.paste(m, ((n - m.width) // 2, (n - m.height) // 2), m)
    fg.save(out / "ic_launcher_foreground.png")

anydpi = RES / "mipmap-anydpi-v26"
anydpi.mkdir(parents=True, exist_ok=True)
adaptive = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
    '    <background android:drawable="@color/ic_launcher_background" />\n'
    '    <foreground android:drawable="@mipmap/ic_launcher_foreground" />\n'
    '</adaptive-icon>\n'
)
(anydpi / "ic_launcher.xml").write_text(adaptive, encoding="utf-8")
(anydpi / "ic_launcher_round.xml").write_text(adaptive, encoding="utf-8")

values = RES / "values"
values.mkdir(parents=True, exist_ok=True)
(values / "colors.xml").write_text(
    '<?xml version="1.0" encoding="utf-8"?>\n'
    '<resources>\n'
    f'    <color name="ic_launcher_background">#{bg[0]:02X}{bg[1]:02X}{bg[2]:02X}</color>\n'
    '</resources>\n',
    encoding="utf-8")

print("background", bg)
print("wrote", sum(1 for _ in RES.rglob("*.png")), "pngs + adaptive xml + colors.xml")