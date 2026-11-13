# PRD：Winget 软件管家（WinUI 3 重写版）

| 项目 | 内容 |
|---|---|
| Language | 简体中文 |
| Programming Language | C# / WinUI 3 (XAML) + Windows App SDK，.NET 8+，**Native AOT（PublishAot=true）** |
| Project Name | `winget_manager_winui` |
| 原始需求复述 | 将现有 PySide6 版「Winget 软件管家」（约 4900 行）用 C# / WinUI 3 重写，启用 Native AOT，**功能与业务逻辑保持不变**；已明确移除的「导入恢复」界面不迁移。 |

---

## 一、产品定义

### Product Goals

1. **迁移对等**：在 WinUI 3 下 1:1 保留原核心业务逻辑——扫描枚举、三组分组（软件 / 运行库 / winget 不支持）、勾选筛选、三件套导出，行为口径（判定规则、文件格式、默认路径、容错策略）与 Python 版完全一致。
2. **原生与体积**：以 Native AOT 编译为自包含原生 exe，无 .NET 运行时拖带、无 PyInstaller 解包开销，冷启动明显优于原版；代码全程 AOT 兼容（x:Bind 编译期绑定、JSON 源生成器，禁用反射依赖）。
3. **可取消、可容错**：保留「0.2 秒内真正终止 winget 子进程」的取消体验与全部容错链路（winget 互斥重试、BOM/编码回落、缓存损坏重建、缺 winget 引导不退出）。

### User Stories

1. 作为**重装系统的用户**，我要启动工具后自动扫描本机软件（约 2 秒），并看到「软件 / 运行库 / winget 不支持」三组列表，以便快速核对哪些能自动装回来。
2. 作为**挑选软件的用户**，我要用搜索框（多关键词、`id:` 前缀）、分组胶囊筛选和「仅看已选」缩小范围，并用「全选 / 反选 / 全选本组」批量勾选——且这些操作只作用于当前可见项，以便放心批量操作不误伤屏外勾选。
3. 作为**着急的用户**，我在扫描进行中点「取消扫描」，winget 子进程在 0.2 秒内被真正终止、界面不僵死，以便立刻重新扫描。
4. 作为**备份用户**，我点「导出配置」后在所选目录得到 `apps.json`（winget 官方 import 格式）+ `恢复-apps.bat`（自动检查管理员权限、`--ignore-unavailable`）+ `无法自动重装清单.txt`，以便新系统上双击 bat 一键恢复。
5. 作为**被外部 winget 占用的用户**，当扫描撞上 winget 进程互斥（退出码 `0x8A150001` 或空输出失败）时程序自动退避重试（最多 2 次 + 0.4s），以便一次外部占用不会毁掉整轮扫描。

---

## 二、技术规范

### Requirements Pool

#### P0 —— 必须迁移的核心功能（缺一不可）

**扫描与解析**
- P0-1 `winget list --accept-source-agreements --disable-interactivity` 单命令扫描；输出解码顺序 utf-8-sig → utf-8 → 本地编码（cp936）→ gbk → 容错替换。
- P0-2 表格解析按「显示宽度 + 中英表头」切列（汉字占 2 列），识别表头标签 名称/ID/版本/可用/源 的中英文变体，过滤噪声行；解析退化时记日志兜底。
- P0-3 重复 ID 去重（x86/x64 多行），保留信息更全的一条（不可重装让位给可重装）；排序为「可重装在前，组内按名称升序（不区分大小写）」。
- P0-4 版本探测 `winget --version`（30s 超时）：拿不到输出按 `unknown` 继续扫描，不中止；「未装 winget」只由 exe 定位失败触发，走引导页而非退出。

**分组判定（业务逻辑核心，口径不变）**
- P0-5 `reinstallable`：仅看「源」列 ∈ {winget, msstore}。
- P0-6 `isDependency` 双层判定：
  - 关键词初判：名称+包 ID 命中词表（vcredist / visualc++ / redistributable / dotnet / windowsappruntime / vclibs / ui.xaml / appinstaller / directx / webview2 / .net framework / net framework / net.native / desktopappruntime / xna）。
  - 元数据核查：扫描完成后后台对「可重装 ∧ 关键词判软件 ∧ 缓存未命中」的包逐个 `winget show --id <id> --exact`，解析描述/标记/安装器 URL 段落中的信号词（runtime / redistributable / redist / 运行库 / 运行时 / required to run，忽略大小写）。
  - **元数据只补漏不打假**：缓存 True 覆盖关键词补漏；缓存 False 仅避免重复 show，不构成否定；未命中退回关键词初判；show 失败/解析不出不写缓存。
  - 缓存落盘 `%LOCALAPPDATA%\WingetManager\metadata-cache.json`（schema 版本字段，损坏/结构不符自动作废重建）；每查一条立即落盘；与扫描共用 winget 入口——扫描中核查让路，用户重扫时核查被取消收尾、没查完的下轮自动续上；改判按批应用、不逐条刷新。

**列表交互**
- P0-7 三组固定顺序「软件 → 运行库 → winget 不支持」，空组不显示组头；「winget 不支持」组灰显不可勾选。
- P0-8 搜索：多关键词 AND 匹配，`id:` 前缀只匹配包 ID，`name:` 前缀只匹配名称，其余按「名称 + 包 ID」模糊匹配。
- P0-9 分组胶囊筛选：全部 / 软件 / 运行库 / winget 不支持。
- P0-10 「仅看已选」：仅本次会话生效；重扫后勾选为空时自动关闭；「仅看已选」下取消勾选，行立即消失。
- P0-11 勾选：全局「全选 / 反选」只作用于当前筛选出的可见可勾项；组头提供「全选本组 / 全不选」。
- P0-12 勾选持久化 `%LOCALAPPDATA%\WingetManager\selection.json`（按包 ID，原子写，损坏回退为空）。

**取消与并发**
- P0-13 「取消扫描」0.2 秒内真正终止 winget 子进程（轮询 + kill 模式，非等待超时）；退避等待期间命中取消同样立即中止。
- P0-14 winget 互斥处理：退出码 `0x8A150001` 或「失败且无任何输出」→ 退避 0.4s 重试，最多 2 次；重试退避计入同一总超时预算。
- P0-15 环境变量：`WPM_WINGET_EXE`（指定路径，指向不存在文件给明确提示）、`WPM_CACHE_TTL`（默认 60，0-3600，0=每次重扫）、`WPM_COMMAND_TIMEOUT`（默认 180，10-3600）。缺 winget 时主窗口照常启动并给「去商店装 App Installer」引导。

**导出**
- P0-16 三件套同目录生成：
  - `apps.json`：winget 官方 import schema 2.0，按 winget/msstore 分 Source 组，WinGetVersion 归一化为数字开头（解析不出回退 `1.0.0`）；
  - `恢复-apps.bat`：chcp 936 + GBK 写盘、检查 winget 存在与管理员权限（net session）、`winget import --ignore-unavailable`；清单名取 basename、`%` 双写转义防注入；文件名含 GBK 无法表示字符时给明确报错；
  - `无法自动重装清单.txt`：按名称平铺 + 导出时间戳。
- P0-17 导出默认路径链：上次使用路径 → `D:\apps.json`（D 盘存在时）→ `文档\apps.json`；路径记忆持久化。

**持久化与日志**
- P0-18 窗口/筛选/上次导出路径持久化到注册表 `HKCU\Software\Shibeng\WingetManager`（沿用原键位，兼容老版本用户数据）。
- P0-19 日志 `%LOCALAPPDATA%\WingetManager\logs\app.log`，512KB × 2 轮转；记录 winget 完整命令行参数。

**AOT 约束（横切）**
- P0-20 全代码 AOT 兼容：`PublishAot=true`；UI 绑定一律 `x:Bind`（禁用反射型 `{Binding}`）；JSON 读写（apps.json / selection.json / metadata-cache.json）一律 System.Text.Json 源生成器（`JsonSourceGenerationMode`）；禁用 `dynamic`、`Activator.CreateInstance` 等反射 API。

**UI 呈现**
- P0-21 底部进度条 + 已用时间提示；统计卡（总数 / 软件 / 运行库 / 不支持 数量）；运行库行角标。

#### P1 —— 体验增强

- P1-1 组头吸顶（WinUI 3 ListView 原生不支持粘性组头，见待确认问题 Q3；先做普通分组头，吸顶用 ItemsRepeater/自定义容器后续补）。
- P1-2 空态文案：无结果 / 「仅看已选」无勾选 / 「不支持」组说明（灰显仅作核对、不影响导出）。
- P1-3 窗口尺寸与位置记忆（随 P0-18 一并持久化）。
- P1-4 快捷键：Ctrl+A 全选当前可见项；搜索框聚焦时 Ctrl+A 保持选中输入文字。
- P1-5 导出成功反馈：按来源统计（winget N 个 / msstore N 个）+ 提示 msstore 包可能需登录商店。
- P1-6 跟随系统深浅色主题（Mica 背景），运行库角标/灰显样式适配两种主题。

#### P2 —— 可延后或裁剪

- P2-1 单实例激活（`AppInstance.FindOrRegisterForKey` 重定向到已有窗口）；原版未做单实例限制，属新策略而非迁移项。
- P2-2 「点吸顶组头回到该组开头」（依赖 P1-1 吸顶完成）。
- P2-3 关于对话框（版本号单一来源，exe 属性与界面一致）。
- P2-4 扫描日志面板（原版有滚动日志区，WinUI 版可先用状态栏文字替代）。

### UI Design Draft（WinUI 3 XAML 页面结构与交互映射）

单页应用，不用 NavigationView。整体 `Grid` 三行：顶栏 / 列表 / 底栏。不追求像素级还原，但下列交互全部保留：

| 原 PySide6 自绘控件 | WinUI 3 对应 | 说明 |
|---|---|---|
| 搜索框（多关键词 + `id:`） | `TextBox`（或 `AutoSuggestBox`） | 输入即筛选，解析逻辑见 P0-8 |
| 分组胶囊（全部/软件/运行库/不支持） | `SelectorBar`（WinAppSDK 1.5+）或一组 `ToggleButton` | 单选语义，见 P0-9 |
| 「仅看已选」按钮 | `ToggleButton` | 激活态高亮，见 P0-10 |
| 全选 / 反选 | 两个 `Button` | 只作用于当前可见可勾项，见 P0-11 |
| 软件行 PackageRow（自绘勾选行） | `ListView` + `ItemTemplate`：`CheckBox` + 名称/版本/ID + 运行库 `InfoBadge` | 「不支持」组行 `IsEnabled=False` 灰显；运行库角标用 InfoBadge |
| 组头（三组 + 全选本组） | `CollectionViewSource` 分组 + `GroupStyle.HeaderTemplate`（内含组名、计数、「全选本组」按钮） | 空组不产生组头；吸顶为 P1-1 |
| 组头吸顶 + 推入动画 | P1：先标准分组头；吸顶需 ItemsRepeater + 自定义布局，列为增强项 | 交互等价性靠胶囊筛选保证 |
| 底部进度条 + 取消扫描 | `ProgressBar`（扫描中 indeterminate）+ 状态文字（已用 x.xs · 点「取消扫描」可中断） | 同一按钮扫描中变「取消扫描」、完成后变「重新扫描」 |
| 统计卡 | 顶栏或底栏一组 `TextBlock`/轻量卡片 | 数量来自内存列表实时计算 |
| 导出配置按钮 | `Button`（Accent 样式）+ `ContentDialog` 确认 | 文件保存走 `FileSavePicker`（未打包应用需传入 Window 句柄初始化，AOT 兼容） |
| 缺 winget 引导页 | 窗口内切换到引导 `StackPanel`（装 App Installer 按钮 + 说明） | 非弹窗、不退出 |

**AOT 相关实现策略**：所有事件与状态用编译期 `x:Bind` + `INotifyPropertyChanged` 手写实现（不用 CommunityToolkit 反射式源？——Toolkit.Mvvm 的 `[ObservableProperty]` 源生成器是编译期生成，AOT 兼容，可选用）；列表数据用 `ObservableCollection` + 批量改判时整体重建集合（对应原版「批应用」）。

### Open Questions（已附默认裁定）

1. **打包形态**：MSIX 打包 vs 未打包自包含 exe？MSIX 有容器/安装器约束，原版用户习惯「双击即用的单文件」。
   **默认裁定**：未打包 + Windows App SDK self-contained + Native AOT，产出单 exe（可能附带少量自包含 DLL），保持免安装分发；不用 MSIX。
2. **组头吸顶**：WinUI 3 `ListView` 分组头无原生粘性（sticky）支持，PySide6 版是自绘 viewport 浮层实现的。
   **默认裁定**：P0 阶段交付标准分组头（不吸顶，功能等价、顺序一致），吸顶降级为 P1-1，用 ItemsRepeater + 自定义布局实现；不为了还原吸顶在 P0 引入复杂自绘。
3. **注册表 vs ApplicationData 设置存储**：WinUI 惯例是 `ApplicationData.LocalSettings`，但未打包应用此 API 依赖应用身份，且会丢老版本用户在注册表里的窗口/路径记忆。
   **默认裁定**：继续写 `HKCU\Software\Shibeng\WingetManager`（`Microsoft.Win32.Registry`，AOT 安全），保持与 Python 版数据互通。
4. **恢复 bat 的编码与运行时编码依赖**：原版用 GBK 写盘（cmd 代码页 936）；.NET 默认不含 GBK 编码表，需注册 `CodePagesEncodingProvider`。
   **默认裁定**：注册 CodePagesEncodingProvider（AOT 兼容），完全复刻 GBK 写盘与转义逻辑，产物字节级兼容。
5. **元数据核查的并发模型**：原版是「后台富化线程 + 与扫描共用唯一 winget 入口 + 让路/取消续传」。WinUI/async 下可做成 Task 队列 + 全局 CancellationTokenSource。
   **默认裁定**：用单一后台 Task + 顶层 CTS 复刻相同生命周期语义（扫描开始 → 取消核查；核查逐条写缓存；未完成 ID 下轮续查），不引入并行 show（避免主动撞 winget 互斥）。

### 明确不做

- 导入恢复界面及其全部代码（原 2.14.0 已移除，不迁移）。
- 托盘图标、系统托盘常驻（原版无此功能）。
- 关键词分类（分组只看 reinstallable + isDependency 两个布尔值）。
