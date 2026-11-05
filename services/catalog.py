"""服务层：把 winget 的原始数据加工成前端可用的目录（去噪、分类、判定能否重装）。"""

from __future__ import annotations

import platform
import threading
import time
from dataclasses import asdict, dataclass, field

from core.config import Config
from core.errors import ScanCancelled
from core.logging_setup import get_logger
from services.winget_cli import WingetRepository
from services.winget_table import RawPackage, parse_list_output

logger = get_logger("catalog")

ID_KIND_MSIX = "msix"
ID_KIND_REGISTRY = "registry"
ID_KIND_WINGET = "winget"

# 分类规则：命中即归类，按顺序匹配（越具体的放前面）
CATEGORY_RULES: list[tuple[str, tuple[str, ...]]] = [
    ("运行库与组件", (
        "vcredist", "visualc++", "redistributable", "dotnet", "windowsappruntime", "vclibs",
        "ui.xaml", "appinstaller", "directx", "webview2", ".net framework", "net framework",
        "net.native", "desktopappruntime", "xna",
    )),
    ("驱动与硬件", (
        "amd", "nvidia", "intel", "realtek", "dolby", "audio", "driver", "chipset", "radeon",
        "graphics", "touchpad", "synaptics", "logitech", "razer",
    )),
    ("开发工具", (
        "git", "python", "nodejs", "node.js", "visualstudio", "vscode", "dbeaver", "terminal",
        "wsl", "docker", "jdk", "java", "idea", "pycharm", "curl", "notepad", "marktext",
        "jgraph", "draw.io", "chsrc", "clink", "wingetcreate", "mysql", "postgres", "postman",
        "navicat", "sublime", "mingw", "cmake", "gradle", "maven", "anaconda", "conda",
        "github", "gitlab", "hBuilder", "devcpp", "keil", "vs code",
    )),
    ("浏览器", ("chrome", "firefox", "edge", "opera", "brave", "vivaldi", "浏览器")),
    ("通讯与协作", (
        "wechat", "weixin", "wecom", "企业微信", "微信", "qq", "dingtalk", "钉钉", "feishu",
        "飞书", "workbuddy", "telegram", "discord", "slack", "zoom", "deskgo", "桌面整理",
        "wemeet", "腾讯会议",
    )),
    ("网络与下载", (
        "clash", "v2ray", "xray", "thunder", "迅雷", "download", "idm", "localsend", "uuremote",
        "teamviewer", "anydesk", "todesk", "sunlogin", "向日葵", "proxy", "clashverge", "neat",
        "motrix", "aria2",
    )),
    ("游戏", ("steam", "epic", "battle.net", "ldplayer", "雷电", "模拟器", "game", "wegame", "游戏")),
    ("媒体与文档", (
        "potplayer", "vlc", "pdfcraft", "pdf", "spotify", "music", "网易云", "qqmusic", "爱奇艺",
        "bilibili", "剪映", "capcut", "obs", "photoshop", "figma", "office", "wps", "acrobat",
        "看图", "播放器",
    )),
    ("安全", ("huorong", "火绒", "360", "defender", "antivirus", "安全", "杀毒", "kaspersky")),
    ("效率与系统工具", (
        "everything", "utools", "pixpin", "7zip", "7-zip", "winrar", "bandizip", "peazip",
        "fileconverter", "geekuninstaller", "twinkle", "tubatools", "palminput", "input", "输入法",
        "snipaste", "quicker", "ditto", "wox", "powertoys", "translucenttb", "rime", "搜狗",
    )),
]

DEPENDENCY_CATEGORY = "运行库与组件"


@dataclass
class Package:
    packageIdentifier: str
    name: str
    version: str = ""
    availableVersion: str = ""
    source: str = ""
    idKind: str = ID_KIND_WINGET
    reinstallable: bool = False
    category: str = "其他"
    isDependency: bool = False

    def to_dict(self) -> dict:
        return asdict(self)


@dataclass
class CatalogSnapshot:
    generatedAt: str
    durationMs: int
    wingetVersion: str
    machine: dict
    counts: dict
    packages: list[Package] = field(default_factory=list)
    # 可重装清单（winget export）没取到时的可读原因；空串表示正常
    exportError: str = ""

    def to_dict(self) -> dict:
        data = asdict(self)
        data["packages"] = [p.to_dict() for p in self.packages]
        return data


def _classify(raw: RawPackage) -> str:
    haystack = f"{raw.name} {raw.package_id}".lower()
    for category, keywords in CATEGORY_RULES:
        for keyword in keywords:
            if keyword in haystack:
                return category
    return "其他"


def _id_kind(package_id: str) -> str:
    lowered = package_id.lower()
    if lowered.startswith("msix\\"):
        return ID_KIND_MSIX
    if lowered.startswith("arp\\"):
        return ID_KIND_REGISTRY
    if "\\" in package_id:
        return ID_KIND_REGISTRY
    return ID_KIND_WINGET


class CatalogService:
    """负责枚举 + 富化 + 短时缓存。

    锁只保护 `_snapshot` 的读写，绝不在锁内跑 winget：一次扫描动辄几十秒，
    把锁跨在子进程外面会让「取消」和后续刷新全部堵死。
    """

    def __init__(self, config: Config, repository: WingetRepository):
        self._config = config
        self._repo = repository
        self._lock = threading.Lock()
        self._snapshot: CatalogSnapshot | None = None
        self._snapshot_at = 0.0

    # ── 对外接口 ────────────────────────────────────────────────
    def get_catalog(self, force: bool = False, cancel: threading.Event | None = None,
                    winget_version: str = "") -> CatalogSnapshot:
        with self._lock:
            fresh = (
                self._snapshot is not None
                and (time.time() - self._snapshot_at) < self._config.cache_ttl
            )
            if fresh and not force:
                return self._snapshot  # type: ignore[return-value]
        # 锁外构建：期间不持锁，取消/查询都不会被长任务挡住
        snapshot = self._build_snapshot(cancel, winget_version)
        with self._lock:
            self._snapshot = snapshot
            self._snapshot_at = time.time()
        return snapshot

    # ── 内部实现 ────────────────────────────────────────────────
    def _build_snapshot(self, cancel: threading.Event | None = None,
                        winget_version: str = "") -> CatalogSnapshot:
        started = time.perf_counter()
        list_result = self._repo.list_installed(cancel=cancel)
        if cancel is not None and cancel.is_set():
            raise ScanCancelled("扫描已取消")
        raw_packages = parse_list_output(list_result.stdout)
        exportable, export_error = self._repo.export_ids(cancel=cancel)

        unique: dict[str, Package] = {}
        for raw in raw_packages:
            if not raw.package_id:
                continue
            key = raw.package_id.lower()
            reinstallable = raw.package_id in exportable
            category = _classify(raw)
            package = Package(
                packageIdentifier=raw.package_id,
                name=raw.name.strip() or raw.package_id,
                version=raw.version,
                availableVersion=raw.available,
                source=raw.source,
                idKind=_id_kind(raw.package_id),
                reinstallable=reinstallable,
                category=category,
                isDependency=category == DEPENDENCY_CATEGORY,
            )
            # 同一 ID 可能有多行（x86/x64 组件），保留信息更全的一条
            previous = unique.get(key)
            if previous is None or (not previous.reinstallable and package.reinstallable):
                unique[key] = package

        packages = sorted(
            unique.values(),
            key=lambda p: (not p.reinstallable, p.category, p.name.lower()),
        )
        duration_ms = int((time.perf_counter() - started) * 1000)
        counts = {
            "total": len(packages),
            "reinstallable": sum(1 for p in packages if p.reinstallable),
            "unsupported": sum(1 for p in packages if not p.reinstallable),
            "dependencies": sum(1 for p in packages if p.isDependency),
        }
        snapshot = CatalogSnapshot(
            generatedAt=time.strftime("%Y-%m-%dT%H:%M:%S%z"),
            durationMs=duration_ms,
            wingetVersion=winget_version or "unknown",
            machine={
                "hostname": platform.node(),
                "os": f"{platform.system()} {platform.release()} ({platform.version()})",
                "arch": platform.machine(),
            },
            counts=counts,
            packages=packages,
            exportError=export_error or "",
        )
        logger.info(
            "目录刷新完成: %s 项（可重装 %s）%sms",
            counts["total"],
            counts["reinstallable"],
            duration_ms,
        )
        return snapshot
