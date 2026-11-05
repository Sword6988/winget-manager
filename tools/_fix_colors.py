"""一次性把硬编码色值替换为主题令牌引用（仅处理安全位置）。"""
import re
from pathlib import Path

P = Path("qt_app.py")
lines = P.read_text(encoding="utf-8").split("\n")

MAP = {
    "#DFE2F0": "CHK_RING_HOVER", "#C3C8D8": "MUTED3", "#E9EBF4": "ROW_ALT",
    "#A9B1C0": "NAME_OFF", "#C3C9D6": "ID_OFF",
    "#98A0B1": "TAG_DIM_FG", "#F2F4F9": "TAG_DIM_BG", "#E7EAF3": "TAG_DIM_LINE",
    "#6D4FD0": "TAG_SRC_FG", "#F3F0FE": "TAG_SRC_BG", "#E5DFFB": "TAG_SRC_LINE",
    "#6B7280": "REF_NAME", "#B6BCCA": "REF_ID", "#9AA2B2": "REF_TAG_FG",
    "#F7F9FC": "REF_TAG_BG", "#EFF2F8": "REF_TAG_LINE",
    "#F1F3F9": "PILL_RING", "#E7EAF5": "PILL_RING", "#C9CDFB": "PILL_RING_FULL",
    "#F8F9FE": "GRAD_H1", "#EFF1FA": "GRAD_H2",
    "#535B6E": "LINK_FG", "#9AA3B5": "LINK_FG_OFF", "#AEB5C4": "LINK_FG_HOVER",
    "#8A93A6": "CHEV", "#6F7789": "HEAD_TITLE", "#AEB5C3": "HEAD_META",
    "#E4E8F1": "REF_DASH", "#F0F2F8": "REF_SEP",
    "#FBFDFF": "HEAD_TOP", "#F8F9FD": "HEAD_BOT", "#F1F2F8": "CHIP_BG",
    "#6A7183": "MUTED", "#EDEFF7": "LINE_SOFT",
    "#FAFBFD": "SURFACE_2", "#FAFBFE": "SURFACE_3", "#CDD2E2": "BORDER_HOVER",
    "#D2D6E4": "SCROLL", "#B8BED2": "SCROLL_HI",
    "#E8EBF3": "SKEL_1", "#EFF1F7": "SKEL_2",
    "#F5F6FC": "SURFACE_3", "#B9BFE0": "BORDER_HOVER",
    "#C5CAFF": "ACCENT_LINE", "#E4E8FF": "ACCENT_SOFT",
    "#F5F6FB": "SURFACE_3", "#FBFCFE": "SURFACE",
}

# 令牌定义区（含 _DARK 表）整体跳过
guard = next(i for i, ln in enumerate(lines) if ln.startswith("_LIGHT_SNAPSHOT"))
out, skipped = [], []
for i, line in enumerate(lines):
    if i <= guard + 30:
        out.append(line)
        continue
    new = line
    for hexv, tok in MAP.items():
        if hexv not in new:
            continue
        new = new.replace(f'QColor("{hexv}")', f"QColor({tok})")
        if hexv not in new:
            continue
        if re.search(r'\bf"""|\bf"|\bf\'', new):
            new = new.replace(hexv, "{" + tok + "}")
        else:
            skipped.append((i + 1, hexv, tok, line.strip()[:88]))
    out.append(new)

P.write_text("\n".join(out), encoding="utf-8")
print(f"替换完成，未处理 {len(skipped)} 处：")
for row in skipped:
    print("  L%-5s %-9s -> %-14s %s" % row)
