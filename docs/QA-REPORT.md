# QA-REPORT · WingetManagerWpf（复刻 Sword6988/winget-manager v2.18.0，C# + WPF + .NET Framework 4.8）

- 测试人：Edward（QA Engineer）
- 日期：2025-11-14
- 被测代码基线：`WingetManagerWpf/WingetManagerWpf/`（14 个 XAML + 56 个 C#，约 13,700 行，含测试期内工程师追加的 `Assets/app.ico` 与图标接线）
- 对照标准：`_ref/`（Python 原版，golden reference，逐文件逐行比对）
- 原始数据：`docs/QA-REPORT-raw-data.md`（由 `tools/qa_verify.py` 自动生成）

---

## 1. 结论

**有条件可交付（Conditional GO）——逻辑层验证通过，UI 层有 3 处 MAJOR 需在真机冒烟时重点确认，无 BLOCKER。**

- **BLOCKER：0**。XAML 结构、x:Class/partial 配套、事件处理器、资源引用、csproj 双向配对、
  4.8 API 禁用清单、分层纪律、文案一致性（18 条抽查）全部通过。
- **MAJOR：3**（同一类问题，见 §4）：三个对话框 `WindowStyle="None"` + `SizeToContent="Height"` 组合。
  不会导致编译失败，但属于工程师自述的重点怀疑区，且是 WPF 上真实的布局抖动坑，需真机确认。
- **MINOR：18**（15 个未使用的设计令牌 + 2 个模板内重名 x:Name（已判定无害）+ 1 条 DispatcherTimer 提示）。
- 逻辑层 32 个自测用例（`tests/SelfTest/`）**因本机无编译器无法运行**；全部用例的期望值
  已先用原版 Python 实现验证，代码经逐行审查，建议拿到有 VS2022 的机器跑一次
  `msbuild SelfTest.csproj && bin\Debug\SelfTest.exe`（退出码 0 = 通过）。

---

## 2. 验证环境与手段（如实声明）

| 手段 | 可用性 | 说明 |
| --- | --- | --- |
| **编译运行 C#/WPF** | ❌ 不可用 | 本机沙箱无 MSBuild / .NET SDK / `csc`（安全策略禁用），**全部 C# 代码未编译、未运行** |
| **逻辑自测工程** | ✅ 已交付、未运行 | `tests/SelfTest/`（传统 csproj、net48、零 NuGet），32 用例，期望值经原版实现验证 |
| **静态检查脚本** | ✅ 已运行 | `tools/qa_verify.py`（纯标准库，10 大类检查，含括号配平状态机） |
| **与原版逐行比对** | ✅ 已完成 | `_ref/services/{winget_table,profiles,metadata,catalog,winget_cli}.py`、`_ref/core/fsio.py`、`_ref/qt_app.py` 关键段落逐一比对；抽查文案 18 条逐字 grep 原版行号 |
| **golden 样本验证** | ✅ 已运行 | 用原版 `parse_list_output` 验证了 4 组 `winget list` 样本（中文表头/英文表头/emoji/兜底）的期望输出，再写入自测断言 |

**明确声明：本报告的「代码审查通过」≠「编译通过」。** 任何结论都需要用户侧一次
VS2022 编译（F5）与 §6 冒烟清单确认。

---

## 3. 自测用例清单（32 个，对应原版行为均已注明）

状态图例：`代码审查通过` = 用例与原版行为比对无误、断言输入经 golden 验证；
`无法运行` = 本机无编译器。

| # | 用例（Program.cs） | 对应原版行为 | 状态 |
| --- | --- | --- | --- |
| T01 | DisplayWidth("名称")==4 / "Name"==4 / emoji==2 / 组合字符 0 | `winget_table.py:53 display_width` | 代码审查通过，无法运行 |
| T02 | 中文表头解析（`已找到` 噪音行、`-` 分隔线）→ 3 条记录字段正确 | `winget_table.py:100 parse_list_output` | 同上 |
| T03 | 英文表头（Name/Id/Version/Available/Source）解析 | `winget_table.py:18 _HEADER_LABELS` | 同上 |
| T04 | 表头缺失 → 兜底按 2+ 空格解析、从右认领字段 | `winget_table.py:140 _parse_by_gaps` | 同上 |
| T05 | 空输出 / null → 0 条、不抛 | `winget_table.py:100` | 同上 |
| T06 | 带 BOM UTF-8 解码后首行无 `\uFEFF` | `winget_cli.py:67 _decode`（utf-8-sig 优先） | 同上 |
| T07 | GBK(936) 字节解码 | `winget_cli.py:74-79`（回落本机编码/gbk） | 同上 |
| T08 | emoji 名称宽度=2、代理对不切半 | `winget_table.py:86-97 _split_row` | 同上 |
| T09 | IsReinstallable：winget/msstore → true；空/MSIX/ARP → false | `catalog.py:74 _is_reinstallable` | 同上 |
| T10 | IsDependencyKeyword：VCRedist/DotNet → true；Chrome → false | `catalog.py:36` 关键词表 + `:82` | 同上 |
| T11 | IdKindOf：`msix\`/`arp\`/其他反斜杠/普通 ID | `catalog.py:88 _id_kind` | 同上 |
| T12 | Groups 三组名称/顺序/归属判定 | `qt_app.py:1586-1590` 分组常量 | 同上 |
| T13 | NormalizeWingetVersion：`v1.30.100-preview`/`unknown`/`""` | `profiles.py:20` | 同上 |
| T14 | BuildManifest：$schema、**msstore 在前**、Packages 只有 ID、无 BOM | `profiles.py:26`（`sorted(by_source.items())`） | 同上 |
| T15 | BuildManifest 空选择 → Sources 空数组不抛 | `profiles.py:26` | 同上 |
| T16 | BuildRestoreBat：chcp 936 / --ignore-unavailable / %~dp0 / CRLF / 无孤立 \n | `profiles.py:95 _render_restore_bat` | 同上 |
| T17 | BuildRestoreBat：`%`→`%%`、basename 防穿越 | `profiles.py:83` | 同上 |
| T18 | BuildUninstallableList：排序/行格式/空集合文案 | `profiles.py:147` | 同上 |
| T19 | WriteTextAtomic 覆盖写、无 .tmp 残留 | `fsio.py:17 atomic_write_text` | 同上 |
| T20 | WriteJsonAtomic：无 BOM/2 空格/尾换行/中文不转义 | `fsio.py:40 atomic_write_json` | 同上 |
| T21 | JSON 序列化↔解析回环（嵌套/中文/引号/反斜杠/换行/bool/null） | `json.dumps(ensure_ascii=False)` | 同上 |
| T22 | JSON 解析容忍 BOM | `metadata.py:150 read_text` | 同上 |
| T23 | 非法 JSON 抛 BadRequestException（尾逗号也不容忍） | `core/errors.py BadRequest` | 同上 |
| T24 | SettingsStore 存取回环 + 损坏文件回退默认 | 原版 QSettings→JSON 等价语义 | 同上 |
| T25 | SelectionStore 严格布尔：`"false"`/`0`/`1` 均非 true；`"yes"`/`"1"` 为 true | `qt_app.py` isinstance(x,bool) 语义 | 同上 |
| T26 | SelectionStore 保存后是 `{"id": true}` 严格布尔、键小写 | `qt_app.py _save_selection` | 同上 |
| T27 | RuntimeCache：字符串 "true" 未命中、schema 不符回退空 | `metadata.py:131 RuntimeCache` | 同上 |
| T28 | MetadataProbe.Analyze：(true,"runtime")/(中文 true)/(null,null)/(false,null) | `metadata.py:91 analyze_runtime_signal` | 同上 |
| T29 | SearchQuery：id:/name: 限定、多词 AND、大小写不敏感、裸前缀回退 | `qt_app.py:742 _search_tokens` + `:758 _pkg_matches` | 同上 |
| T30 | ListPresenter.Rebuild：三组顺序、空组不产出、折叠组行不丢 | `qt_app.py` 分组渲染等价 | 同上 |
| T31 | MainViewModel 批量勾选作用域（有筛选只动可见项） | `qt_app.py:1521 _scoped_packages` | 同上 |
| T32 | NormalizeNewlines / 控制字符转义 | `fsio.py` newline="" + json.dumps | 同上 |

---

## 4. 问题清单

### BLOCKER（0）

无。以下高危点逐一排查通过：

- **XAML → InitializeComponent 生成链**：14 个 `.xaml` 全部可被 XML 解析；`x:Class` 与
  文件路径/命名空间一致；对应 `.xaml.cs` 均含 `partial class <同名>` 且命名空间匹配；
  事件处理器（Click/Loaded/ScrollChanged/StateChanged/MouseLeftButtonDown 等）在
  code-behind 中全部存在同名方法（`qa_verify.py` 检查项 A/B）。
- **WindowChrome 用法**（`Views/MainWindow.xaml:33-40`）：`WindowChrome.WindowChrome`
  元素结构正确（CaptionHeight=0 / ResizeBorderThickness=7 / GlassFrameThickness=0），
  且未与 `AllowsTransparency=True` 同用（正确避免了软件渲染掉帧路径）。
- **DropShadowEffect.Color**：所有 `DropShadowEffect`/`GradientStop` 的 `Color=` 引用的键
  （`Color.ShadowFx`、`Color.HeadTop/HeadBot`）均在 `Theme/Tokens.xaml` 以 `<Color x:Key>` 定义，
  没有 Brush 误用（`qa_verify.py` 检查项 XAML-COLOR-BRUSH，0 命中）。
- **x:Static**：`{x:Static core:Strings.WindowTitle}` 等引用的成员均为 `public const`，存在且类型匹配。
- **csproj 完整性**：磁盘 ↔ `<Compile>/<Page>/<ApplicationDefinition>` 双向配对零缺口；
  `<ApplicationIcon>Assets\app.ico` 与 `<Resource Include="Assets\app.ico">` 指向的文件
  存在且是合法 ICO（36094 字节，头 `00 00 01 00`）。
- **4.8 / C# 7.3 禁用清单**：`WaitForExitAsync`、`Kill(entireProcessTree)`、`ArgumentList`、
  `System.Text.Json`、`Span<>`、`ValueTask`、`IAsyncEnumerable`、`??=`、`record`、`using var`、
  switch 表达式、目标类型 `new()`、`init;`、Index/Range 运算符、`HttpClient` —— **全部 0 命中**
  （csproj 钉死 `<LangVersion>7.3</LangVersion>`，双保险）。
- **分层纪律**：Core/Models/Services 无任何 `using System.Windows*`；ViewModels 仅有
  `RelayCommand.cs` 的 `using System.Windows.Input`（ICommand，允许项）。
- **括号配平**：56 个 C# 文件经字符串/注释/字符字面量状态机检查，全部配平；
  `#if`、`<summary>` 配平；无 `???` 占位符；标识符无中文字符。
- **互斥退出码**：`CommandResult.cs:65` 用 `unchecked((int)0x8A150001)` 处理
  超出 int 范围的互斥码（原版 Python 无此坑，C# 正确处理了，值得肯定）。

### MAJOR（3，同一根因，建议合并处理）

| # | 位置 | 现象与证据 | 建议 |
| --- | --- | --- | --- |
| M-1 | `Views/AboutDialog.xaml:9-12` | `SizeToContent="Height"` + `WindowStyle="None"` + `AllowsTransparency="True"` 组合。WPF 上 SizeToContent 重测会先按旧内容排一次再量一次，无边框窗口首帧常出现高度闪动；叠加 AllowsTransparency 后窗口由 WPF 自绘，缩放过程可见。工程师自述怀疑区之一，静态检查确认属实 | 真机确认首帧是否抖动；若明显，改为固定 Height 或在 Loaded 前手动 `Height = 内容实际高度`（一次测量） |
| M-2 | `Views/ExportDialog.xaml:17` | 同上（`SizeToContent="Height"` + `WindowStyle="None"`） | 同上 |
| M-3 | `Views/WingetGuideDialog.xaml:12` | 同上 | 同上 |

### MINOR（18）

| # | 位置 | 现象 | 建议 |
| --- | --- | --- | --- |
| m-1 | `Theme/Tokens.xaml` / `Theme/Styles.xaml` | 15 个定义后从未引用的设计令牌（如 `Brush.HighlightBg` 曾误报，实际经 ThemeLookup 使用；剩余确认为未用：`Brush.Accent2`、`Brush.GradH1/2`、`Brush.HeadTop/HeadBot`、`Color.Accent/Accent2/AccentDeep/Bg`、`FontSize.Stat/Sub/Title`、`Space.Card/Page/Row`、`Text.Link` 等） | 保留亦可（作为调色板备用）；要清理需同步删引用文档，避免「改令牌没生效」的误解 |
| m-2 | `Theme/Styles.xaml`（10 处 `x:Name="Bd"`、2 处 `PART_Track`） | 同文件多个 ControlTemplate 内重名。**判定无害**：每个 ControlTemplate 是独立 XAML namescope，编译与运行不冲突 | 无需修改 |
| m-3 | `Views/MainWindow.xaml.cs:1312-1337 WaitForScanShutdown` | `Dispatcher.PushFrame` 消息泵在 Closing 期间会继续派发输入事件，理论上有重入窗口（用户在 3s 泵期间再点关闭/按钮）。`_closing` 守卫已覆盖主要路径（RebuildList/Flash），风险很低 | 真机按「扫描中关窗」验证一次；如有重入现象，改用 `Wait(TimeSpan)` + 后台轮询 |

---

## 5. 与原版行为差异核对表（14 项）

标准 = `_ref/`（Python 原版）。✅ = 行为等价；⚠️ = 有差异但已评估影响。

| # | 行为项 | 原版位置 | C# 位置 | 结论 |
| --- | --- | --- | --- | --- |
| 1 | `winget list` 表格解析（中文/英文表头、显示宽度切列、兜底、噪音行） | `winget_table.py:100/140` | `Services/WingetTableParser.cs` | ✅ 逐行等价；golden 样本（中文/英文/emoji/兜底 4 组）期望值一致 |
| 2 | 显示宽度：汉字 2 列、emoji 代理对整体 2、组合字符 0 | `winget_table.py:47-54` | `Core/EncodedText.cs:108-154` | ✅ 区段表覆盖 Unicode 12 常用 Wide/Fullwidth；组合字符判定与 `unicodedata.combining()` 等价（Mn/Me） |
| 3 | 输出解码顺序 utf-8-sig → utf-8 → 本机编码(936) → 容错 | `winget_cli.py:67-84 _decode` | `Core/EncodedText.cs:40-94 Decode` | ✅ 顺序一致；C# 中间档用 `Encoding.Default` 再显式 GBK，与 Python `locale.getpreferredencoding()` 等价 |
| 4 | 子进程命令与全局开关 | `winget_cli.py:106/226-249` | `Services/WingetRepository.cs:199 AppendGlobalFlags` | ✅ 子命令统一追加 `--disable-interactivity --accept-source-agreements`；`--version` 不追加；最终 argv 与原版一致 |
| 5 | 互斥冲突判定与重试（0x8A150001 / 空输出失败 / 退避计入总预算） | `winget_cli.py:32-64/93-122` | `CommandResult.cs:61` + `WingetRepository.cs:125-192` | ✅ 语义一致；C# 正确处理了 int 溢出（`unchecked((int)0x8A150001)`） |
| 6 | 重装判定 / 关键词表 / ID 形态 | `catalog.py:32-40/74-96` | `Services/PackageCatalog.cs:27-192` | ✅ 关键词 15 项、顺序、大小写匹配方式一致 |
| 7 | 目录去重与排序 | `catalog.py:170-179`（stable sort） | `PackageCatalog.cs:238-318` | ⚠️ 微差：同 ID 多行保留规则一致；同名称并列时 Python 靠稳定排序保持出现序，C# 追加了「ID 小写」兜底键使排序完全确定。无功能影响，结果更可复现 |
| 8 | 元数据只补漏不打假（缓存 True 覆盖、False 不否定、未命中等富化） | `catalog.py:105-109/152-168` + `metadata.py:131-181` | `PackageCatalog.cs:233-235` + `Services/RuntimeCache.cs` | ✅ 语义逐条一致（含 dep 严格 JSON bool、schema=1 校验、损坏回退空、Put 即落盘） |
| 9 | 运行库信号词与 (None,null) 未知语义 | `metadata.py:26-105` | `Services/MetadataProbe.cs:93-137` | ✅ 信号词 6 个逐字一致；「解析不出绝不当 false」语义一致 |
| 10 | winget import 清单（键序、msstore 在前、Packages 只写 ID、版本正则） | `profiles.py:16-60` | `Services/ExportProfiles.cs:37-117` | ✅ 键序 `$schema→CreationDate→Sources→WinGetVersion` 一致；`sorted(dict.items())` → msstore 在前一致；时间戳毫秒精度一致 |
| 11 | 恢复脚本（chcp 936、GBK 编盘、文案、% 转义、basename） | `profiles.py:73-144` | `ExportProfiles.cs:157-445` | ✅ 文案逐字一致（含 msstore 提示行）；`%`→`%%`、`Path.GetFileName` 与 `Path(json_name).name` 等价；GBK 编不进去时抛 BadRequest（原版为编码试抛，行为一致） |
| 12 | 无法自动重装清单 | `profiles.py:147-173` | `ExportProfiles.cs`（`BuildUninstallableHtml`） | ⚠️ **有意替换**（产品改进，非移植缺陷）：原版输出 txt（`· 名称 v版本` + `id [源]` 两行堆叠、不分节、`ARP`/`msix` 术语原样给用户）。现改为**单文件 HTML 待办页**：分节 + 类型标签 + 复选框（localStorage 记进度）+ 进度条 + 筛选/搜索 + 搜官网链接 + 打印样式。空集合文案「（没有这类软件，全部都能自动重装）」保留 |
| 13 | 原子写盘（同目录临时文件 → fsync → 覆盖；失败清理临时文件） | `fsio.py:17-42` | `Core/AtomicFile.cs` | ✅ 等价；C# 额外在 `File.Replace` 失败时退化拷贝（NTFS 正常路径仍是原子 Replace），bat 的 CRLF 由 `newline=""` 等价的直写保持 |
| 14 | 搜索语法（id:/name:/多词 AND/大小写不敏感/裸前缀回退）与批量勾选作用域 | `qt_app.py:742-770/1521-1558` | `ViewModels/SearchQuery.cs` + `MainViewModel.cs:831-958` | ✅ 语义一致：有筛选只作用可见可勾选项，无筛选作用于全部可重装包；flash 文案一致 |

另有两处**已声明的架构性差异**（非缺陷，PRD/架构文档明确允许）：

- ⚠️ 设置存储：原版 QSettings（注册表）→ 本版 `settings.json`（`Models/AppSettings.cs` 注释已声明）。
- ⚠️ 快照 machine.os 字段格式：原版 `platform.system()/release()/version()`，C# 为
  `Environment.OSVersion` 拼接（`PackageCatalog.cs:332-345`）——仅诊断信息展示，无功能影响。

---

## 6. 无法验证项与用户侧验证步骤

### 无法在本机验证（原因：无 MSBuild/.NET SDK，沙箱禁用 csc）

1. **整个工程能否编译**（最大未知项——静态检查全绿 ≠ 编译零错）；
2. 自测工程 `tests/SelfTest` 的 32 个用例实际运行结果；
3. 一切 XAML 运行时行为（资源解析、模板、绑定求值、布局）；
4. 真实 winget 子进程链路（ProcessRunner、互斥重试、取消）；
5. `SizeToContent` 对话框的实际观感（M-1/2/3）。

### 用户侧 VS2022 冒烟清单（F5 后按序执行，约 10 分钟）

1. **编译**：VS2022 打开 `WingetManagerWpf.sln`，确认 Debug|AnyCPU 直接 F5 零编译错误
   （若有 C# 8+ 语法报错，说明工程师违反了 7.3 约定——记录文件与行号回报）。
2. **首扫**：启动后应看到骨架屏 + 「正在扫描已安装软件…」，数秒后列表出现
   软件/运行库/winget 不支持 三组，顺序与分组与 Python 版一致。
3. **搜索**：搜索框输入 `chrome` → 只剩命中行；输入 `id:chrome` → 不匹配名称含 chrome 而 ID 不含的行；
   命中词应有黄色高亮底色；Esc 清空搜索。
4. **勾选与作用域**：搜 `chrome` 后 Ctrl+A → 底栏 flash「已全选当前 N 项」且 N 等于搜索结果数
   （屏幕外未勾）；清筛选后 Ctrl+Shift+A 全不选 → 只清可见集。勾选后重启程序，勾选状态恢复。
5. **统计卡与底栏**：四张统计卡数字 = 列表实际；底栏文案「N 个软件 + M 个运行库可一键重装 · …」
   与原版逐字一致；「仅看已选」开关后底栏出现「· 仅显示已勾选」。
6. **导出**：勾选若干（含至少 1 个 msstore 包）→ Ctrl+E → 保存到临时目录。检查三产物：
   `apps.json`（UTF-8 无 BOM、2 空格缩进、`Sources` 里 msstore 在前、`Packages` 只有
   `PackageIdentifier`）；`恢复-<stem>.bat`（GBK、双击先 chcp 936 且中文不乱码）；
   `无法自动重装清单-<stem>.html`（UTF-8 无 BOM、单文件、浏览器打开）。
7. **日志面板**：拖动高度（90~420 夹紧）、收起/展开、复制、另存、清空均可用；
   重启后高度与展开状态恢复。
8. **窗口几何**：拖动/缩放（四边 7px 热区）、双击标题栏最大化（内容四边不溢出 7px）、
   关闭重启后位置尺寸恢复；最大化关闭重启直接最大化。
9. **三个对话框**（重点观察 M-1/2/3）：关于 / 导出 / winget 引导——确认无边框窗口
   **首次弹出时高度是否闪动**，圆角阴影是否正常；关于对话框的图标是否正常显示
   （验证 `Assets/app.ico` pack URI）。
10. **异常路径**：临时把 `WPM_WINGET_EXE` 指到不存在路径后启动 → 应弹安装引导而不是崩溃；
    扫描进行中点「取消扫描」→ 状态回 Ready/Cancelled 且无孤儿 winget 进程
    （任务管理器确认）；扫描中直接关窗 → 进程正常退出、无残留 winget。

---

## 7. 回归验证（第二轮 · 定向，针对 3 MAJOR + 3 MINOR 修复）

验证范围：仅改动面（`Controls/DialogSizer.cs`、三个对话框 XAML/.cs、`MainWindow.xaml.cs`
的 `WaitForScanShutdown`、`MainWindow.xaml` 标题字号、`Tokens/Styles` 注释、docs），
另重跑全量静态检查（`qa_verify.py`）确认无回归：**BLOCKER=0，MAJOR=0，MINOR=17**
（较上轮少 1：`FontSize.Title` 令牌随 MainWindow.xaml:87 的接线被引用）。

### A. FitToContent 与 AllowsTransparency 的耦合

- **结论：正确。** 三个对话框全部是 `AllowsTransparency="True"`：
  `AboutDialog.xaml:12`、`ExportDialog.xaml:20`、`WingetGuideDialog.xaml:15`。
  `DialogSizer.FitToContent`（`Controls/DialogSizer.cs:48-58`）直接把
  `window.Height` 设为 `root.DesiredSize.Height`、**不减**边框/标题栏——
  在透明无边框窗口上客户区=整窗，这正是正确做法；三个窗口均无例外，不存在
  「某个窗口非透明导致多出 30px」的漏点。
- 附带核验：三个 XAML 均给了初值 Height（430/400/380），因此 `DialogSizer.cs:55`
  的 `Math.Abs(window.Height - height)` 不会遇到 NaN 陷阱；`Loaded` 均已接线
  （`AboutDialog.xaml.cs:43`、`ExportDialog.xaml.cs:106`、`WingetGuideDialog.xaml.cs:35`）。

### B. 多显示器/负坐标下的居中

- **结论：正确。** `CenterOnOwner`（`DialogSizer.cs:103-130`）以 **Owner** 为基准居中
  （`owner.Left/Top + (owner.ActualWidth - width)/2`），坐标系自动跟随副屏；
  屏外夹取用的是 `SystemParameters.VirtualScreenLeft/Top/Width/Height`
  （`DialogSizer.cs:107-110`），**不是** `PrimaryScreen*`，负坐标（副屏在主屏左侧）安全。
- 已知边角（MINOR，不阻塞）：无 Owner 时（`AboutDialog.ShowAbout` 的 CenterScreen 分支，
  `AboutDialog.xaml.cs:56-57`）`CenterOnOwner` 直接返回（`DialogSizer.cs:82-86`），
  改高后窗口保持 Top 不动、向下微长几像素——仅外观，首帧尺寸已量准，影响可忽略。

### C. ShowSuccess 重测高度时的可见性顺序

- **结论：正确。** `ExportDialog.xaml.cs:341-379`：先 `FormPage.Visibility=Collapsed`
  （:342）→ `SuccessPage.Visible`（:343）→ 填标题与文件行（:345 `BuildFileRows`）→
  设置 BatTip/TxtTip 可见性（:365-366）→ **最后**才 `FitToContent`（:379）。
  `Visibility=Collapsed` 的元素不参与 Measure，不会量到两页叠加高度；
  文件行在测量前已构建，成功页高度一次到位。

### D. WaitForScanShutdown 守卫与复位

- **结论：正确。** `_waitingForScanShutdown` 声明于 `MainWindow.xaml.cs:79`；
  早退守卫在 :1326，置位在 :1337（**先**判空/判完成返回、**再**置位，不会误拦后续关闭），
  `try/finally` 在 :1360 复位 —— **所有返回路径（含 `PushFrame` 抛异常）都会复位**。
- 超时常量 `ScanShutdownWaitMs = 2000`（:76，原 3000 已改）。
- `CurrentScanTask` 为 `MainViewModel.cs:109` 的公开属性，扫描启动时赋值（:391）、
  从不置 null；为 null 时走 :1331-1329 的早退，不进泵，引用安全。

### E. 新增/修改文件的新编译风险

- **结论：未发现新风险。** 全量静态检查 0 BLOCKER / 0 MAJOR：
  - `Controls/DialogSizer.cs` 已进 csproj（`WingetManagerWpf.csproj:173`）；
  - 三个对话框 code-behind 均 `using WingetManagerWpf.Controls;`
    （`AboutDialog.xaml.cs:5`、`ExportDialog.xaml.cs:10`、`WingetGuideDialog.xaml.cs:5`）；
  - XAML `x:Name="RootBorder"` 与 code-behind 字段同名同命名空间（检查项 XAML-XCLASS/
    XAML-PARTIAL/EVENT 全过）；
  - `MainWindow.xaml:87` 的 `TextFontSize="{StaticResource FontSize.Title}"`：
    `FontSize.Title` 是 `sys:Double` 令牌（`Tokens.xaml:116`），与 `GradientText.TextFontSize`
    的 double 类型匹配（静态检查 RES-UNRESOLVED 0 命中）；
  - 事件处理器、括号配平、4.8 API 禁用清单在新文件上同样全部通过。

### 第二轮新增问题

无 BLOCKER / MAJOR。仅 1 条新记录的 MINOR（B 节边角：无 Owner 时改高不重新居中），
与上轮遗留 17 条 MINOR 合计不构成交付阻塞。

**回归结论：GO。**

## 8. 交付物清单

| 交付物 | 路径 | 状态 |
| --- | --- | --- |
| 自测工程（32 用例） | `tests/SelfTest/SelfTest.csproj`、`Program.cs`、`run-selftest.bat` | ✅ 已交付（后于第三轮实跑通过，见 §9） |
| 自测说明 | `tests/README.md` | ✅ |
| 静态检查脚本 | `tools/qa_verify.py` | ✅ 已运行（BLOCKER=0 / MAJOR=3 / MINOR=18） |
| 样本生成与 golden 验证脚本 | 开发机 `.workbuddy/checks/gen_cs_literals.py` 等（未随仓库发布） | ✅ 已运行 |
| 原始数据 | `docs/QA-REPORT-raw-data.md` | ✅ 脚本自动生成 |
| 本报告 | `docs/QA-REPORT.md` | ✅ |

---

## 9. 编译与自测实跑（第三轮 · 补做，结论已升级）

前面第 2 节声明过「本机无编译器，未编译」。交付后补齐了离线构建工具链
（系统 MSBuild 4.0 + Roslyn 4.8 + 官方 net48 参考程序集，详见
`ARCHITECTURE.md` §11.7），因此这一节是**真正编译并运行后的结果**，取代此前的推断。

### 9.1 编译结果

| 项 | 结果 |
|---|---|
| 编译命令 | `build-release.bat`（离线工具链，Release\|AnyCPU） |
| 错误 | **0** |
| 警告 | 3 条 `CS0618`（`FormattedText` 旧构造重载，`GradientText`/`CountPill`/`StatCard`）+ 1 条 `MSB3270`（离线模式固有，脚本已压制） |
| 产物 | `dist\WingetManager.exe`，**291,328 字节**，目录内唯一文件 |
| 静态校验 | PE 头合法、Subsystem=GUI(2)、.NET 元数据（`BSJB`）、manifest 含 `requestedExecutionLevel` / `PerMonitorV2` / `longPathAware`、`app.ico` 资源、**14 个 BAML 全部存在**（`views/mainwindow.baml`、`theme/tokens.baml`、`app.baml` …） |

### 9.2 编译暴露的 3 个真实缺陷（此前静态审查未发现）

| # | 位置 | 错误 | 修法 |
|---|---|---|---|
| 1 | `Controls/PackageRowControl.xaml.cs` | `CS0114` + `CS0123`：处理器与 `UIElement.OnMouseLeftButtonUp` 同名同签名 | 改名 `OnRowMouseLeftButtonUp(object, MouseButtonEventArgs)` |
| 2 | `Services/MetadataProbe.cs:261` | `CS1503`：`out bool?` 无法传给 `out bool` | 声明改为 `bool` |
| 3 | `Views/MainWindow.xaml.cs:1342` | `CS0103`：缺 `using System.Threading` | 补 using |

### 9.3 自测实跑结果

```
tests\SelfTest\build-and-run.bat
总计: 427 项断言, 通过 427, 失败 0
结果: PASS        exit code = 0
```

首次跑出现 3 条失败，逐条对照原版后确认**全部是用例期望值写错，不是产品缺陷**，已修正用例：

1. `name:google chrome` 被当成「两个词都限定 name」。原版 `qt_app.py:747` 的 `name:` 只作用于紧跟的那一个词，
   第二词是 any 词 → `Google Chrome 32` 本应命中。改为断言「命中」，并另加 `name:google name:32` 验证真正的 AND。
2. SelectionStore 保存用例期望串漏了 `.id`（实际输出 `{"a.id": true}`，与原版 `_save_selection` 一致）。
3. 批量作用域用例在「运行库分组全不选」前少了一步重新全勾，导致后续反选断言基于错误初态。

### 9.4 结论修订

第 1 节的「有条件可交付」**升级为可交付**：逻辑层已实际编译 + 427 项断言全通过，
`dist\WingetManager.exe` 可直接分发。仍建议人工过一眼的只剩纯视觉项
（对话框首帧高度、无边框窗口最大化留边、字体回退观感），这些静态与自动化手段都无法替代肉眼。

---

## 10. 第四轮 · 全量代码审计与修复（当前基线）

**本节数字取代 §9 的旧快照**（产物大小、警告条数、MINOR 数量均已变化，原因见下表）。

审计范围：`Core/`、`Services/`、`Models/`、`ViewModels/`、`Views/`、`Controls/`、`Theme/`、
构建脚本、`.gitignore`、全部 `docs/`，约 19,000 行。方法：通读 + 逐类交叉核对 +
`tools/qa_verify.py` 静态检查 + 实机编译 / 自测循环验证。

### 10.1 已修复缺陷（按优先级）

| # | 级别 | 位置 | 问题 | 修法 |
|---|---|---|---|---|
| 1 | **P0 数据丢失** | `build-release.bat` | 每次重建都 `rd /s /q dist`，而便携模式把用户数据放在 `dist\Data`（`selection.json` 勾选状态、`metadata-cache.json`、`logs\app.log`）——**一次重新构建即静默清空全部勾选** | 改为只删 `WingetManager.exe` / `*.pdb` / `*.config` / `*.dll`，`dist\Data` 与 `dist\` 本身不再被删 |
| 2 | P1 功能失效 | `Views/MainWindow.V3.cs` `OpenDataDir` | 路径作为**整条命令行**传给 `explorer.exe` 且未加引号，`Program Files`、含空格用户名下参数被拆成两个，打开的是错误位置 | 改用 `ProcessStartInfo`（自动按参数引用），并先 `AppPaths.EnsureDataDir()` 再打开 |
| 3 | P1 崩溃 | `Core/Json/JsonReader.cs` | 无嵌套深度上限，畸形或恶意 JSON（如 10 万个 `[`）触发 `StackOverflowException`——.NET 中**不可捕获**，进程直接消失且不留日志 | 新增 `MaxDepth = 64`，超限抛 `BadRequestException`；`finally` 保证计数器回滚 |
| 4 | P1 静默失败 | `ViewModels/MainViewModel.cs` | `MetadataProbe.EnrichAsync(...)` 返回的 Task 被丢弃，后台异常无人观察，元数据核查失败时界面无任何提示 | 加 `OnlyOnFaulted` 续延，写入日志「元数据核查在后台异常退出（本次结果可能不完整）」 |
| 5 | P1 并发 | `ViewModels/MainViewModel.cs` | `OnMetadataBatch` 释放的是**字段当前指向**的 CTS：上一轮扫描的迟到回调会 `Dispose` 掉新一轮的令牌，该轮从此无法取消 | 每轮持有自己的 `CancellationTokenSource` 并闭包捕获；收尾时仅在 `ReferenceEquals(_enrichCts, owner)` 成立时清字段，然后只释放自己那一个 |
| 6 | P2 乱码 | `Core/EncodedText.cs` | 开启「Beta: 使用 UTF-8 提供全球语言支持」时 `Encoding.Default` 是 cp65001，`GetString` 不抛异常而是返回 `U+FFFD`，旧逻辑据此**提前返回乱码**，跳过了真正的 GBK 兜底 | 第 ② 步加 `!ContainsReplacementChar(text)` 守卫；新增公开方法 `ContainsReplacementChar` |
| 7 | P2 高 DPI 裁字 | `Controls/CountPill.cs`、`GradientText.cs`、`StatCard.cs` | 三处用 `FormattedText` 的 6 参数过时重载（CS0618），隐含 `pixelsPerDip = 1.0`，在 125% / 150% 缩放下按错误尺寸测量 → 文字被裁切。此前被当成「无害的历史警告」 | 改用 7 参数重载 + 新增 `ThemeLookup.GetPixelsPerDip(visual)`（`VisualTreeHelper.GetDpi`，未入树时回退 `1.0`）。构建现为 **0 warning** |
| 8 | P2 文档矛盾 | `Core/AppPaths.cs` | `IsPortable` 的 XML 注释写「默认关闭」，类注释与代码都是「默认开启」 | 注释改为「默认开启（v3.6 起）」并补 `%LOCALAPPDATA%` 回退与 `MigrateLegacyDataIfNeeded` 语义 |
| 9 | P2 构建脚本分叉 | `tests/build-and-run.bat` | 与 `tests/SelfTest/build-and-run.bat` 是两份各自演化的落本，VS 查找列表不同、注释里的断言数还停在 267；跑错那个会得到不同工具链解析结果 | 根目录改为**转发垫片**，只 `call` 真正的脚本，保持单一事实来源 |
| 10 | P2 脚本不可移植 | `tools/qa_verify.py` | 检查脚本只存在于开发机 `.workbuddy/checks/`，且内部硬编码 `D:\...\_ref` 与固定三级 `BASE` 推导，别人 clone 后无法运行 | 脚本入库到 `tools/`，改为向上查找 `WingetManagerWpf/WingetManagerWpf/WingetManagerWpf.csproj` 定位工程根；删掉从未被使用的 `REF` 常量。开发机原路径保留为薄壳转发 |
| 11 | P2 配置 | `.gitignore` | 缺 `.tmp-preview/`、`dist-Data-backup-*/`、`dumps/`、`tests/_dump/`、`*.tmp`——这些目录含**真实勾选与包清单**，极易误提交 | 补齐，并注明「备份里含真实勾选与包清单」 |
| 12 | P2 文档漂移 | `README.md`、`docs/*`、`tests/README.md` | `WPM_META_TTL_DAYS` 未记录；「点右下角导出配置」在 v3 已改为底部浮动条；便携模式仍写「默认关闭」；产物大小 285 KB / 断言数 267 / 「已知 3 条警告」全部过时；`.workbuddy/checks/qa_verify.py` 在仓库里根本不存在 | 逐条对齐实际行为 |

### 10.2 复核后判定「不改」的项

- **37 条 `RES-UNUSED` 主题令牌**：确认是**故意保留**——全仓库不存在动态拼接资源键的写法
  （`grep '"Brush\." *+'` 无命中），删掉不会影响运行，但会破坏 `docs/UI-DESIGN.md` 里
  「所有旧令牌键名保留」的 v3 令牌契约。保留。
- **2 条 `XAML-DUPNAME`**：`x:Name="Bd"` 出现 15 次、`PART_Track` 2 次，全部位于
  `ControlTemplate` 内，各自独立 namescope，不构成冲突。保留。
- 逐项复核确认正确、未改动：`AtomicFile`（同目录临时文件 → `Flush(true)` → `File.Replace`，
  带 `File.Copy` 回退）、`ProcessRunner`（双管道并发读，规避 4 KB 缓冲死锁；`QuoteArgument`
  符合 MSVC 规则；`KillTree` 分级升级）、`SelectionStore`（严格布尔，拒绝 `"false"` / `0`）、
  `RuntimeCache`（TTL 含当日语义）、`WingetTableParser`（显示宽度切列 + `ParseByGaps` 兜底）、
  `JsonWriter`（`\u2028`/`\u2029`/控制字符转义）、`app.manifest`（`asInvoker` +
  PerMonitorV2 + `longPathAware`）、窗口位置恢复（已按虚拟屏幕边界裁剪）、
  csproj 的 `<Compile>`/`<Page>` 与磁盘完全同步。产品代码中无 `async void`、
  无硬编码盘符、无 TODO/FIXME。

### 10.3 本轮验证结果

| 项 | 结果 |
|---|---|
| `build-release.bat` | **0 error CS / 0 warning CS**（上一轮为 3 warning，已随 #7 修复归零） |
| 产物 | `dist\WingetManager.exe`，**339,968 字节**，SHA256 `9c32cb250b6136dbbe1659ff24bede74d2022c643334408f2e42303cbef7776c` |
| `tests\SelfTest\build-and-run.bat` | `总计: 427 项断言, 通过 427, 失败 0` / `结果: PASS` |
| `tools/qa_verify.py` | `BLOCKER=0  MAJOR=0  MINOR=39`（= 37 条 RES-UNUSED + 2 条 XAML-DUPNAME，即 §10.2 判定保留项） |
| P0 回归实证 | 先把 `dist\Data` 备份恢复回来，再跑一次 `build-release.bat`，`metadata-cache.json` / `selection.json` / `settings.json` / `logs\app.log` **全部存活**；修复前同样的操作会整目录清空 |
| 新增回归哨兵 | 自测新增 T38（JSON 深度上限：64 层通过、65 层抛错、10 万字符未闭合 `[` 不栈溢出、`_depth` 跨两次解析正确回滚）、T39（`ContainsReplacementChar` + GBK 往返 + BOM 剥离 + 空数组）、T40（断言 `build-release.bat` 不再含 `rd /s /q "%DIST%"`），共 +18 条断言 |

**结论：可交付。** 逻辑层编译零错零警、427 项断言全通过、静态检查无 BLOCKER/MAJOR；
剩余 39 条 MINOR 均已逐条判定为「有意保留」或「模板作用域所致」。唯一无法自动化的
仍是纯视觉项（对话框首帧高度、无边框最大化留边、字体回退观感）与真实机器上的
`winget` 端到端冒烟，见 §6 清单。

### 10.4 第五轮 · CI 在干净环境里抓到的解码缺陷（已修）

代码推上 GitHub 后，`build-singlefile` 工作流在「Run self-test」这一步失败，
**425 / 427**，两条失败都指向同一件事：

| 用例 | 期望 | 实际 |
|---|---|---|
| T07 GBK 字节解码为原文 | `名称版本 winget` | `Ãû³Æ°æ±¾ winget` |
| T39 GBK 字节解码结果正确 | `运行库 中文测试` | `ÔËÐÐ¿â ÖÐÎÄ²âÊÔ` |

**根因**：GitHub 的 `windows-latest` 是 en-US 系统，`Encoding.Default` 是 **cp1252 —— 单字节
代码页**。单字节页对任何字节都有映射，**永远不会产出 U+FFFD**，所以 §10.1 #6 加的那道
「替换字符守卫」在这里完全拦不住（它只对 65001 有效）。GBK 字节被「成功」解成一串拉丁
乱码后函数直接 return，排在后面的 936 兜底永远轮不到。

**为什么本机全绿**：中文 Windows 的 `Encoding.Default` 恒为 936，这条分支根本走不到。
这是典型的「本机能过、干净环境能炸」，也正是把自测放进 CI 的价值——它补上了本机
不可能覆盖的那一半输入空间。

**修法**（`Core/EncodedText.cs`）：

1. 新增**严格 GBK** 解码器（`DecoderFallback.ExceptionFallback`）。原来的宽容解码器遇到
   非法字节只吐 U+FFFD 不抛，无法用来判断「这段字节到底是不是 GBK」——不判就无法
   决定该不该把解码权交回 ANSI。
2. ANSI 代码页是单字节页（1252/437/850…）或 65001 时，把严格 GBK 提到 ANSI **之前**。
   这两类页面根本装不下汉字，先试 GBK 只会更好；936/932/949/950/54936 等双字节页
   保持原有顺序，不动既有行为。
3. 新增 `Decode(byte[] raw, Encoding ansi)` 重载 —— 让中文机器上的自测能显式重放
   1252 / 65001 分支。没有它，这条路径永远只能在 CI 上暴露一次。
4. 反向约束：`café résumé` 这类真正的 cp1252 文本不能被 GBK 抢走。严格 GBK 在非法
   尾字节（如 `0xE9` 后跟 `0x20`）上会抛，天然能区分，已写成断言。

**当前基线（取代 §10.3 对应两行）**：自测 **427 → 432** 项断言、全通过；
exe 339,968 字节，SHA256 `3a7b183c58d4959ff89c17d1a99c0a004c894c2030a2581cf594b203da82daff`。

**教训**：凡是「读本机配置决定行为」的分支（代码页、区域设置、DPI、屏幕尺寸、
文件系统大小写敏感性），都必须能在测试里**显式注入另一套配置**，否则测试只覆盖
开发机那一种现实。这次的口子就是 `Encoding.Default` 被直接读死。
