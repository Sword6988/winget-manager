"""解析 `winget list` 的表格输出。

难点：表头是中文（名称/ID/版本/可用/源），且列宽按「显示宽度」对齐
（一个汉字占 2 列）。因此不能用字符下标切片：先把表头里每个标签的起始
位置换算成「显示列号」，再按显示宽度把每行映射回字符位置切片。
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

_SEPARATOR = re.compile(r"^-{5,}$")
_TOKEN = re.compile(r"\S+")
_GAP_SPLIT = re.compile(r"\s{2,}")

_HEADER_LABELS: dict[str, set[str]] = {
    "name": {"名称", "name"},
    "id": {"id", "包 id", "标识符", "identifier"},
    "version": {"版本", "version"},
    "available": {"可用", "available"},
    "source": {"源", "source"},
}
_FIELD_ORDER = ["name", "id", "version", "available", "source"]

_NOISE_PREFIXES = (
    "已找到",
    "找不到",
    "以下",
    "the following",
    "no installed package",
    "没有找到",
    "已安装的程序包",
)


@dataclass
class RawPackage:
    name: str
    package_id: str
    version: str = ""
    available: str = ""
    source: str = ""


def _char_width(ch: str) -> int:
    if unicodedata.combining(ch):
        return 0
    return 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1


def display_width(text: str) -> int:
    return sum(_char_width(ch) for ch in text)


def _locate_columns(header: str) -> tuple[list[int], list[str]] | None:
    """返回每个列的「显示列号」起点与字段名。"""
    tokens = [(match.start(), match.group()) for match in _TOKEN.finditer(header.strip())]
    if len(tokens) < 2:
        return None
    offsets: list[int] = []
    fields: list[str] = []
    for index, (position, label) in enumerate(tokens):
        offsets.append(display_width(header[:position]))
        lowered = label.strip().lower()
        matched = next(
            (field for field, names in _HEADER_LABELS.items() if lowered in names), None
        )
        if matched is None:
            matched = _FIELD_ORDER[index] if index < len(_FIELD_ORDER) else f"extra{index}"
        fields.append(matched)
    if "id" not in fields:
        return None
    return offsets, fields


def _split_row(line: str, offsets: list[int]) -> list[str]:
    """按显示列号切分一行，返回与 offsets 等长的单元格列表。"""
    cells: list[str] = []
    start = 0
    width = 0
    index = 0
    boundary = 1  # offsets[0] 恒为 0，第一列不需要切
    length = len(line)
    while index < length:
        if boundary < len(offsets) and width >= offsets[boundary]:
            cells.append(line[start:index])
            start = index
            boundary += 1
            continue
        width += _char_width(line[index])
        index += 1
    cells.append(line[start:])
    while len(cells) < len(offsets):
        cells.append("")
    return cells


def parse_list_output(text: str) -> list[RawPackage]:
    lines = [line.rstrip("\r") for line in text.splitlines()]
    sep_index = next(
        (i for i, line in enumerate(lines) if _SEPARATOR.match(line.strip())),
        None,
    )
    if sep_index is None or sep_index == 0:
        return _parse_by_gaps(lines)

    located = _locate_columns(lines[sep_index - 1])
    if located is None:
        return _parse_by_gaps(lines)
    offsets, fields = located

    packages: list[RawPackage] = []
    for line in lines[sep_index + 1 :]:
        stripped = line.strip()
        if not stripped or _SEPARATOR.match(stripped):
            continue
        if stripped.lower().startswith(_NOISE_PREFIXES):
            continue
        cells = [cell.strip() for cell in _split_row(line, offsets)]
        record = {field: (cells[i] if i < len(cells) else "") for i, field in enumerate(fields)}
        package_id = record.get("id", "")
        if not package_id:
            continue
        packages.append(
            RawPackage(
                name=record.get("name", "").strip(),
                package_id=package_id,
                version=record.get("version", ""),
                available=record.get("available", ""),
                source=record.get("source", ""),
            )
        )
    if packages:
        return packages
    return _parse_by_gaps(lines)


def _parse_by_gaps(lines: list[str]) -> list[RawPackage]:
    """兜底解析：表头识别失败时，按「2 个以上空格」切分并从右往左认领字段。"""
    known_sources = {"winget", "msstore"}
    packages: list[RawPackage] = []
    header_seen = False
    for line in lines:
        stripped = line.strip()
        if not stripped or _SEPARATOR.match(stripped):
            continue
        if stripped.lower().startswith(_NOISE_PREFIXES):
            continue
        parts = [part.strip() for part in _GAP_SPLIT.split(stripped) if part.strip()]
        if len(parts) < 2:
            continue
        if not header_seen and parts[0].lower() in ("名称", "name"):
            header_seen = True
            continue
        source = ""
        if parts[-1].lower() in known_sources:
            source = parts.pop()
        version = ""
        if len(parts) >= 3 and re.match(r"^[0-9v]", parts[-1]):
            version = parts.pop()
        if len(parts) >= 4 and re.match(r"^[0-9v]", parts[-1]):
            parts.pop()  # 「可用」列
        if len(parts) < 2:
            continue
        packages.append(
            RawPackage(name=parts[0], package_id=parts[1], version=version, source=source)
        )
    return packages
