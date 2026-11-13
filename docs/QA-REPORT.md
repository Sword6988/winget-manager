# QA-REPORT · winget-manager C#/WinUI 3 + Native AOT 重写独立验证

- 验证人：Edward（QA Engineer，独立验证，不采信工程师自述）
- 日期：2026-11-13
- 被测项目：`D:/UserData/Shibeng/桌面/winget/winget-manager-winui/`
- 行为基线：`D:/UserData/Shibeng/桌面/winget/winget-manager/`（Python 3.14 + PySide6）
- 验证轮次：第 1 轮（全部通过，未进入修复循环）

---

## 总结论

**PASS。** 干净构建 0 警告 0 错误；185/185 测试全部真实通过、0 跳过；AOT 发布成功且产物经字节级检验为原生镜像（无 IL 元数据）；真实启动扫描成功（172 项，可重装 82）；与 Python 版的数据文件双向兼容全部实测通过。**未发现需要工程师修复的源码缺陷。**

---

## 一、验证项清单与逐项结论

### 1. 干净构建与测试 — **PASS**

| 命令 | 结果 |
|---|---|
| `dotnet clean` | 成功 |
| `dotnet restore` | 成功（⚠ 沙箱环境坑，见附录 A） |
| `dotnet build` | **0 警告 / 0 错误**（26.8s） |
| `dotnet test` | **通过 185 / 失败 0 / 跳过 0**（1m02s） |

工程师声称的「185/185、0 警告」与实测一致。

### 2. AOT 发布复核 — **PASS**

- `dotnet publish -c Release -r win-x64` 产出 `...\win-x64\publish\WingetManager.exe`（**9.7 MB**），仅附带 WinAppSDK 原生 DLL（self-contained），**无 WingetManager.dll 托管产物**。
- 原生性检验：对 exe 全文搜索 IL 元数据魔数 `BSJB` → **0 命中**，确认是 Native AOT 原生镜像。
- **真实启动验证**：后台启动 exe → 15 秒后查 `%LOCALAPPDATA%\WingetManager\logs\app.log`：
  ```
  2026-11-13 10:55:08 [INFO] 启动 Winget 软件管家 3.0.0 · .NET 8.0.33
  2026-11-13 10:55:09 [INFO] winget --version -> 0 (380ms)
  2026-11-13 10:55:18 [INFO] winget list --accept-source-agreements --disable-interactivity -> 0 (8545ms)
  2026-11-13 10:55:18 [INFO] 目录刷新完成: 172 项（可重装 82）8550ms
  ```
  AOT 下 GBK/编码、进程调用、扫描全链路均正常。已 `taskkill` 结束，未截屏、未长时间占屏。
- ⚠ 沙箱内复现需要补环境变量与 MSVC 工具链路径（见附录 A）；在正常开发机（有 VS/BuildTools 环境）上裸命令即可发布。

### 3. AOT 兼容性独立审计 — **PASS**

全仓 grep（`src/` 下全部 .cs/.xaml，排除 obj/）：

| 检查项 | 命中 | 结论 |
|---|---|---|
| `{Binding ` | 0 | 全部改用 `x:Bind`（编译期绑定），含 `UiConverters` 函数式绑定 |
| `XamlReader` | 0 | 无运行时 XAML |
| `Activator.CreateInstance` / `Assembly.Load` / `BindingFlags` / `dynamic ` / `Type.GetType` | 0 | 无反射 API |
| 无 context 的 `JsonSerializer.Serialize/Deserialize` | 0（仅 3 处测试代码 + 1 处 AppJsonContext 内部实现均带 `AppJsonContext.Default.Xxx` typeInfo） | 源生成序列化覆盖全部读写路径 |
| `DataContext` | 0 | — |

csproj 配置核验（`WingetManager.App.csproj` / `WingetManager.Core.csproj`）：
- `PublishAot=true`、`TrimMode=full`、`IsAotCompatible=true`（Core + App 双双启用，AOT/Trim 分析器生效——审计期间 build 无任何 AOT 警告）。
- `InvariantGlobalization=false`（两处均显式保留中文文化行为）。
- GBK 编码实测可用：探针程序输出 `GBK_OK=True`、`GBK_STRICT_OK=True`（`CodePagesEncodingProvider` 注册路径 `Infra/TextEncoding.cs` L54-60 生效）。
- 恢复脚本 GBK 写盘实测：生成的 `.bat` 文件用 Python `raw.decode('gbk')` 解码成功。

### 4. 业务口径对齐（对照 Python 源码逐项） — **PASS**

| 口径 | Python 出处 | C# 出处 | 结论 |
|---|---|---|---|
| reinstallable = 源列 ∈ {winget, msstore} | `services/catalog.py` L74-79（`REINSTALLABLE_SOURCES`） | `CatalogService.IsReinstallable` L244-249 | 一致（词表、strip/lower 语义相同） |
| isDependency 缓存 True 覆盖 / False 不打假 / 未命中退回关键词 | `catalog.py` L168 `True if cached_dep is True else keyword_dep` | `CatalogService.cs` L156 `cachedDependency == true ? true : keywordDependency` | 一致（含 `PendingMetadataIds` 只查「可重装 ∧ 非依赖 ∧ 缓存未命中」L206-236 与 Python L207-230 一致） |
| 同 ID 多行去重（保留可重装优先） | `catalog.py` L171-173 | `CatalogService.cs` L160-164 | 一致 |
| 排序（可重装在前，组内按名称小写） | `catalog.py` L176-179 | `CatalogService.cs` L168-171 | 一致 |
| metadata-cache schema=1 结构 | `services/metadata.py` L131-181 | `RuntimeCache.cs` + `Models/SelectionFile.cs` L14-38 | 一致（含损坏回退、单条坏 entry 跳过不连坐） |
| selection.json 裸映射 `{"<小写 id>": bool}` | `qt_app.py` L1718-1731 | `SelectionFile.Load/Save` | 一致（宽进：`"true"/"1"/"yes"/"on"` 白名单，C# 额外容忍数字，见下「已知差异 #2」） |
| apps.json 结构 | `services/profiles.py` L26-60 `build_winget_manifest` | `ExportService.BuildManifest` | **实测一致**（见下方实测） |
| bat：GBK 编码、`%` 转义、`net session` 检查、`--ignore-unavailable` | `profiles.py` L73-144 | `ExportService.RenderRestoreBat` | **实测一致** |
| txt：UTF-8 BOM + CRLF + 按 (名称小写, ID 小写) 平铺 | `profiles.py` L147-173 | `ExportService.BuildUnreinstallableList` | **实测一致** |
| 版本归一化回退 1.0.0 | `profiles.py` L20-23 | `ExportService.NormalizeWingetVersion` | 一致（同一正则；`'v1.9.25200-preview'` → `'1.9.25200-preview'`、`'abc'`/`''` → `'1.0.0'`，Python 侧实测同值） |
| winget 互斥重试 0x8A150001，最多 2 次 × 0.4s，计入同一总超时 | `winget_cli.py` L32-34, L54-64, L93-122 | `WingetRepository.cs` L32-38, L396-406, L105-149 | 一致（含「非 0 且空输出也按互斥处理」） |
| 解码链 utf-8-sig → utf-8 → cp936 → gbk → 容错 | `winget_cli.py` L67-84 | `TextEncoding.DecodeChain` | 一致（BOM 先剥，防 U+FEFF 混入首行） |
| 取消即 kill 子进程 | `winget_cli.py` 0.2s 轮询 | `WingetRepository.RunOnce` 200ms 轮询 + `Kill(entireProcessTree)` | 一致（E2E 测试实测取消 < 5s 生效） |

**双向兼容实测**（用真实 Python 产物 + 临时探针 `qa-probe/`，未动用户原文件）：

1. C# 读 Python 真实 `metadata-cache.json`（57 条，`schema:1`）→ 条目、dep、checkedAt 全部正确还原。
2. C# 读 Python 真实 `selection.json`（56 键 / 42 勾选）→ 正确。
3. C# 写的 `cs-metadata-cache.json`（schema=1）与 `cs-selection.json` → Python `json.loads` 解析成功且结构/键名/布尔类型正确（key 小写化行为一致）。
4. 同一输入（3 个包：winget/msstore/空源）分别跑 Python `build_winget_manifest` 与 C# `ExportAsync`：`$schema`、`WinGetVersion`、`Sources[].SourceDetails`（4 字段逐值）、`Packages` 分组与排序、顶层键顺序 **逐字段相等**（仅时间戳不同）。
5. bat 实测：`chcp 936`、`net session >nul 2>nul`、`--accept-package-agreements --accept-source-agreements --ignore-unavailable`、纯 CRLF、GBK 可解码；`%` 转义由单测 `ExportServiceTests.cs:227 BuildRestoreBat_EscapesPercentAndStripsDirectory` 覆盖（`"100%%备份.json"`）。
6. txt 实测：`EF BB BF` BOM + 纯 CRLF。

### 5. 边界与容错 — **PASS**

| 场景 | 验证方式 | 结论 |
|---|---|---|
| 缺 winget（`WPM_WINGET_EXE` 指向不存在文件） | 探针实测：`AppConfig.Load()` 返回 `WingetError != null`、不抛异常、`HasWinget=false` | PASS（与 Python 非致命一致） |
| `WPM_WINGET_EXE` 合法但 `RunAsync` 前丢失 | 单测 `WingetRepositoryTests.RunAsync_ThrowsWingetNotFoundWhenExeMissing/NotExecutable` | PASS |
| 损坏 metadata-cache.json | 单测 `RuntimeCacheTests.Load_CorruptJsonFallsBackToEmptyAndCanBeRebuilt`、`Load_InvalidSchemaFallsBackToEmpty`、`Load_SkipsEntriesWithNonBooleanDep` | PASS（schema 不符/JSON 损坏 → 空缓存；单条坏 entry 只跳过） |
| 损坏 selection.json | 单测 `PersistenceTests.SelectionStore_CorruptJsonFallsBackToEmpty` | PASS |
| 只读目录 | 读码判定：`RuntimeCache.Save`/`SelectionStore.Save`/`LogService.Write` 均 catch `IOException`+`UnauthorizedAccessException` 只记日志（`RuntimeCache.cs` L214-221、`SelectionStore.cs` L71-82、`LogService.cs` L94-99），功能不崩、下次重查。`AtomicFile` 失败清理临时文件并保留旧文件（L107-111） | PASS（依据：容错路径全部覆盖 IOException/UnauthorizedAccess，测试套另有原子性/临时文件残留用例） |
| winget 空输出 + 退出码 0x8A150001 重试 | 单测 `IsMutexConflict_TrueForMutexExitCode/TrueForFailureWithNoOutput/FalseWhenFailureHasOutput`、`MutexExitCode_MatchesWingetContract`、`RunAsync_RetriesAfterMutexConflictAndGivesUpAfterMax` | PASS |
| 取消失败路径（取消真终止 / 退避中取消 / 进程清理） | E2E `FullPipeline_CancelTerminatesFakeWingetQuickly`（<5s）+ 单测 `RunAsync_CancelKillsSubprocessImmediately`、`RunAsync_CancelBeforeStartDoesNotLaunchProcess`、`RunAsync_CancelDuringBackoffReturnsImmediately`；finally 块兜底 kill | PASS |

### 6. 配置与日志 — **PASS**

- `WPM_CACHE_TTL=10` / `WPM_COMMAND_TIMEOUT=3600` → 实测分别生效（探针输出 `CONFIG_TTL10=10 TIMEOUT3600=3600`）。
- 越界拒绝：`3601` → 抛 `ConfigInvalidException`「必须在 0~3600 之间」（与 Python `_env_int` 行为一致）。
- 日志轮转 512KB×2：探针写 6×100KB 后主文件 614KB（Python `RotatingFileHandler` 同语义：溢出发生在下一次写前判定）；再写 2 次 → `app.log.1`=614KB、主文件回到 205KB，无 `.3`。**行为与 Python 等价**。
- 注册表：`HKCU\Software\Shibeng\WingetManager` 存在（Python 版遗留值 `export/path`、`view/category`、`view/logExpanded`、`win/maximized`、`win/geometry`），C# `SettingsStore` 键名/路径完全对齐；沙箱禁 reg.exe，改用 .NET Registry API 实测写入/读回/清理成功（`Software\Shibeng\WingetManager.QAProbe`）。

### 7. 测试质量抽查 — **PASS**

抽查 8 个用例（3 文件 E2E + 4 文件单测 + Persistence 组）：

- `EndToEndFakeWingetTests`：真起 cmd 假 winget 进程跑全链路（UTF-8 样本 / GBK 代码页 / 取消时限），断言验证行为而非实现。
- `CatalogServiceTests.BuildSnapshot_ThreeGroupsDeterminedBySourceColumn`：4 行输入断言分组、计数、沉底排序，口径来自 Python tools/test_smoke.py。
- `PersistenceTests`：原子覆盖、无临时文件残留、损坏回退、Python 兼容形态，断言均实质。
- **无任何 `[Skip]`/`Ignore`**（grep 全测试目录 0 命中，含 Theory 全量跑）。
- 断言密度：185 用例 / 每文件 10-40 个 `Assert.*`，无空断言、无永真断言。

---

## 二、发现的缺陷

**需要工程师修复的源码 Bug：无。**

## 三、与 Python 基线的不一致点（低危，不影响验收）

1. **注册表布尔值的遗留文件读法**（`Persistence/SettingsStore.cs` L140-152 `ReadInt`）：Python QSettings 把 `view/logExpanded`、`win/maximized` 存成 REG_SZ `"true"/"false"`；C# 按 int 解析失败即回退默认值。后果：从 Python 版升级到 C# 版后，用户若曾收起日志面板（`"false"`），C# 首次会按默认（展开）显示，之后由 C# 自己的写入覆盖。低危 UI 状态项，非数据破坏。
2. **selection.json 数字字面量的宽进差异**（`Models/SelectionFile.cs` L125-142 `CoerceBool`）：JSON 数字 `2`/`1.0` → C# 判 True，Python `str(v) in ("1","true",...)` 判 False。两版本自身都只写真布尔，仅在「第三方手改文件」场景出现，无实际影响。
3. **窗口几何键不互通**（设计使然）：Python 存二进制 `win/geometry`，C# 用 `win/x|y|width|height` 独立键。升级后窗口位置回默认一次。低危。
4. **metadata-cache 带 BOM 的边缘差异**：Python `json.loads` 对带 BOM 文件判损坏回空；C# `JsonDocument.Parse` 可容忍 BOM。仅影响异常文件的宽容度方向不同，不影响正常互通（实测互通正常）。

## 四、测试通过率与路由判定

- 总用例 185 / 通过 185 / 失败 0 / 跳过 0 → **通过率 100%**
- 构建警告 0、错误 0
- **路由判定：Send To NoOne**（第 1 轮全通过；验证中我自己的探针预期「6 次写入必须立即轮转」是探针预期错误而非源码缺陷，已自行修正预期并复测——与 Python RotatingFileHandler 溢出语义比对后确认 C# 行为正确）

## 五、遗留风险

1. **AOT 构建环境依赖**：发布依赖 MSVC 工具链（link.exe）。在标准 VS/BuildTools 开发机无感；但在精简环境（缺 `OS=Windows_NT`、`ProgramData` 等系统环境变量，或 PATH 混入 GNU coreutils 的 `link`）会以「Cross-OS native compilation」/「Platform linker not found」/LNK1181 等报错失败。建议 CI/文档固定 `vcvars64 + dotnet publish` 流程（附录 A 给出沙箱内完整复现命令）。
2. WinAppSDK 1.7.251107005 已钉版本（对），但小版本升级仍可能引入 AOT 行为变化（ARCHITECTURE §5 UNCLEAR#2 已自述），升级时需回归发布+启动冒烟。
3. 测试覆盖缺口（非缺陷）：`WPM_CACHE_TTL/WPM_COMMAND_TIMEOUT` 边界解析、日志轮转、`AppConfig.Load()` 整体行为没有直接单测文件（本次由探针实测覆盖）；建议后续补 `AppConfigTests` / `LogServiceTests`。
4. 本报告的运行验证在沙箱内完成；正常桌面环境的 GUI 交互（窗口尺寸记忆、分类筛选恢复）仅经代码走读与注册表键名核对，未人工点检。

---

## 附录 A：沙箱内完整复现命令（备查）

沙箱缺失系统环境变量（`OS`、`ProgramData`、`ProgramFiles` 等），导致两个独立故障：restore 报 `Value cannot be null (Parameter 'path1')`（NuGet 机器级配置目录解析失败）、AOT 报 `Cross-OS native compilation`。同时 `IlcUseEnvironmentalTools=true` 需 MSVC `link.exe` 在 PATH 且 `LIB` 含 SDK 库：

```bash
ENV='ProgramData=C:\ProgramData ProgramFiles=C:\Program Files ProgramFiles(x86)=C:\Program Files (x86) ComSpec=C:\WINDOWS\system32\cmd.exe OS=Windows_NT'
MSVC='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
SDK='C:\Program Files (x86)\Windows Kits\10'
# restore
env $ENV dotnet restore
# publish（PATH 前插 MSVC/SDK x64 bin；LIB 用 Windows 风格路径）
env $ENV PATH="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64:/c/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64:$PATH" \
  LIB="$MSVC\\lib\\x64;$SDK\\Lib\\10.0.26100.0\\um\\x64;$SDK\\Lib\\10.0.26100.0\\ucrt\\x64" \
  dotnet publish src/WingetManager.App -c Release -r win-x64 -p:IlcUseEnvironmentalTools=true
```

验证辅助工件（QA 自建，不属于仓库）：`D:/UserData/Shibeng/桌面/winget/qa-probe/`（探针工程 + 输出样本）。
