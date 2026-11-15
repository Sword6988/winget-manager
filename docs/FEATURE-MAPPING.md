# FEATURE-MAPPING

本文档逐项对应原版（`_ref/` 下的 Python/PySide6 实现）与本版（C# WPF）的实现关系。
对等程度分三档：

- **一致**：行为与输出逐字节/逐字符对齐；
- **等价**：行为对齐，实现机制不同（例如 QThread → Task）；
- **近似**：视觉/行为意图一致，细节有差异（差异项全部收在 §3）。

---

## 1. 功能对应关系

| # | 原版（Python） | 本版（C# WPF） | 程度 | 验证方式 |
|---|---|---|---|---|
| 1 | `core/config.py::load_config` | `Core/AppConfig.cs::Load` | 等价 | 环境变量边界值（10/3600/0）逐一对照 |
| 2 | `core/config.py::_env_int` | `Core/AppConfig.cs::ReadEnvInt` | 一致 | 非整数/越界 → 回默认值并记日志 |
| 3 | `core/config.py::find_winget` | `Core/AppConfig.cs::FindWinget` | 等价 | 优先级 WPM_WINGET_EXE → PATH → WindowsApps；原版起 `where` 子进程，本版直查文件（少一次 winget 互斥） |
| 4 | `core/errors.py::AppError` + 5 子类 | `Core/AppException.cs` + 5 子类 | 一致 | `Code/Status/UserMessage` 三元组逐字对齐（`400 bad_request`、`499 scan_cancelled` …） |
| 5 | `core/fsio.py::atomic_write_text` | `Core/AtomicFile.cs::WriteTextAtomic` | 一致 | 临时文件 + 替换；崩溃中断不留半个文件 |
| 6 | `core/fsio.py::atomic_write_json` | `Core/AtomicFile.cs::WriteJsonAtomic` | 一致 | 同上（UTF-8 无 BOM） |
| 7 | `core/logging_setup.py` | `Core/AppLogger.cs` | 等价 | 文件 INFO/WARNING/ERROR + 512KB×2 轮转；界面 tag 额外携带颜色语义 |
| 8 | `core/version.py::APP_VERSION` | `Core/AppVersion.cs::Version` | 基线一致 | 原版 `2.18.0`；WPF 侧当前 `2.18.1`（界面修订独立递增），AssemblyInfo 四段同源 |
| 9 | `services/winget_cli.py::WingetRepository.run/_run_once` | `Core/ProcessRunner.cs::Run` | 等价 | 200ms 轮询 + 互斥退避重试 + 取消杀进程树 |
| 10 | `winget_cli._is_mutex_conflict`（`0x8A150001`） | `ProcessRunner`（`unchecked((int)0x8A150001)`） | 一致 | 退出码是负数，必须 unchecked 比较 |
| 11 | `winget_cli._decode` | `Core/EncodedText.cs::Decode` | 一致 | `utf-8-sig` → GBK → 替换回退，优先顺序一致 |
| 12 | `winget_cli.version/list_installed/show_metadata` | `Services/WingetRepository.cs` 同名三方法 | 一致 | 参数与超时逻辑一致 |
| 13 | `services/winget_table.py::parse_list_output` | `Services/WingetTableParser.cs::Parse` | 一致 | 显示宽度切片：全角=2、组合字符=0、emoji（代理对）=2 整体处理 |
| 14 | `winget_table._locate_columns` | `WingetTableParser` 列定位 | 一致 | 表头定位失败 → 「2+ 空格从右往左」兜底（`_parse_by_gaps` 对齐） |
| 15 | `services/catalog.py::CatalogService.get_catalog` | `Services/PackageCatalog.cs::GetSnapshotAsync` | 等价 | 目录 TTL 缓存 + 强制刷新语义一致 |
| 16 | `catalog._is_reinstallable/_is_dependency/_id_kind` | `PackageCatalog` 同名判定 | 一致 | 关键词表逐条对照 |
| 17 | `catalog.pending_metadata_ids` | `PackageCatalog::PendingMetadataIds` | 一致 | 可重装 ∧ 关键词=软件 ∧ 缓存未核查 |
| 18 | `services/metadata.py::RuntimeCache` | `Services/RuntimeCache.cs` | 一致 | schema=1；`dep` 必须是 JSON bool，字符串 "true" 视为未命中 |
| 19 | `metadata.analyze_runtime_signal/matched_signal` | `Services/MetadataProbe.cs::Analyze` | 一致 | 信号词表逐条对照；None → 不写缓存下次重试 |
| 20 | `metadata.fetch_show_output` | `MetadataProbe::FetchAsync` | 等价 | 后台逐包 show，scanGate 让路给新扫描 |
| 21 | `services/profiles.py::normalize_winget_version` | `Services/ExportProfiles.cs::NormalizeWingetVersion` | 一致 | 正则 `^v?(\d+…)`，不匹配 → `1.0.0` |
| 22 | `profiles.build_winget_manifest` | `ExportProfiles::BuildManifest` | 一致 | 键序 `$schema`→`CreationDate`→`Sources`→`WinGetVersion`；Sources 字典序（msstore 在前）；不写 Version |
| 23 | `profiles.source_breakdown` | `ExportProfiles::SourceBreakdown` | 一致 | winget / msstore 计数 |
| 24 | `profiles.build_restore_bat`（GBK） | `ExportProfiles::BuildRestoreBat` | 一致 | GBK + `EncoderFallback.ExceptionFallback`；`%`→`%%`；basename 防目录穿越 |
| 25 | `profiles._render_restore_bat` | `ExportProfiles::RenderRestoreBat` | 一致 | `chcp 936`、`where winget`、`net session`、逐行对照 |
| 26 | `profiles.build_unreinstallable_list` | `ExportProfiles::BuildUninstallableList` | 一致 | 全部非可重装包；`(名称小写, id 小写)` 排序；空集合句柄行一致 |
| 27 | `qt_app.py::ScanWorker(QThread)` | `Services/ScanService.cs` + `MainViewModel` Task | 等价 | QThread.request_cancel → CancellationToken |
| 28 | `qt_app.py::MetadataWorker(QThread)` | `MainViewModel` 富化链 + `MetadataProbe` | 等价 | `_wait_for_scan` → scanGate（`ManualResetEventSlim`） |
| 29 | `qt_app.py::_start_scan/_on_scan_ok/_on_scan_err` | `MainViewModel::StartScanAsync/OnScanOk/OnScanFailed` | 一致 | 状态机迁移逐一对照（§4.3） |
| 30 | `qt_app.py::_start_metadata_enrichment` | `MainViewModel::StartEnrichmentAsync` | 一致 | 日志四句文案逐字对齐（开始/改判/完成/打断） |
| 31 | `qt_app.py::_rebuild_list` | `MainWindow::RebuildList` + `ListPresenter` | 等价 | 数据折叠与渲染分离（原版混在 Qt 类里） |
| 32 | `qt_app.py::_update_sticky/_scroll_to_section` | `MainWindow` 吸顶段（缓存 Y 表 + TranslateTransform） | 近似 | 推入公式取 `-max(0, 42-(nextTop-offset))`（见 §3 #11） |
| 33 | `qt_app.py::_set_all/_invert/selected_packages` | `MainViewModel::SetAll/Invert/SelectedPackages` | 一致 | 批量范围 = 屏幕可见范围（共用 `SearchQuery`） |
| 34 | `qt_app.py::_flash` | `MainWindow::Flash`（220ms DispatcherTimer） | 一致 | flash 生效期间不覆盖底栏文案 |
| 35 | `qt_app.py::_show_row_menu` | `PackageRowControl` 右键菜单 | 一致 | 复制包 ID/名称/名称+ID/安装命令/勾选切换 |
| 36 | `qt_app.py::log_append/_save_log` | `LogPanelControl::Append/SaveToFile` | 一致 | UTF-8 落盘；默认名 `winget-manager-yyyyMMdd-HHmmss.log` |
| 37 | `qt_app.py::_on_export` + `qt_ui.ExportDialog` | `MainWindow::OpenExportDialog` + `Views/ExportDialog` | 等价 | 表单页/成功页两页切换；三产物写盘全部走 `ExportProfiles` |
| 38 | `qt_app.py::_restore_window_state/_save_window_state` | `MainWindow::RestoreWindowGeometry/OnClosing` + `SettingsStore` | 近似 | 存储介质不同（QSettings→settings.json）；新增越界回落 |
| 39 | `qt_app.py::_save_selection/_flush_selection/_load_selection` | `Services/SelectionStore.cs` | 一致 | 严格布尔读入（`"false"`/`0` 一律 False） |
| 40 | `qt_app.py::closeEvent` | `MainWindow::OnClosing` + `WaitForScanShutdown` | 等价 | 停计时器 → flush 勾选 → 存设置 → 取消富化 → 等扫描 ≤3s（消息泵，不阻塞续体） |
| 41 | `qt_app.py::_claim_single_instance` | `Core/SingleInstance.cs` | 等价 | 原版 QLocalSocket；本版 `Local\` Mutex + `GC.KeepAlive` |
| 42 | `qt_app.py::main/_install_logging` | `App.xaml.cs` | 等价 | 单实例 → 全局异常兜底 → 日志初始化 → 主窗口 |
| 43 | `qt_common.py::default_export_path` | `Core/AppPaths.cs::DefaultExportPath` | 一致 | 上次 → `D:\apps.json` → 文档 → 用户目录 |
| 44 | `qt_common.py::settings()`（QSettings） | `Core/SettingsStore.cs`（settings.json） | 近似 | 键名语义对齐，介质不同（见 §3 #1） |
| 45 | `qt_common.py::app_icon/_brand_arrow_path` | `Assets/app.ico`（`tools/make_icon.py` 生成） | 等价 | 圆角方渐变 + 下箭头托盘；五档尺寸 |

## 2. UI 布局对应

原版 pt 字号 → WPF DIP 换算：`pt × 96 / 72`（例如 19pt → 25.3）。

| 层 | 原版（Qt） | 本版（WPF） | 关键尺寸/文案 |
|---|---|---|---|
| 标题栏 | `qt_ui.TitleBar`，高 52 | `MainWindow.xaml` Row0，高 52（`Metrics.TitleBarHeight`） | 品牌图标 26×26 圆角 8 + 渐变标题 25.3 + 「WPF 桌面版」胶囊 |
| 顶部渐变线 | TitleBar 顶部 3px | Row0 顶部 3px `Brush.AccentGradient` Border | 一致 |
| 头部 | 副标题 + 4 张 `StatCard` | Row1 `SubtitleText` + 4×`StatCard`（104×70） | 卡片悬停呼吸动画近似（§3 #10） |
| 玻璃工具条 | 半透明圆角条 | Row2 `Border.Glass`（CornerRadius 10） | `Brush.Glass`/`GlassBorder`；毛玻璃近似（§3 #2） |
| 搜索框 | QLineEdit + 放大镜 + 占位 | Row2 `TextBox.Search` + Path 放大镜 | 高 36，圆角 10 |
| 分组胶囊 | 横向 chip 行 | Row3 `ChipHost`（`Button.Pill`） | 全部 / 软件 / 运行库 / winget 不支持 |
| 列表行 | `PackageRow`，高 46 | `PackageRowControl`，高 46（`Metrics.RowHeight`） | 复选框 20×20、不可勾选行 31px 占位、来源标签配色一致 |
| 分组头 | `GroupHeader`，高 42 | `GroupHeaderControl`，高 42 | 计数胶囊 3/12 + 一个批量按钮 |
| 分节 | `GroupSection`（折叠动画） | `GroupSectionControl` | 折叠动画简化（§3 #10） |
| 吸顶头 | `StickyHeader` | `StickyGroupHeader`，高 42 | 滚动时 `TranslateTransform` 位移，推入公式见 §3 #11 |
| 日志面板 | `LogGrip` 拖拽 + 折叠 | `LogPanelControl`（90~420，折叠 34） | 复制/保存/清空/收起四个动作一致 |
| 底栏 | 高 58 | Row6，高 58 | 状态文案 + 渐变数字 + 「/ N 项已勾选」+ 导出按钮 120×38 |
| 导出对话框 | `qt_ui.ExportDialog`（560 宽两页） | `Views/ExportDialog`（560 宽，`SizeToContent=Height`） | 表单页 padding 28/26/28/22；成功页 ✓ 徽章 46×46 圆角 23 |
| 关于对话框 | `qt_ui.AboutDialog` | `Views/AboutDialog` | 诊断信息 = 版本/winget/OS/数据目录/计数/日志尾行 |
| winget 引导框 | `qt_app._show_winget_guide` | `Views/WingetGuideDialog` | 商店 URI `9NBLGGH4NNS1` + GitHub 下载地址一致 |
| 空态 | `qt_ui` 内空态卡 | `Controls/EmptyStatePanel` | 文案逐字一致（含「仅看已选」分情形） |
| 骨架屏 | `_build_skeleton` | `Controls/SkeletonPanel` | 扫描中占位动画近似 |
| 窗口 | 1240×880，最小 940×600 | 同（`Metrics.WindowWidth/…`） | 最大化留边处理见 §3 #12 |

## 3. 已知差异（14 条）

| # | 差异 | 原因 | 影响 |
|---|---|---|---|
| 1 | settings 存 **settings.json**，原版用 QSettings 写注册表 | 落文件可随数据目录整体备份/迁移，也便于排障时直接看 | 与原版设置不互通；键语义已对齐 |
| 2 | 玻璃工具条用半透明白色叠加，**没有真实的模糊（Acrylic）** | `AllowsTransparency=False` 才能保住硬件加速；开 Acrylic 需要整窗透明，长列表滚动掉帧 | 背景透出的颜色比原版更「实」，但滚动性能好得多 |
| 3 | 窗口四角外是**直角**（内层 Border 圆角是画上去的） | `WindowChrome + AllowsTransparency=False` 的固有限制（§10-15） | 仅 1px 级视觉差异 |
| 4 | QThread → **Task + CancellationToken + IProgress<T>** | WPF 惯用异步模型 | 行为一致；富化让路用 `ManualResetEventSlim` 实现 scanGate |
| 5 | 单实例用 **Local\ Mutex**，原版用 QLocalSocket | 无 Qt IPC 栈 | 行为一致（第二个进程直接退出） |
| 6 | 发布形态是**单文件 exe**（`build-release.bat` → `dist\WingetManager.exe`），原版靠 PyInstaller spec | 零 NuGet ⇒ 14 个 XAML 编成 BAML 内嵌、图标与 manifest 内嵌，Release 产物天然只有一个 exe，不需要 ILMerge/Costura | 更简单：原版单 exe 首次启动要解压到临时目录（慢且有杀软误报），本版是纯托管加载，启动更快 |
| 7 | 字体回退：**不内置 HarmonyOS Sans SC** | 授权与体积考虑；`FontFamily.Ui` 默认 `Microsoft YaHei UI`，启动时探测系统字体覆写 | 字重与字距与原版有细微差别 |
| 8 | 列表**无虚拟化**：行控件全量创建 | 自绘行 + 手动吸顶需要拿真实坐标；典型装机量（<500）下全量创建更简单可靠 | 800 行以上滚动掉帧（`ListPresenter` 注释已标注升级路径） |
| 9 | 动画简化：StatCard 悬停呼吸、GroupSection 折叠等用**短动画或直接跳变** | Qt 的 QVariantAnimation 对应物（Storyboard）逐个移植收益低 | 不影响任何功能 |
| 10 | 吸顶头「推入」公式取 `-max(0, 42-(nextTop-offset))`，与架构文档原式符号相反 | 文档原式算出正值会把吸顶头往下推、盖住第一行（代码注释已说明） | 与原版行为一致，只是文档笔误 |
| 11 | 日志「保存…」对话框默认名 `winget-manager-yyyyMMdd-HHmmss.log`，原版同名不同分隔符位置 | 时间戳格式差一位 | 无 |
| 12 | 最大化留边：`StateChanged` 里只对 `Maximized` 设 `RootGrid.Margin=7`，还原置 0 | WindowChrome 撑大窗口的补偿值按 `ResizeBorderThickness` 推导 | **待真机确认**：不同 DPI/任务栏位置下可能需要并入 `SystemParameters.WindowNonClientFrameThickness` |
| 13 | 三个对话框的高度策略：`SizeToContent="Manual"` + XAML 初值（About 430 / 引导 380 / 导出 400），`Loaded` 里由 `Controls/DialogSizer` 按内容精确测量；导出对话框切到成功页后再测一次 | 原版 Qt 是 `adjustSize()`（布局完成即定尺寸）；WPF 的 `SizeToContent="Height"` + `WindowStyle=None` + `AllowsTransparency=True` 在首帧没有非客户区可借，会**先显示一帧错误高度再跳到正确值**（慢机器/高 DPI 下肉眼可见抖动） | 视觉终态与设计一致，只是换了一条测量路径；成功后 `DialogSizer` 还会相对 Owner 重新居中（WPF 只在首次 Show 时按当时高度居中一次） |
| 14 | 新增**便携模式**（`WPM_PORTABLE=1` 或 exe 旁放 `WingetManager.portable`）：数据改写到 exe 同目录 `Data\`；单实例互斥量名按数据目录加 FNV 后缀 | 绿色版需求（U 盘/多机同步）；默认仍写 `%LOCALAPPDATA%` 以保证与原版 `selection.json` 互通 | 默认行为与原版一致；开启后两个不同目录的副本可并行运行、互不抢窗口（原版与普通模式仍是单实例） |

> 其余「清单不写 Version」、三产物编码、布尔严格读入等易错项在两版间是**一致**的，
> 不算差异，见 §1 与 ARCHITECTURE §10。
