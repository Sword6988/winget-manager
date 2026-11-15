# QA 自测工程（SelfTest）

针对 `WingetManagerWpf` 的**逻辑层自测**：把被测工程的 `Core/`、`Models/`、`Services/`、
`ViewModels/`（外加 `Views/ListPresenter.cs`，它刻意不引用 WPF 类型）以 `<Compile Link>`
方式编进一个控制台程序，用手写断言验证与原版 Python 参考实现（`_ref/`）行为一致。

## 为什么这样设计

- **零 NuGet、传统 csproj、net48**：不引入任何测试框架，只引用 `System` / `System.Core`，
  在任何装了 VS2022（或 Build Tools，含 .NET 桌面开发工作负载）的机器上都能直接 msbuild。
- **不含 UI**：被测源码的 `Views/*.xaml`、`Controls/*.xaml`、`App.xaml.cs` 不参与编译，
  因此断言全部跑在无界面环境（CI / 沙箱）里。
- **golden 对照**：所有解析类用例（winget list 表格样本）都先用原版
  `_ref/services/winget_table.py` 的 `parse_list_output` 生成并验证过期望值，
  再硬编码进 `Program.cs`——断言的「期望值」就是原版行为，不是 QA 凭想象写的。
- **假仓储层**：T34/T35/T36 需要跑真实的 `PackageCatalog.BuildSnapshotAsync`（计数口径、
  日志文案、TTL 联动都在私有方法里），因此用 `Program.cs` 内的 `FakeWingetRepository`
  回放一段固定的 `winget list` 输出——**不启动任何子进程**，但走的是产品真实代码路径。

## 如何编译运行

**最省事的方式**（不需要装 VS）：

```bat
build-and-run.bat
```

这个脚本按顺序找两套工具链：VS 的 MSBuild → 系统自带 MSBuild 4.0 + `..\tools\build-deps`
里的 Roslyn 4.8 与 net48 参考程序集。编译成功后自动运行，退出码 0 = 全部通过。

> **实跑记录（2026-11-15 全面审计后）**：离线工具链跑通，
> `总计: 445 项断言, 通过 445, 失败 0`，退出码 0。
> （历史上首次运行曾出现 3 条失败，逐条对照 `_ref/` 后确认是**用例期望值写错**，不是产品缺陷，已修正：
> ① `name:` 前缀只作用于紧跟的那一个词；② 保存用例期望串漏了 `.id`；③ 批量作用域用例缺一步重新全勾。）
>
> **注意**：上面 445 项已是**当前** `Program.cs` 的实跑结果，包括 T27b、T33~T37（TTL / 计数口径 /
> 关键词补漏 / 配置项）与 T38~T41（审计与 UI 回归锁：JSON 嵌套上限、解码 U+FFFD 识别、
> 打包脚本不删 `dist\Data`、窗口按钮字形矢量化）。本机可用离线工具链直接跑（`build-and-run.bat`），
> 不再需要 VS2022。

**手动方式**（装了 VS 的话）：

1. 打开 **Developer Command Prompt for VS2022**；
2. `cd /d WingetManagerWpf\tests\SelfTest`
3. `msbuild SelfTest.csproj /p:Configuration=Debug /nologo /v:m`
4. `bin\Debug\SelfTest.exe`
5. 检查输出末尾 `结果: PASS`；**退出码 0 = 全部通过，1 = 有失败断言**。

## 用例清单（38 个用例，均在 Program.cs 中注明对应原版行为）

| # | 用例 | 对应原版行为 |
| --- | --- | --- |
| T01 | DisplayWidth 中文/ASCII/空串/emoji/组合字符 | `_ref/services/winget_table.py:53 display_width` |
| T02 | 中文表头 winget list 解析（噪音行、`-` 分隔线、可用列） | `winget_table.py:100 parse_list_output` |
| T03 | 英文表头（Name/Id/Version/Available/Source）解析 | `winget_table.py:18 _HEADER_LABELS` |
| T04 | 表头缺失 → 按 2+ 空格兜底解析 | `winget_table.py:140 _parse_by_gaps` |
| T05 | 空输出 / null → 0 条且不抛 | `winget_table.py:100` |
| T06 | 带 BOM 的 UTF-8 解码后无 `\uFEFF` | `winget_cli.py` 输出解码（utf-8-sig 优先） |
| T07 | GBK(936) 字节解码 | `winget_cli.py`（cmd 代码页 936） |
| T08 | emoji 名称宽度=2 且代理对不被切半 | `winget_table.py` display_width + _split_row |
| T09 | IsReinstallable 只看源列 winget/msstore | `_ref/services/catalog.py:74 _is_reinstallable` |
| T10 | IsDependencyKeyword（19 词逐个命中 + 原版 15 词顺序不变 + 不误判） | `catalog.py:36` 关键词表 + `catalog.py:82` |
| T11 | IdKindOf：msix\ / arp\ / 其他反斜杠 / 普通 ID | `catalog.py:88 _id_kind` |
| T12 | Groups 三分组名称/顺序/归属 | `qt_app.py:1586-1590` 分组常量 |
| T13 | NormalizeWingetVersion（v 前缀 / unknown / 空） | `_ref/services/profiles.py:20` |
| T14 | BuildManifest：$schema、msstore 在前、Packages 只有 ID、WinGetVersion 数字开头 | `profiles.py:26 build_winget_manifest` |
| T15 | BuildManifest 空选择 → Sources 空数组不抛 | `profiles.py:26` |
| T16 | BuildRestoreBat：chcp 936 / --ignore-unavailable / %~dp0 / CRLF / 无孤立 \n | `profiles.py:95 _render_restore_bat` |
| T17 | BuildRestoreBat % 双写、basename 防目录穿越 | `profiles.py:83` |
| T18 | BuildUninstallableHtml：结构（DOCTYPE/meta charset/统计卡/筛选/搜索）、分节与计数、勾选 key、搜官网链接、排序、空集合 | 有意替换原版 `profiles.py:147`（清单改单文件 HTML） |
| T18b | HTML 清单：三类类型标签（商店应用 / 无独立安装包 / 源不可用）、说明文案、`& < >` 转义、内部路径不作为可见文本 | 同上 |
| T19 | WriteTextAtomic 覆盖写、无 .tmp 残留 | `_ref/core/fsio.py:17 atomic_write_text` |
| T20 | WriteJsonAtomic：无 BOM / 2 空格缩进 / 尾换行 / 中文不转义 | `fsio.py:40 atomic_write_json` |
| T21 | JSON 序列化↔解析回环（嵌套/中文/引号/反斜杠/换行/bool/null） | `json.dumps(ensure_ascii=False)` 等价 |
| T22 | JSON 解析容忍 BOM | `metadata.py:150`（read_text utf-8 留 BOM 的老文件） |
| T23 | 非法 JSON 抛 BadRequestException（含尾逗号不容忍） | `_ref/core/errors.py BadRequest` |
| T24 | SettingsStore 存取回环 + 损坏文件回退默认 | 原版 QSettings→JSON 等价语义 |
| T25 | SelectionStore 严格布尔（"false"/0/1 不是 true；"yes"/"1" 是） | `qt_app.py` isinstance(x,bool) + 老文件兼容 |
| T26 | SelectionStore 保存后为 `{"id": true}` 严格布尔 | `qt_app.py _save_selection` |
| T27 | RuntimeCache：严格 bool、schema 校验、损坏回退空 | `_ref/services/metadata.py:131 RuntimeCache` |
| T27b | RuntimeCache TTL：默认 90 天过期 / `0`=永不过期 / 坏时间戳当过期 / 未来时间算新鲜 / 过期不删条目不落盘 / `Put` 刷新解除过期 | 本轮改造 1（`WPM_META_TTL_DAYS`） |
| T28 | MetadataProbe.Analyze 四种判定（true/false/null/null） | `metadata.py:91 analyze_runtime_signal` |
| T29 | SearchQuery：id:/name: 限定、多词 AND、大小写不敏感 | `qt_app.py:742 _search_tokens` + `:758 _pkg_matches` |
| T30 | ListPresenter.Rebuild：三组顺序/空组不产出/折叠不丢行 | `qt_app.py` 分组渲染等价 |
| T31 | MainViewModel 批量勾选作用域（有筛选只动可见项；无筛选动全部可重装包） | `qt_app.py:1521 _scoped_packages` + `:1542` 注释语义 |
| T32 | NormalizeNewlines / JsonWriter 控制字符转义 | `fsio.py` newline="" 保 CRLF + json.dumps 转义 |
| T33 | CatalogCounts 双口径：`dependencies` / `dependencies_all` 两键都在、`all >= dep`、Counts=null 兜底 | 本轮改造 2（`docs/ARCHITECTURE.md` §CatalogCounts） |
| T34 | 计数口径端到端（假仓储回放 `winget list`）：1 可重装运行库 + 1 不可重装运行库 + 1 软件 → `Dependencies=1` / `DependenciesAll=2` / `Unsupported=1`，且等于分组行数 | 本轮改造 2（`PackageCatalog.BuildSnapshotAsync`） |
| T35 | 关键词命中诊断日志：次数降序 + 同次数词名字母序；无命中词文案原样；命中行数按 raw 行累加（同 ID 两行计 2 行） | 本轮改造 3（`FormatTopKeywordHits`） |
| T36 | TTL 与 PendingMetadataIds 联动：过期条目会重新核查，`ttl=0` 时老结论仍被采信 | 本轮改造 1 + `PendingMetadataIds` |
| T37 | AppConfig 元数据 TTL：四参构造兼容（默认 90）、五参、`CreateDefault`、`WPM_META_TTL_DAYS` 0~3650 与非法值抛 `ConfigInvalidException` | 本轮改造 1（`AppConfig.Load`） |

样本生成与验证脚本：开发机上的 `.workbuddy/checks/gen_cs_literals.py`（未随仓库发布；用原版解析器
验证期望值后输出 C# 字面量，`Program.cs` 中前四个解析样例常量即来源于此）。T33~T36 另外三份
`WingetListCounts` / `WingetListPlain` / `WingetListDuplicate` 是为计数口径新造的小样本，
不带 `-` 分隔线、走 `ParseByGaps` 兜底路径，期望解析结果同样按兜底规则逐行推演过。

## 不测什么

- `Core/ProcessRunner.cs`、`Core/SingleInstance.cs`：被编译进工程（无 UI 依赖，能通过编译
  本身就是一种检查），但**不运行**——需要真实子进程 / 命名管道 / Mutex / 桌面会话。
- WPF 层的**交互行为**（拖拽、动画、真实 DPI 缩放、多显示器）：工程可以离线编译（见
  `tests/SelfTest/build-and-run.bat`），但没有桌面会话可驱动 UI 自动化，因此这一层由
  `tools/qa_verify.py` 静态检查 + `docs/QA-REPORT.md` §6 的人工冒烟清单覆盖。

## 已知局限

1. 自测工程与主工程都能在本机离线编译（`build-and-run.bat` / `build-release.bat`），
   但本套件只覆盖**纯逻辑层**；WPF 层仍靠静态检查与人眼冒烟。
2. T31 用反射注入 `_packages`（私有字段）；若后续重构改字段名，该用例会失败——
   这是「防止行为悄悄变化」的哨兵，属预期。
3. GBK 解码用例（T07/T16/T17）依赖机器装有代码页 936（中文 Windows 必有）。
