# PRD · WingetManagerWpf（Winget 软件管家 · C#/WPF 复刻版）

> 产品经理：许清楚 ｜ 版本：v1.0 ｜ 类型：简单 PRD（复刻类需求，无竞品分析）

---

## 1. 项目信息

| 项 | 内容 |
|---|---|
| Language | 简体中文 |
| Programming Language | C# + WPF + .NET Framework 4.8（传统 csproj，`TargetFrameworkVersion v4.8`，`UseWPF`），**零 NuGet 第三方依赖** |
| 解决方案 / 项目名 | `WingetManagerWpf.sln` / `WingetManagerWpf`（snake_case 标识：`winget_manager_wpf`） |
| 参考项目 | GitHub `Sword6988/winget-manager`（Python 3.14 + PySide6，版本 2.18.0），本地源码 `_ref/` |
| 输出目录 | `D:\Program Files\WorkBuddy\winget\WingetManagerWpf\` |
| 原始需求复述 | 把「Winget 软件管家」按**功能对等**复刻为 C# + WPF 桌面应用：扫描本机已安装软件 → 固定三组（软件 / 运行库 / winget 不支持）→ 勾选要保留的 → 导出 winget 官方 import 清单 `apps.json` + 双击即恢复的 `恢复-*.bat` + 无法自动重装清单 `*.html`（可勾选的待办页）。界面布局与操作逻辑与原版一致，代码结构清晰、可编译运行，并交付功能对应关系与已知差异说明。**只导出，不导入。** |

---

## 2. 产品定义

### 2.1 产品目标（3 个，正交）

1. **功能对等**：扫描、可重装判定、运行库识别、去重排序、导出三产物、持久化——业务规则与原项目逐条对齐，同名数据文件格式互通（`selection.json` / `metadata-cache.json` 可直接复用）。
2. **交互对等**：窗口自上而下的布局层次、状态机、禁用矩阵、快捷键、筛选语义、日志与提示文案与原版一致，老用户无需重新学习。
3. **工程可维护**：分层与原项目一一对应（Core / Services / UI），零第三方依赖，单解决方案在装有 .NET Framework 4.8 的 Windows 10/11 上直接 F5 运行。

### 2.2 范围边界

- **在范围内**：本机 winget 枚举、三组分类、勾选与筛选、导出产物、运行日志、设置持久化、winget 缺失引导、关于对话框。
- **不在范围内**：导入恢复（原项目 2.14.0 已移除，本期不复活）、winget 升级/卸载软件、打包安装器（Inno/ClickOnce）、多语言 i18n、深色主题、云同步。

### 2.3 用户故事

1. 作为一个**准备重装系统的普通用户**，我想一键扫描出本机所有已安装软件并看到「哪些能被 winget 自动装回来」，以便决定要保留哪些。
2. 作为一个**只想保留应用、不带运行库的用户**，我想用分组胶囊和搜索（支持 `id:` 前缀、多词 AND）快速定位，并只对当前可见项做全选/反选，以便不误改屏幕外的勾选。
3. 作为一个**担心漏装的用户**，我想把勾选结果导出成 `apps.json` + 双击即恢复的 `.bat`，并额外拿到一份「无法自动重装清单.html」，重装后照着它逐个装、勾选记进度。
4. 作为一个**看日志排障的用户**，我想在界面底部看到带时间戳与分级着色的运行日志（info/success/skip/fail/done），并能复制、保存、清空，以便把日志贴给别人排查。
5. 作为一个**winget 缺失或版本异常的用户**，我想被明确引导到微软商店安装「应用安装程序」，而不是看到程序崩溃或静默空白。
6. 作为一个**中途反悔的用户**，我想在扫描过程中点「取消扫描」并在 0.2 秒内真正终止 winget 子进程，界面不卡死。

---

## 3. 需求池

> P0 = 必须对等，缺失即判定复刻失败；P1 = 应做到；P2 = 可裁剪（视觉动效细节等）。

### P0-A 运行环境与 winget 接入

1. 启动即探测 winget：`winget --version`，输出去掉前缀 `v`；**拿不到版本按 `unknown` 继续扫描**，不得误判为「未安装」（是否可用由 `winget list` 结果决定）。
2. 支持环境变量：`WPM_WINGET_EXE`（指定 exe 路径，路径不存在需明确报错）、`WPM_COMMAND_TIMEOUT`（默认 180s，10–3600 区间）、`WPM_CACHE_TTL`（默认 60s）。
3. 所有 winget 调用带 `--disable-interactivity --accept-source-agreements`；创建进程 **不弹控制台窗口**（`CreateNoWindow`）；超时按 `WPM_COMMAND_TIMEOUT` 终止并给出可读错误。
4. 并发互斥容错：退出码 `0x8A150001`（或失败且输出为空）判为 winget 进程互斥冲突 → 退避 0.4s 重试，最多 2 次。
5. 可取消：0.2s 轮询取消令牌 + 真正 Kill 进程树（`winget list` 与 `winget show` 皆需可取消）。
6. 输出解码顺序：`utf-8-sig` → `utf-8` → 系统本地编码（GBK/936）→ 容错替换，全程不因 BOM/乱码抛异常。
7. winget 不可用 → 状态 `NO_WINGET`，弹引导对话框：**打开应用商店**（`ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1`）、**复制下载地址**（`https://aka.ms/getwinget`）、**知道了**；主窗口内呈现对应空态文案。

### P0-B 扫描与列表解析

1. 启动后约 80ms 自动触发首轮扫描；只跑一条 `winget list`（不调用 `winget export`）。
2. 表格解析：先定位分隔线（`^-{5,}$`），按**显示宽度（East Asian Width）**用中英表头标签（名称/Name、ID/Id、版本/Version、可用/Available、源/Source）切列；失败则兜底按「2+ 连续空格」从右往左认领（源 ∈ {winget, msstore}、版本匹配 `^[0-9v]`）。
3. 噪音行过滤：`已找到` / `找不到` / `以下` / `the following` / `no installed package` / `没有找到` / `已安装的程序包` 等前缀行不计入包。
4. 去重：同一包 ID 多行合并，保留信息更全的一条（优先可重装的）。
5. 重装判定：`源` 列 ∈ {`winget`, `msstore`} → 可重装；空/其他 → 不可重装（行灰显、不可勾选）。
6. 分组与排序：`软件`（可重装 ∧ 非运行库）→ `运行库`（可重装 ∧ 运行库）→ `winget 不支持`（不可重装）；组顺序固定，组内按名称小写升序；**空组不渲染组头**。
7. 扫描耗时统计，副标题展示 `共 N 个已安装 · R 项可重装（软件 a / 运行库 b） · U 项 winget 装不上（归入「winget 不支持」） · 扫描耗时 X.Xs`。

### P0-C 运行库识别（两级）

1. **关键词初判**（对 name + id 小写包含匹配）：`vcredist` / `visualc++` / `redistributable` / `dotnet` / `windowsappruntime` / `vclibs` / `ui.xaml` / `appinstaller` / `directx` / `webview2` / `.net framework` / `net framework` / `net.native` / `desktopappruntime` / `xna` / `vc_redist` / `net runtime` / `windowsdesktop.app` / `aspnetcore`（共 19 个；后 4 个为补充的窄变体）。
2. **元数据富化**：扫描完成后，对「可重装 ∧ 非运行库 ∧ 缓存未核查」的包逐个执行 `winget show --id <id> --exact`，解析描述/标记/安装器 URL 段落，命中信号词 `runtime` / `redistributable` / `redist` / `运行库` / `运行时` / `required to run` → 改判为运行库。
3. 判定优先级：**只补漏、不打假**——元数据命中才覆盖；关键词已认定的运行库不因元数据未命中而被移出该组。
4. 结果写磁盘缓存 `%LOCALAPPDATA%\WingetManager\metadata-cache.json`（`{id: {dep: bool, checkedAt: iso}}`，`schema: 1`），下次扫描直接采用；文件损坏自动作废重建。
5. 批量应用改判（每批 5 条）后一次性重建列表，逐条写日志；扫描进行中核查让路，重扫时取消进行中的核查，未查完的下一轮续上。

### P0-D 勾选、筛选与搜索

1. 勾选持久化：`%LOCALAPPDATA%\WingetManager\selection.json`，格式 `{包id小写: true/false}`，原生写出（tmp + fsync + 替换的原子写）；读入严格校验布尔值，文件损坏回退为空并写日志；扫描后清理已卸载项的残留勾选（日志告知清理数量）。
2. 范围批量操作：「全选 / 全不选 / 反选」**只作用于当前搜索 + 分组 + 仅看已选下可见且可勾选的包**，操作后 flash 提示「已全选当前 N 项」等文案。
3. 「仅看已选」toggle：只显示已勾选项；该模式下取消勾选，行随即从列表消失；若勾选为空则给出「还没有勾选任何软件 / 取消「仅看已选」可继续浏览并勾选」提示。
4. 搜索：130ms 防抖；空格分词多词 AND；支持 `id:` / `name:` 前缀限定字段；命中片段浅黄高亮；`Esc` 清空搜索（光标在搜索框内）或清除全部筛选。
5. 分组胶囊：`全部 N` / `软件 n` / `运行库 n` / `winget 不支持 n`，单选、选中态渐变填充、横向滚动；上次选择的分组需持久化，恢复时该组已不存在则回落「全部」。
6. 列表行交互：行高 46px，自绘复选框 + 名称 + 包 ID（等宽字体）+ 状态标签（`运行库` 琥珀 / 来源 `winget`·`msstore` 紫 / 不可重装灰显）+ 右对齐版本列（`v` 前缀，空显示 `—`）；hover 浅底、键盘焦点描边、底部分隔线。
7. 行右键菜单：复制包 ID / 复制软件名称 / 复制「名称 (包ID)」/ 复制安装命令 `winget install --id <id> -e --accept-package-agreements --accept-source-agreements` / 勾选·取消勾选。
8. 行键盘操作：`Space` 切换勾选、`Enter` 复制包 ID、`↑`/`↓` 移动当前行。
9. 分组头：折叠三角 + 标题 + 计数胶囊（已选/总数，勾满高亮）+ 一个批量按钮「全选本组 ⇄ 全不选」（文字随本组是否勾满翻转，不可勾选组不显示）；折叠状态会话内保留（不落盘，重扫与搜索重渲染后保持）。
10. 空态文案需区分：`NO_WINGET` / `CANCELLED` / `SCAN_FAILED` / 扫描中 / 未发现可重装的软件 / 搜索无结果（「未找到与『xxx』匹配的软件」）。

### P0-E 状态机与按钮禁用矩阵

状态：`BOOT → SCANNING → READY` / `SCAN_FAILED` / `NO_WINGET` / `CANCELLED`。

1. 扫描中「重新扫描」按钮变文案为「取消扫描」，点击即取消（0.2s 内终止 winget）；扫描中按 F5 只提示「扫描进行中，请点「取消扫描」中断」。
2. 取消后：有旧数据 → 回 `READY` 并保留旧列表；无数据 → `CANCELLED` 空态。
3. 防重入：上一轮仍在收尾时提示「上一轮扫描仍在收尾，请稍候」。
4. 扫描中按规则禁用：重新扫描（改为取消）、搜索框、全选/全不选/反选、仅看已选、导出配置按钮；勾选操作同理不可用。
5. 扫描失败不弹模态框：保留旧数据 + 页内横幅/空态文案 + 可重试。
6. 单实例：命名 Mutex（语义等同原 `WingetManagerQt.SingleInstance`），二次启动激活已有窗口后退出。

### P0-F 导出（核心价值）

1. 导出对话框（约 560px 宽，两页）：
   - **表单页**：保存路径行 + `更改…`、目标同名文件存在时琥珀色提示并把主按钮文案改为「覆盖导出」、msstore 来源提示、`导出`/`取消`。
   - **成功页**：✓ 徽章 + 三个产物文件清单（含完整路径）+ 双提示语（如何在新机恢复）+「导出后打开文件夹」勾选行 + `关闭`。
2. 默认导出路径优先级：上次导出路径 → `D:\apps.json`（D 盘存在时）→ `文档\apps.json`；导出后记录本次路径。
3. 三个产物（同目录、原子写）：
   - `apps.json`：winget packages schema `2.0`，`$schema` = `https://aka.ms/winget-packages.schema.2.0.json`；`Sources` 按 `winget` / `msstore` 分组（空组不写），`SourceDetails` 用固定常量；`WinGetVersion` 必须数字开头（正则 `^v?(\d+(?:\.\d+){1,3}([-+][0-9A-Za-z.\-]+)?)`，不符则 `1.0.0`）；`CreationDate` 本地时间 ISO 格式；`Packages` 含 `PackageIdentifier` / `Version`。
   - `恢复-<stem>.bat`：**GBK 编码 + CRLF + `chcp 936`**；检查 winget 是否可用、检查管理员权限；执行 `winget import -i "%~dp0<文件名>" --accept-package-agreements --accept-source-agreements --ignore-unavailable`；文件名中 `%` 双写转义；无法用 GBK 编码时报用户可读错误（不静默丢字符）。
   - `无法自动重装清单-<stem>.html`：**单文件 HTML（UTF-8 无 BOM、`meta charset` 声明、CRLF）**，双击用浏览器打开。内容：标题 + 导出时间 + 进度卡（待装 / 已装好 / 完成度）+ 进度条 + 「全部 / 未装 / 已装」筛选 + 搜索框 + 按「普通软件 / 系统组件 · 运行库」分节 + 每项「复选框 + 名称 + 版本 + 类型标签（无独立安装包 / 商店应用 / 源不可用）+ 搜官网链接」。勾选状态存 localStorage（关页面再开仍记得）；CSS/JS 全内联，**零外部资源**，离线可用；带打印样式。目标是**能照着装的手册**，不是机器可读清单（机器可读的是 `apps.json`）。
4. 导出前置校验：0 勾选时禁止导出（按钮 disabled，防御性兜底提示）；非法文件名字符就地校验并禁用主按钮。
5. 导出成功后在日志中按文件逐条输出路径（tag = `done`）；勾选项里 msstore 来源需在成功页/对话框给出「可能需先登录微软商店」提示。

### P0-G 日志与持久化

1. 日志面板：标题行（状态圆点 + 「运行日志」+ 收起/展开 + 复制 + 保存… + 清空）；条目格式 `HH:mm:ss` 浅灰时间戳 + 按级别着色正文（info 深灰 / success 绿 / skip 紫 / fail 红 / done 靛蓝加粗）；新日志自动滚底（用户手动上滚时不强制滚动）。
2. 可拖拽调整日志区高度（约 90–420px），双击标题行折叠/展开，高度与展开状态持久化。
3. 文件日志：`%LOCALAPPDATA%\WingetManager\logs\app.log`，单文件 512KB × 2 轮转；记录 winget 完整命令行（含超时/退出码/重试）。
4. 设置持久化：窗口位置与尺寸、最大化状态、日志高度与展开态、上次分组筛选、上次导出路径。原项目用注册表（QSettings），WPF 侧改用同目录 `settings.json`（**已知差异，可接受**）。
5. 数据目录不可写时**不得启动失败**：降级为内存态并给出引导日志。

### P0-H 窗口与视觉令牌

1. 无边框圆角窗口（`WindowChrome`：`GlassFrameThickness=0`、`CaptionHeight=0` 自管、`ResizeBorderThickness=7` 边缘缩放）；默认 1240×880，最小 940×600。
2. 自绘标题栏 52px：品牌标 + 标题（渐变字）+ ⓘ 关于按钮 + `—` `□` `✕`；可拖动移动、双击最大化/还原。
3. 设计令牌集中定义（颜色/字号/圆角/间距），按原 `qt_common.py` 取值：`BG #F4F5FA`、`CARD #FFFFFF`、`LINE #E8EAF3`、`TEXT #181C28`、`MUTED #6A7183`、`ACCENT #6366F1`、`ACCENT_DEEP #4F46E5`、`ACCENT_2 #8B5CF6`、`ACCENT_SOFT #EEF0FF`、`ACCENT_LINE #D9DCFF`、`WARN #B45309`/`WARN_BG #FFF8E8`、`ERR #C53030`、`ROW_HOVER #F7F8FE`、日志色 `LOG_OK #0B7A55`/`LOG_SKIP #6D4FD0`/`LOG_DONE #4F46E5` 等；字体 `HarmonyOS Sans SC` 回退 `微软雅黑`，等宽 `Consolas`；主按钮 135° 渐变 `ACCENT → ACCENT_2`。
4. 关于对话框：显示版本 `2.18.0`（单一版本来源）、`复制诊断信息`、`打开数据文件夹`（`%LOCALAPPDATA%\WingetManager`）。

### P1（应做到）

1. 统计卡 ×4（已安装 / 可重装高亮 / 已勾选高亮 / 运行库）：104×70 白卡、顶部渐变条、hover 微浮。
2. 列表吸顶分组头：滚动时当前组浮顶、点击回跳该组、下一组头推入效果。
3. 首次扫描加载骨架屏（6 行占位块呼吸动画）。
4. 顶部迷你进度条 + 扫描计时文案 `正在扫描 · 已用 X.Xs · 点「取消扫描」可中断等待`（100ms tick）。
5. 窄窗口适配：宽度 < 1080 时隐藏行内状态标签与版本列（不破坏勾选与名称列）。
6. 折叠/展开过渡动画、胶囊切换过渡、flash 提示条。
7. 底栏：左侧状态文案（筛选时 `筛选出 M / 共 N 项`）+ 右侧「已勾选数渐变数字 / R 项已勾选」+ 导出配置主按钮。
8. 玻璃质感工具条与底栏（`rgba(255,255,255,0.78)` + 描边 + 轻阴影）——WPF 用静态半透明画刷实现，不追求实时模糊。
9. 勾选写盘 debounce（约 300ms）+ 退出时强制 flush。

### P2（可裁剪）

1. 计数胶囊「勾满渐变填充」的按比例动画、hover 微浮位移、行 hover 渐入过渡。
2. 吸顶分组头「下一组头推入」的动效细节（保留浮顶与点击回跳，动效可简化）。
3. 骨架屏呼吸动画曲线（可退化为静态占位块）。
4. 自绘品牌标（可用内嵌矢量 Path 简化重绘）。
5. 高 DPI（125%/150%）下的像素级对齐微调、多显示器跨屏 DPI 切换自适应。
6. 列头点击排序（原项目亦未实现，不纳入对等范围）。

---

## 4. UI 结构说明

窗口自上而下 8 层（与原版一致）：

```
┌──────────────────────────────────────────────────────────────┐
│ ① 自绘标题栏 52px  品牌标 + 「Winget 软件管家」渐变字 + ⓘ — □ ✕ │
├──────────────────────────────────────────────────────────────┤
│ ② 头部区                                                        │
│    左：标题 + 副标题（共 N … 扫描耗时 X.Xs）                      │
│    右：统计卡 ×4（已安装 / 可重装 / 已勾选 / 运行库）              │
├──────────────────────────────────────────────────────────────┤
│ ③ 玻璃工具条（圆角 16 卡片）                                      │
│    [🔍 搜索名称或包 ID]  [重新扫描/取消扫描]  [仅看已选]          │
│    [全选] [全不选] [反选]                                        │
├──────────────────────────────────────────────────────────────┤
│ ④ 分组胶囊行（横向滚动）                                          │
│    (全部 61) (软件 40) (运行库 12) (winget 不支持 9)              │
├──────────────────────────────────────────────────────────────┤
│ ⑤ 列表卡（主区域，弹性填充）                                       │
│    ▾ 软件  12/40              [全选本组]   ← 分组头（吸顶）      │
│    ☑ 名称                        [运行库]  v1.2.3                │
│      Publisher.PackageId                                        │
│    ☐ 名称2                       [winget]  —                     │
│    （空态/骨架屏/搜索无结果时替换本区内容）                         │
├──────────────────────────────────────────────────────────────┤
│ ⑥ 日志面板（高度可拖 90–420，可折叠）                              │
│    ● 运行日志            [收起] [复制] [保存…] [清空]             │
│    12:01:03  已找到 61 个已安装包                                 │
│    12:01:05  ✓ 运行库核查完成（改判 3 项）                        │
├──────────────────────────────────────────────────────────────┤
│ ⑦ 底栏 状态文案                已勾选 23/R 项已勾选  [导出配置 ▸]  │
└──────────────────────────────────────────────────────────────┘
```

**对话框/浮层**

- **导出对话框**（模态，约 560px）：表单页（路径 + 更改… / 覆盖提示 / msstore 提示 / 导出）→ 成功页（✓ + 文件清单 + 恢复提示 + 「导出后打开文件夹」+ 关闭）。
- **关于对话框**：应用名 + 版本 2.18.0 + 复制诊断信息 + 打开数据文件夹 + 关闭。
- **winget 缺失引导**：说明文案 + `打开应用商店` / `复制下载地址` / `知道了`。
- **右键菜单**（列表行）：5 项，如 P0-D-7。
- **flash 提示条**：批量操作后的短时提示（如「已全选当前 12 项」）。

**操作逻辑要点**

- 单选胶囊 + 搜索 + 仅看已选 三者交集决定「当前可见可勾选项」，所有批量勾选只作用于该集合。
- 扫描中除日志与取消外，交互整体降级（见 P0-E-4）。
- 窗口尺寸、日志高度、折叠态、分组筛选、导出路径在下次启动还原。

---

## 5. 功能对应关系表（原 Python 模块 → WPF 承载）

| 原 Python 模块/文件 | 职责 | WPF 侧预期承载 |
|---|---|---|
| `core/config.py` | 环境变量（WPM_WINGET_EXE / CACHE_TTL / COMMAND_TIMEOUT）、路径常量 | `Core/AppConfig.cs`、`Core/AppPaths.cs` |
| `core/errors.py` | 类型化异常（AppError 等） | `Core/AppException.cs`（分类：Winget/IO/Parse/Encode） |
| `core/fsio.py` | 原子写文本/JSON | `Core/AtomicFile.cs`（tmp + Flush(true) + `File.Replace`/`Move`） |
| `core/logging_setup.py` | 唯一日志入口、512KB×2 轮转 | `Core/AppLogger.cs`（文件轮转 + 事件推送到 UI 日志面板） |
| `core/version.py` | 单一版本号 2.18.0 | `Core/AppVersion.cs`（AssemblyInfo 同步） |
| `services/winget_cli.py` | 唯一子进程入口：执行/超时/取消/互斥重试/多编码解码 | `Services/WingetCli.cs`（`Process` + `CancellationToken` + 0.2s 轮询 + 编码回退） |
| `services/winget_table.py` | `winget list` 表格解析（显示宽度 + 中英表头 + 兜底） | `Services/WingetTableParser.cs` |
| `services/catalog.py` | 去重、可重装判定、关键词运行库初判、分组、目录缓存 | `Services/PackageCatalog.cs` |
| `services/metadata.py` | `winget show` 元数据核查 + 磁盘缓存 | `Services/MetadataProbe.cs` + `Models/MetadataCache.cs` |
| `services/profiles.py` | 生成 apps.json / 恢复 bat / 无法重装 txt | `Services/ExportProfiles.cs`（JSON 用 `DataContractJsonSerializer` 或手写序列化，bat 用 GBK `Encoding.GetEncoding(936)`） |
| `qt_common.py` | 设计令牌、路径、QSettings | `Theme/Tokens.xaml`（颜色/字号/圆角资源字典）、`Core/AppSettings.cs`（settings.json） |
| `qt_ui.py` | 自绘控件与对话框 | `Controls/`：`TitleBarControl`、`StatCard`、`MiniProgress`、`PackageRowControl`、`CountPill`、`GroupHeaderControl`、`LogPanel`、`GlassCard`；`Views/`：`ExportDialog`、`AboutDialog`、`WingetMissingDialog`（`DrawingContext` 重绘替代 QPainter） |
| `qt_app.py`（MainWindow / ScanWorker / MetadataWorker / 状态机 / 快捷键） | 主窗口与编排 | `MainWindow.xaml(.cs)` + `ViewModels/MainViewModel.cs`、`Services/ScanService.cs`、`Services/MetadataService.cs`、`Core/AppStateMachine.cs`、`Commands/HotkeyBindings.cs` |
| `tools/test_smoke.py`（40 例） | 离屏测试 | `WingetManagerWpf.Tests`（可选，纯逻辑层单测：表格解析/去重分组/运行库判定/JSON 结构/bat 转义/原子写） |

**已知差异（需在交付说明中明示）**

1. 窗口/筛选/导出路径持久化由注册表改为 `%LOCALAPPDATA%\WingetManager\settings.json`。
2. 玻璃模糊质感以静态半透明画刷近似（无实时高斯模糊）。
3. Python `QThread` 线程模型改为 `Task` + `CancellationToken`（对外行为一致）。
4. PyInstaller 打包形态在本期不提供，交付 `.sln` 源码与本地编译步骤。

---

## 6. 待确认问题

1. **持久化位置**：窗口状态用 `settings.json`（推荐，便于排查与迁移）还是照抄注册表 `HKCU\Software\Shibeng\WingetManager`？默认按推荐执行。
2. **目录缓存（WPM_CACHE_TTL）**：原项目有「目录缓存秒数」。是否必须在进程内实现 60s 缓存（同一会话短时间内重扫复用 `winget list` 结果）？建议实现为内存缓存并保留环境变量语义。
3. **测试工程**：是否要求附带纯逻辑层单元测试工程（会引入额外 csproj 与测试框架依赖，与「零 NuGet」冲突）？建议不引入第三方测试框架，仅交付控制台自检入口或省略。
4. **吸顶分组头与骨架屏**是否列为验收必需项？成本较高，建议按 P1 验收，允许在时间紧张时降级为普通分组头 + 静态占位。
5. **字体**：`HarmonyOS Sans SC` 多数机器未安装，WPF 将回退 `微软雅黑`；是否接受该回退（视觉差异极小）？
6. **运行库核查开关**：元数据核查会额外调用多次 `winget show`（首扫可能多花数秒）。是否需要界面开关（原版无开关，默认开启）？建议不加开关，保持对等。
