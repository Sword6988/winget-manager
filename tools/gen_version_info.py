"""从 core/version.py 生成 PyInstaller 的 version_info.txt。

改版本号只改 core/version.py，然后跑本脚本：
    py -3.14 tools/gen_version_info.py

产物会被 spec 的 `version='version_info.txt'` 吃进 exe 的「属性 → 详细信息」，
四段式版本号不足的部分补 0（2.14.0 → 2.14.0.0）。
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from core.version import APP_VERSION  # noqa: E402

OUT = ROOT / "version_info.txt"

TEMPLATE = """\
# -*- mode: python ; coding: utf-8 -*-
# PyInstaller 版本资源：写入 exe 的「属性 → 详细信息」
# 本文件由 tools/gen_version_info.py 生成，改版本号请改 core/version.py 后重跑脚本。
VSVersionInfo(
  ffi=FixedFileInfo(
    filevers={vers},
    prodvers={vers},
    mask=0x3f,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
    ),
  kids=[
    StringFileInfo(
      [
      StringTable(
        u'080404B0',
        [StringStruct(u'CompanyName', u'Shibeng'),
        StringStruct(u'FileDescription', u'Winget 软件管家 - 已装软件清点与重装清单导出'),
        StringStruct(u'FileVersion', u'{quad}'),
        StringStruct(u'InternalName', u'WingetSoftwareManagerQt'),
        StringStruct(u'LegalCopyright', u'Copyright (C) 2026 Shibeng'),
        StringStruct(u'OriginalFilename', u'WingetManager.exe'),
        StringStruct(u'ProductName', u'Winget 软件管家'),
        StringStruct(u'ProductVersion', u'{quad}')])
      ]),
    VarFileInfo([VarStruct(u'Translation', [2052, 1200])])
  ]
)
"""


def _quad(version: str) -> tuple[int, int, int, int]:
    """'2.14.0' → (2, 14, 0, 0)；非法段落直接报错，别默默写个错版本号进 exe。"""
    parts: list[int] = []
    for chunk in version.strip().split(".")[:4]:
        if not chunk.isdigit():
            raise SystemExit(f"版本号段必须是整数: {version!r}")
        parts.append(int(chunk))
    while len(parts) < 4:
        parts.append(0)
    return tuple(parts)  # type: ignore[return-value]


def render(version: str) -> str:
    quad = _quad(version)
    return TEMPLATE.format(
        vers="(" + ", ".join(str(n) for n in quad) + ")",
        quad=".".join(str(n) for n in quad),
    )


def main() -> int:
    text = render(APP_VERSION)
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"{OUT.name} <- {APP_VERSION}（{'.'.join(str(n) for n in _quad(APP_VERSION))}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
