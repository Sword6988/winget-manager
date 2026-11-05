"""服务层：把 winget 的原始数据加工成前端可用的目录（去噪、判定能否重装）。

分组判定只有两个输入：`reinstallable`（能不能被 winget 重装）与
`isDependency`（是不是运行库/系统组件）。界面据此分「软件 / 运行库 /
winget 不支持」三组，不涉及任何关键词分类。本模块只负责把这两个布尔值
算准，具体分几组由 UI 层决定。
"""

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

# 运行库/系统组件名单：只用来给列表行打「运行库」角标、给统计卡计数，
# 不参与分组（分组只看 reinstallable）。
DEPENDENCY_KEYWORDS: tuple[str, ...] = (
    "vcredist", "visualc++", "redistributable", "dotnet", "windowsappruntime", "vclibs",
    "ui.xaml", "appinstaller", "directx", "webview2", ".net framework", "net framework",
    "net.native", "desktopappruntime", "xna",
)


@dataclass
class Package:
    packageIdentifier: str
    name: str
    version: str = ""
    availableVersion: str = ""
    source: str = ""
    idKind: str = ID_KIND_WINGET
    reinstallable: bool = False
    # 是不是运行库/系统组件（展示用，与分组无关）
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


def _is_dependency(raw: RawPackage) -> bool:
    """是否属于运行库/系统组件（名称或包 ID 命中即算）。"""
    haystack = f"{raw.name} {raw.package_id}".lower()
    return any(keyword in haystack for keyword in DEPENDENCY_KEYWORDS)


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
            package = Package(
                packageIdentifier=raw.package_id,
                name=raw.name.strip() or raw.package_id,
                version=raw.version,
                availableVersion=raw.available,
                source=raw.source,
                idKind=_id_kind(raw.package_id),
                reinstallable=reinstallable,
                isDependency=_is_dependency(raw),
            )
            # 同一 ID 可能有多行（x86/x64 组件），保留信息更全的一条
            previous = unique.get(key)
            if previous is None or (not previous.reinstallable and package.reinstallable):
                unique[key] = package

        # 可重装的排前面，组内按名称；分组本身由界面按 reinstallable 判定
        packages = sorted(
            unique.values(),
            key=lambda p: (not p.reinstallable, p.name.lower()),
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
