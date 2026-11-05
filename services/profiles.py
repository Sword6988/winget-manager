"""清单生成：把勾选的软件包转成 winget 官方 import 格式，并生成恢复脚本。

只做「导出」这一件事。导入/恢复走 winget 自己的 `winget import`，
本工具不再解析回读清单（用户明确不需要「导入恢复」功能）。
"""

from __future__ import annotations

import re
from datetime import datetime
from pathlib import Path

from core.errors import BadRequest
from services.catalog import Package

PROFILE_SCHEMA_URL = "https://aka.ms/winget-packages.schema.2.0.json"
_VERSION_RE = re.compile(r"^v?(\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.\-]+)?)")


def normalize_winget_version(raw: str) -> str:
    """winget 的 import schema 对 WinGetVersion 有正则约束：必须是数字开头的版本号。"""
    match = _VERSION_RE.match(str(raw or "").strip())
    return match.group(1) if match else "1.0.0"


def build_winget_manifest(selected: list[Package], winget_version: str) -> dict:
    """标准 winget import 格式：可直接 `winget import -i file.json`。"""
    by_source: dict[str, list[Package]] = {}
    for package in selected:
        if not package.reinstallable:
            continue
        source = package.source if package.source in ("winget", "msstore") else "winget"
        by_source.setdefault(source, []).append(package)

    details = {
        "winget": {
            "Argument": "https://cdn.winget.microsoft.com/cache",
            "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe",
            "Name": "winget",
            "Type": "Microsoft.PreIndexed.Package",
        },
        "msstore": {
            "Argument": "https://storeedgefd.dsx.mp.microsoft.com/v9.0",
            "Identifier": "StoreEdgeFD",
            "Name": "msstore",
            "Type": "Microsoft.Rest",
        },
    }
    return {
        "$schema": PROFILE_SCHEMA_URL,
        "CreationDate": datetime.now().astimezone().isoformat(timespec="milliseconds"),
        "Sources": [
            {
                "Packages": [{"PackageIdentifier": p.packageIdentifier} for p in items],
                "SourceDetails": details[source],
            }
            for source, items in sorted(by_source.items())
        ],
        "WinGetVersion": normalize_winget_version(winget_version),
    }


def source_breakdown(selected: list[Package]) -> dict[str, int]:
    """按来源统计可重装包（winget / msstore），用于导出后的提示。"""
    out = {"winget": 0, "msstore": 0}
    for package in selected:
        if not package.reinstallable:
            continue
        out[package.source if package.source in out else "winget"] += 1
    return out


def build_restore_bat(json_name: str, count: int, msstore: int = 0) -> str:
    """生成「双击即恢复」的批处理，放在清单同目录，重装系统后直接跑。

    cmd 默认代码页是 936，所以调用方要用 GBK 写盘，否则中文提示会乱码。
    `--ignore-unavailable`：源里已下架/改名的包会被跳过而不是中断整批。

    安全要点：清单名是用户可控的（导出时的文件名），直接拼进 cmd 会被 `&`、`|`、
    `%VAR%` 解释成命令。这里只取 basename 防目录穿越，`%` 双写防变量展开，
    并在 echo 里用引号隔离。
    """
    name = Path(json_name).name.replace("%", "%%")
    text = _render_restore_bat(name, count, msstore)
    try:
        text.encode("gbk")           # cmd 代码页 936，编不进去的字符写盘时会炸
    except UnicodeEncodeError as exc:
        raise BadRequest(
            f"清单文件名无法用 GBK 编码: {name}",
            user_message="文件名里有 emoji 等 GBK 无法表示的字符，请改用中文/英文/数字后重试。",
        ) from exc
    return text


def _render_restore_bat(name: str, count: int, msstore: int) -> str:
    """按成品清单名渲染批处理文本（name 已做过 basename 与 % 转义）。"""
    ms_tip = ""
    if msstore:
        ms_tip = (f"echo   其中 {msstore} 个来自微软商店 msstore，"
                  "可能要求先登录商店账号\r\n")
    lines = [
        "@echo off",
        "chcp 936 >nul",
        "title 恢复软件清单",
        'cd /d "%~dp0"',
        "",
        "where winget >nul 2>nul",
        "if errorlevel 1 (",
        "  echo.",
        "  echo [x] 未找到 winget，请先安装「应用安装程序 / App Installer」。",
        "  echo     打开 Microsoft Store 搜索 App Installer 即可安装。",
        "  echo.",
        "  pause",
        "  exit /b 1",
        ")",
        "",
        "net session >nul 2>nul",
        "if errorlevel 1 (",
        "  echo.",
        "  echo [!] 当前不是管理员权限。部分机器级安装包会失败，",
        "  echo     建议右键本文件 - 以管理员身份运行。继续执行请按任意键。",
        "  pause >nul",
        ")",
        "",
        "echo.",
        "echo   Winget 软件管家 · 恢复脚本",
        "echo   ----------------------------------------",
        f'echo   清单: "{name}"（{count} 个软件包）',
        ms_tip.rstrip("\r\n") if ms_tip else "echo.",
        "echo.",
        "echo   即将执行 winget import，请保持网络畅通。",
        "echo   源里已下架的包会被自动跳过，不影响其余安装。",
        "echo.",
        "pause",
        "",
        f'winget import -i "%~dp0{name}" --accept-package-agreements '
        "--accept-source-agreements --ignore-unavailable",
        "",
        "echo.",
        "echo   ----------------------------------------",
        "echo   执行结束。按任意键关闭。",
        "pause >nul",
    ]
    return "\r\n".join([ln for ln in lines if ln is not None]) + "\r\n"


def build_unreinstallable_list(packages: list[Package]) -> str:
    """无法自动重装的软件清单（纯文本，重装系统后照着手动装）。

    界面上这些行只在本次运行里可见，导出成 txt 才留得住。
    """
    items = [p for p in packages if not p.reinstallable]
    stamp = datetime.now().strftime("%Y-%m-%d %H:%M")
    head = [
        "Winget 软件管家 · 无法自动重装清单",
        f"导出时间：{stamp}",
        "",
        f"共 {len(items)} 项。这些软件没有可用的 winget 安装源，",
        "重装系统后需要手动下载安装（本清单仅供核对，不会被恢复脚本处理）。",
        "",
    ]
    if not items:
        return "\r\n".join(head + ["（没有这类软件，全部都能自动重装）"]) + "\r\n"

    by_cat: dict[str, list[Package]] = {}
    for p in items:
        by_cat.setdefault(p.category or "未分类", []).append(p)

    body: list[str] = []
    for cat in sorted(by_cat, key=lambda c: (-len(by_cat[c]), c)):
        group = sorted(by_cat[cat], key=lambda p: p.name.lower())
        body.append(f"【{cat}】{len(group)} 项")
        for p in group:
            ver = f"  v{p.version}" if p.version else ""
            src = f"  [{p.source}]" if p.source else ""
            body.append(f"  · {p.name}{ver}")
            body.append(f"    {p.packageIdentifier}{src}")
        body.append("")
    return "\r\n".join(head + body) + "\r\n"
