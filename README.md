# Winget 软件管家 · WPF 桌面版

清点本机已安装软件，把可自动重装的包导出为 [winget 官方清单格式](https://aka.ms/winget-packages.schema.2.0.json)。重装系统后把导出的三个文件拷到新机器，双击恢复脚本即可批量装回。

> 这是原 Python/PySide 版本（`WingetSoftwareManager`）的 C# WPF 复刻，UI 与功能逐项对齐。
> 定位是**只导出、不导入**：本工具不会替你安装任何软件，安装动作只发生在恢复脚本由你亲手双击时。
>
> 📍 **所在分支**：`wpf-rewrite`。本仓库的 `master` 分支是 Python/PySide 原版，
> `winui3-rewrite` 是另一条 WinUI 3 重写线；三个实现互不干扰，各自是一棵完整可构建的树。

- 版本：2.18.1（基线对齐原版 `core/version.py` 的 2.18.0，此后 WPF 侧的界面修订在此独立递增）
- 界面语言：中文
- 零 NuGet 依赖：只用 .NET Framework 4.8 自带程序集
- 不想自己编译？直接下 exe：[**WPF 版发布页**](https://github.com/Sword6988/winget-manager/releases?q=wpf)
  （`WingetManager-<版本>-wpf.exe` + `SHA256SUMS.txt`，由 GitHub Actions 构建并上传）。
  这里刻意不写死某个版本号——链接指向按 `wpf` 筛选的发布列表，永远显示最新一版，不会随发版失效。

---

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809+ / Windows 11 |
| 运行时 | .NET Framework 4.8（Win10 1903+ 自带） |
| 包管理器 | winget 1.6+（随「应用安装程序 / App Installer」提供） |
| 编译 | Visual Studio 2022（含 .NET 桌面开发负载）或 MSBuild 15+ |
| 屏幕分辨率 | 940×600 起（推荐 1240×880） |

---

## 编译

**方式一（推荐，产出绿色单文件版）**：

```bat
build-release.bat
:: 产物：dist\WingetManager.exe —— 就一个文件，拷走即用
```

脚本按顺序找两套工具链：

| 顺序 | 工具链 | 条件 |
|---|---|---|
| 1 | Visual Studio 2019/2022 的 MSBuild（走 `vswhere`） | 装了 VS 或 Build Tools |
| 2 | **离线工具链**：系统自带 MSBuild 4.0 + `tools/build-deps` 里的 Roslyn 4.8 编译器 + 官方 .NET Framework 4.8 参考程序集 | 没装 VS 也能编，无需管理员权限 |

两种模式都以 `Release|AnyCPU` 重编译，关掉 pdb 与 exe.config，再把 exe 单独复制到 `dist\`，
最后打印文件大小与 SHA256。离线模式缺依赖时，先跑 `tools\fetch-build-deps.bat`（约 40 MB 下载）。

> 这条离线路径不是理论方案：本工程最后一次构建就是用它产出的
> `dist\WingetManager.exe`（约 330 KB，0 error / 0 warning），并用同一套工具链编译并跑通了
> `tests/SelfTest`（445 项断言全通过）。

> **手上没有 Windows 机器？** 推到 GitHub 后跑 `.github/workflows/build.yml`（已随仓库提供），
> 在 Actions 的 Artifacts 里直接下载 `WingetManager-singlefile`。

**方式二**：用 VS2022 打开 `WingetManagerWpf.sln` → 选中 `WingetManagerWpf` 项目 → `F5`。

**方式三（命令行）**：

```bat
msbuild WingetManagerWpf.sln /p:Configuration=Release /m
:: 产物在 WingetManagerWpf\bin\Release\WingetManager.exe
```

> 图标由 `tools/make_icon.py`（纯 Python 标准库）生成，已随仓库提交；若需重新生成：
>
> ```bat
> python tools/make_icon.py
> ```

**编译警告**：目前为 **0 warning**。此前 `Controls/GradientText.cs`、`CountPill.cs`、`StatCard.cs`
用的 `FormattedText` 旧构造重载会在高 DPI 下按 1.0 量字（字被裁），已统一改为带 `PixelsPerDip`
的重载（`CS0618` 一并消失）。

**自测**（逻辑层 445 项断言，不需要 UI）：

```bat
tests\SelfTest\build-and-run.bat
:: 输出末尾 "结果: PASS"，退出码 0
```

---

## 绿色单文件版

**为什么能只有一个 exe**：工程零 NuGet，只引用 .NET Framework 4.8 自带程序集；
14 个 XAML 经 `MSBuild:Compile` 编成 BAML 后嵌进程序集资源；图标、DPI manifest
也都内嵌。所以 `Release` 输出天然只有一个 exe，不需要 ILMerge / Costura 之类的合并器。

| 项目 | 说明 |
|---|---|
| 交付物 | `dist\WingetManager.exe`（单文件，约 330 KB） |
| 依赖 | 目标机需 **.NET Framework 4.8**（Win10 1903+ / Win11 自带；Win7/8.1 需自行安装） |
| 安装 | 无。双击即运行，不写注册表、不碰 Program Files、不建开始菜单 |
| 首次运行 | 可能弹 Windows SmartScreen（未签名），点「更多信息 → 仍要运行」 |
| 卸载 | 删 exe 所在文件夹（数据在旁边的 `Data\`）；若曾强制关闭便携模式，数据在 `%LOCALAPPDATA%\WingetManager` |

### 数据目录（默认便携）

**默认是便携模式**：程序会在 exe 同目录建 `Data\`，配置、勾选记录、判定缓存、日志都跟着 exe 走
——换个盘符、整个文件夹拷走、删目录即卸载，不需要写注册表也不需要用户目录。

只有当 exe 所在目录**写不进去**（装在 `Program Files`、只读介质、受控目录）时，才自动回落到
`%LOCALAPPDATA%\WingetManager`，保证受限账户也能跑起来。首次进入便携模式时，会把旧用户目录里的
`selection.json` / `settings.json` / `metadata-cache.json` **复制**过来（源文件保留），所以与原版
Python 版的数据仍然互通。

| 方式 | 操作 |
|---|---|
| 强制关闭便携 | `set WPM_PORTABLE=0`（或 `false` / `no`） |
| 强制开启便携 | `set WPM_PORTABLE=1`，或在 exe 同目录放一个空文件 `WingetManager.portable` |

便携模式下互斥量名会按数据目录加后缀，因此两个不同目录的副本可以同时运行、互不抢窗口。

> 便携模式请勿把 exe 放在只读位置（如 `Program Files`、只读光盘）；
> 写不进去时程序不会崩，但勾选与窗口状态只在本次运行内有效（日志里会有 WARN）。
>
> 重新打包（`build-release.bat`）**不会**删除 `dist\Data` —— 它只替换 `dist\WingetManager.exe`。

---

## 运行

双击 `WingetManager.exe`。启动后自动扫描已安装软件（约几秒，取决于软件数量）：

1. 列表按「全部 / 软件 / 运行库 / winget 不支持」分组；
2. 勾选想要保留的软件（支持全选 / 全不选 / 反选 / 仅看已选）；
3. 点底部浮动条上的「导出配置」（或 `Ctrl+E`）；
4. 在导出对话框里确认路径，点「立即导出 / 覆盖导出」。

### 四个环境变量（全部可选）

| 变量 | 含义 | 默认值 | 范围 |
|---|---|---|---|
| `WPM_WINGET_EXE` | 指定 winget.exe 完整路径 | 自动探测（PATH → `%LOCALAPPDATA%\Microsoft\WindowsApps`） | 必须指向存在的文件 |
| `WPM_COMMAND_TIMEOUT` | 单条 winget 命令总超时（秒，含重试退避） | 180 | 10 ~ 3600 |
| `WPM_CACHE_TTL` | 目录内存缓存存活秒数（0 = 每次都重扫） | 60 | 0 ~ 3600 |
| `WPM_META_TTL_DAYS` | 「是软件 / 是运行库」判定缓存的有效期（天，0 = 永不过期） | 90 | 0 ~ 3650 |

环境变量非法时程序**不会崩**：记一条日志后回退默认值继续跑。

---

## 导出产物

以默认路径 `D:\apps.json` 为例，导出后同目录会出现三个文件（stem 同名）：

| 文件 | 编码 | 用途 |
|---|---|---|
| `apps.json` | UTF-8 **无 BOM**、2 空格缩进、LF | winget import 标准清单（主产物） |
| `恢复-apps.bat` | **GBK + CRLF**（cmd 代码页 936） | 双击即恢复：自动执行 `winget import -i` |
| `无法自动重装清单-apps.html` | UTF-8 **无 BOM**、CRLF、单文件 | 可勾选的待办页：按软件/运行库分节，进度自动保存，每项可一键搜官网 |

`apps.json` 结构（`Sources` 按源名字典序排列，所以 msstore 在 winget 前面）：

```json
{
  "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
  "CreationDate": "2024-05-01T14:23:05.120+08:00",
  "Sources": [
    {
      "Packages": [ { "PackageIdentifier": "Microsoft.VisualStudioCode" } ],
      "SourceDetails": { "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name": "winget", ... }
    }
  ],
  "WinGetVersion": "1.10.0"
}
```

> 清单里**故意不写 Version**：版本写死会在该包从源下架后让恢复失败；
> `--ignore-unavailable` 会自动跳过已下架的包，不影响其余安装。

恢复时把三个文件放到任意目录，双击 `恢复-apps.bat`（建议以管理员运行）即可。

---

## 数据文件位置

普通模式下所有数据都在 `%LOCALAPPDATA%\WingetManager\` 下；
便携模式下改为 **exe 同目录的 `Data\`**（见上文「便携模式」），文件结构完全一致：

| 文件 | 内容 | 与原版互通 |
|---|---|---|
| `selection.json` | 勾选表（包 ID → bool） | ✅ 完全互通（严格布尔语义） |
| `metadata-cache.json` | 元数据核查缓存（schema 1） | ✅ 完全互通 |
| `settings.json` | 窗口几何 / 日志高度 / 分组 / 上次导出路径 | ⚠️ 原版用 QSettings 写注册表，不互通 |
| `logs\app.log` | 运行日志（512KB × 2 轮转） | ✅ 同格式 |

删除整个目录即可完全重置（勾选、缓存、设置一起清掉）。

---

## 目录结构

```
WingetManagerWpf/
├── WingetManagerWpf.sln
├── README.md
├── build-release.bat           # 一键打出 dist\WingetManager.exe（单文件绿色版）
├── dist/                       # 脚本产物（构建后生成，仅一个 exe）
├── docs/
│   ├── PRD.md                  # 需求文档
│   ├── ARCHITECTURE.md         # 架构设计（含 §10 易错点、§11 实现偏差）
│   ├── FEATURE-MAPPING.md      # 与原版的功能/UI 对应关系 + 已知差异
│   └── UI-DESIGN.md            # v2「清朗 Azure」重设计说明（色彩/字体/间距/响应式/可用性）
├── tests/SelfTest/             # 32 个用例的离线自测工程（控制台，链接源码）
├── tools/
│   └── make_icon.py            # 一次性图标生成脚本（纯标准库）
└── WingetManagerWpf/
    ├── WingetManagerWpf.csproj # net48 WPF，零 NuGet，LangVersion 钉死 7.3
    ├── Assets/app.ico          # 由 make_icon.py 生成（256/64/48/32/16 五档）
    ├── App.xaml(.cs)           # 启动：单实例、全局异常兜底
    ├── Theme/                  # Tokens.xaml（设计令牌）+ Styles.xaml（控件样式）
    ├── Core/                   # 配置、路径、日志、原子写盘、编码、进程、JSON
    ├── Models/                 # Package / AppSettings / ExportResult …
    ├── Services/               # 扫描编排、表解析、元数据核查、导出产物
    ├── ViewModels/             # 主状态机、筛选、勾选、计数
    ├── Controls/               # 行控件、吸顶头、日志面板、空态、骨架屏 …
    └── Views/                  # 主窗口 + 导出 / 关于 / winget 引导对话框
```

---

## 快捷键

| 键 | 行为 |
|---|---|
| `Ctrl+F` | 聚焦搜索框 |
| `Ctrl+A` | 全选（焦点在搜索框内时 = 全选文本） |
| `Ctrl+Shift+A` | 全不选 |
| `Ctrl+I` | 反选 |
| `Ctrl+E` | 导出配置 |
| `F5` | 重新扫描（扫描中按 = 提示先取消） |
| `Esc` | 清空搜索词（对话框里 = 关闭对话框） |
| `↑ / ↓` | 列表内行间移动焦点 |
| 空格 | 切换焦点行的勾选 |

---

## 故障排查

**扫描一直转 / 报「winget 不可用」**

winget 的 COM 接口有**全局互斥**：同时跑两个 winget 命令会撞 `0x8A150001`。
本工具已经做了互斥让路（扫描前取消元数据核查、命令级重试退避），
如果还是失败，先关掉其它 winget 任务（VSCode 插件、`winget upgrade --all` 定时任务等）再扫。
用 `WPM_WINGET_EXE` 指定明确的 exe 路径也能绕开一部分探测问题。

**日志里的中文是乱码**

日志文件是 UTF-8；用记事本/VSCode 打开即可。「保存…」导出的日志也 UTF-8。
恢复脚本必须保持 **GBK**——不要用编辑器「顺手」把它转成 UTF-8，cmd 在 936 代码页下会读成乱码。

**导出失败 / 覆盖不了**

文件名不能含 `\ / : * ? " < > |`；含 emoji 的文件名会因为 GBK 编不进去被拒绝
（恢复脚本依赖 GBK）。路径不可写（OneDrive 同步锁、权限）时换一个目录重试。

**扫描结果里软件被归错类（软件 ↔ 运行库）**

分类由「目录关键词初判 + 元数据核查改判」两级完成，核查结果缓存在 `metadata-cache.json`。
删除该文件后重新扫描即可重新核查。

**窗口跑到屏幕外**

删除 `settings.json`（或整个数据目录）后启动，窗口会回到系统居中。

**程序双开没反应**

单实例设计：第二个进程检测到已有实例就退出。想同时跑两份（不推荐，会撞 winget 互斥）
需要改 `Core/SingleInstance.cs` 里的 Mutex 名。
