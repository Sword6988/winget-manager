"""Measure where the typed text starts inside the search box.

Scans a horizontal band across the search box, then reports:
  - the box's left/right border x (first non-background pixel of the rounded frame)
  - the magnifier icon's x range
  - the first dark glyph pixel (the text's left edge)
Everything is in PNG pixels; the caller converts to DIP.
"""
import sys
from PIL import Image

path = sys.argv[1]
y0, y1 = int(sys.argv[2]), int(sys.argv[3])   # vertical band to scan (inclusive-ish)
xmax = int(sys.argv[4]) if len(sys.argv) > 4 else 520

im = Image.open(path).convert("RGB")
W, H = im.size
px = im.load()
print(f"image      : {W}x{H}   band y={y0}..{y1}  x<{xmax}")


def lum(p):
    return 0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]


# Column profile inside the band: darkest luminance found in that column,
# plus how many pixels differ from pure white.
cols = []
for x in range(0, min(xmax, W)):
    darkest = 255.0
    nonwhite = 0
    for y in range(y0, min(y1 + 1, H)):
        p = px[x, y]
        l = lum(p)
        if l < darkest:
            darkest = l
        if l < 250:
            nonwhite += 1
    cols.append((x, darkest, nonwhite))

# Group runs of columns that contain something non-white, to see the structure.
runs = []
cur = None
for x, darkest, nonwhite in cols:
    if nonwhite > 0:
        if cur is None:
            cur = [x, x, darkest]
        else:
            cur[1] = x
            cur[2] = min(cur[2], darkest)
    else:
        if cur is not None:
            runs.append(tuple(cur))
            cur = None
if cur is not None:
    runs.append(tuple(cur))

print("runs (x0..x1, width, darkest):")
for x0, x1, d in runs:
    tag = ""
    if d < 120:
        tag = "  <- glyph/dark"
    elif d < 200:
        tag = "  <- mid"
    print(f"  {x0:4d}..{x1:<4d} w={x1 - x0 + 1:<4d} darkest={d:5.1f}{tag}")

# Split text from chrome: the frame border and icon are grey (>150),
# glyphs here are near-black.
glyph = [r for r in runs if r[2] < 120]
if glyph:
    gx0 = glyph[0][0]
    print(f"\ntext left edge : x={gx0}")
    frame = [r for r in runs if r[0] < gx0]
    if frame:
        print(f"box left border: x={frame[0][0]}  (first run)")
        if len(frame) > 1:
            print(f"icon run       : x={frame[1][0]}..{frame[1][1]}")
    print(f"gap border->text: {gx0 - frame[0][0] if frame else 'n/a'} px")
    if len(frame) > 1:
        print(f"gap icon->text  : {gx0 - frame[1][1]} px")
else:
    print("\nno dark glyph found in the band")
