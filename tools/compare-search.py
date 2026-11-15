"""Build a before/after comparison strip for the search box text inset.

Top    : the old build, where the text started 70.4 DIP from the left border.
Bottom : the fixed build, where it starts at 36 DIP (34 padding + 1 border + bearing).
A magenta guide marks the measured text edge in both halves.
"""
import sys
from PIL import Image, ImageDraw, ImageFont

before, after, out = sys.argv[1], sys.argv[2], sys.argv[3]
ZOOM = 3
H = 130          # source rows per half
Y0 = 55

# measured absolute x of the first glyph pixel and of the box's left border
CASES = [
    (before, "BEFORE", 108, 20, 10),
    (after,  "AFTER",   74, 29, 19),
]

FONT = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 26)
FONT_S = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 20)

halves = []
for path, label, text_x, box_x, crop_x in CASES:
    im = Image.open(path).convert("RGB")
    box = im.crop((crop_x, Y0, crop_x + 360, Y0 + H))
    box = box.resize((360 * ZOOM, H * ZOOM), Image.NEAREST)
    d = ImageDraw.Draw(box)

    gx = (text_x - crop_x) * ZOOM
    bx = (box_x - crop_x) * ZOOM
    d.line([(gx, 6), (gx, H * ZOOM - 6)], fill=(255, 0, 140), width=2)
    d.line([(bx, 6), (bx, H * ZOOM - 6)], fill=(0, 160, 255), width=2)

    dip = (text_x - box_x) / 1.25
    d.rectangle([0, 0, 340, 62], fill=(255, 255, 255))
    d.text((8, 4), label, font=FONT, fill=(20, 24, 32))
    d.text((8, 34), "text inset = %.1f DIP" % dip, font=FONT_S, fill=(120, 20, 90))
    halves.append(box)

w = halves[0].width
total = Image.new("RGB", (w, halves[0].height * 2 + 8), (230, 234, 240))
total.paste(halves[0], (0, 0))
total.paste(halves[1], (0, halves[0].height + 8))
total.save(out)
print("saved", out, total.size)
