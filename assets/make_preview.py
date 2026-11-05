"""把四个候选图标拼成「浅色 / 深色背景 × 多尺寸」对比预览图。"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parent / "icons"
PLANS = {
    "a": ("方案 A · 流光 W", "渐变圆角底 + 几何 W（品牌延续性最好）"),
    "b": ("方案 B · 安装箭头", "下箭头 + 托盘（安装 / 管理语义最直白）"),
    "c": ("方案 C · 负空间 W", "W 由底板挖空形成（镂空随背景变色，现代感）"),
    "d": ("方案 D · 无底流光 W", "W 自身渐变、无背板（最轻盈，深色任务栏利落）"),
}
LIGHT, DARK = (247, 248, 252, 255), (26, 29, 38, 255)
TEXT_L, TEXT_D = (24, 28, 40, 255), (232, 234, 243, 255)
MUTED_L, MUTED_D = (106, 113, 131, 255), (154, 161, 181, 255)

FONT = "C:/Windows/Fonts/msyh.ttc"
f_title = ImageFont.truetype(FONT, 30)
f_desc = ImageFont.truetype(FONT, 17)
f_small = ImageFont.truetype(FONT, 15)


def strip(im: Image.Image, size: int, bg) -> Image.Image:
    """把图标放在 size×size 的纯色块中央。"""
    canvas = Image.new("RGBA", (size, size), bg)
    icon = im.resize((size, size), Image.LANCZOS)
    canvas.paste(icon, (0, 0), icon)
    return canvas


def zoom16(im: Image.Image, bg, factor: int = 8) -> Image.Image:
    """16px 原始渲染图做最近邻放大，检验小尺寸真实可读性。"""
    icon = im.resize((16, 16), Image.LANCZOS)
    canvas = Image.new("RGBA", (16 * factor, 16 * factor), bg)
    big = icon.resize((16 * factor, 16 * factor), Image.NEAREST)
    canvas.paste(big, (0, 0), big)
    return canvas


def main() -> None:
    row_h, pad = 232, 28
    W = 1430
    H = pad * 2 + row_h * len(PLANS)
    img = Image.new("RGBA", (W, H), (255, 255, 255, 255))
    d = ImageDraw.Draw(img)

    # 左浅右深两大背景区
    d.rectangle([0, 0, W // 2, H], fill=LIGHT)
    d.rectangle([W // 2, 0, W, H], fill=DARK)

    for i, (key, (title, desc)) in enumerate(PLANS.items()):
        y = pad + i * row_h
        im512 = Image.open(OUT / f"{key}_512.png")
        icon16 = im512.resize((16, 16), Image.LANCZOS)

        for half, (x0, bg, tcol, mcol) in enumerate(
                [(0, LIGHT, TEXT_L, MUTED_L), (W // 2, DARK, TEXT_D, MUTED_D)]):
            d.text((x0 + 30, y + 14), title, font=f_title, fill=tcol)
            d.text((x0 + 30, y + 56), desc, font=f_desc, fill=mcol)
            # 大图标 128
            canvas = Image.new("RGBA", (128, 128), bg)
            big = im512.resize((128, 128), Image.LANCZOS)
            canvas.paste(big, (0, 0), big)
            img.paste(canvas, (x0 + 30, y + 90))
            # 原尺寸 48 / 32 / 16（底对齐）
            xx, base = 196, y + 142
            for s in (48, 32, 16):
                img.paste(strip(im512, s, bg), (x0 + xx, base - s))
                xx += s + 24
            # 16px 最近邻 8x 放大
            img.paste(zoom16(im512, bg), (x0 + 430, y + 90))
            d.text((x0 + 430, y + 56), "16px ×8", font=f_small, fill=mcol)
        # 方案分隔线
        d.line([(28, y + row_h - 16), (W - 28, y + row_h - 16)],
               fill=(210, 214, 228, 120), width=1)

    img.convert("RGB").save(OUT / "preview.png")
    print("preview saved", img.size)


if __name__ == "__main__":
    main()
