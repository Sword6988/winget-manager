# Winget 软件管家

一个跑在本机的 winget 图形化工具：**枚举已安装软件 → 勾选要保留的 → 导出 winget 官方清单 + 双击即恢复的批处理**。

> **只做「导出」，不做「导入」。** 恢复由 `winget import` 或导出时生成的 `.bat` 完成，本工具不解析回读清单。
> 2.14.0 起移除了历史上的「导入恢复」界面与相关代码（`services/jobs.py` 等约 550 行）。

---

## 一、它解决什么问题

重装系统后逐个下载软件很费时间。本工具把当前机器的软件清单导出成 winget 官方格式，重装后一条命令（或双击一个 bat）批量装回来。

只列出**能被 winget 重装**的软件；MSIX/UWP、注册表安装、源里已下架的软件会灰显并归入「其他」分组，仅作核对用。

## 二、三种形态

| 形态 | 产物 | 说明 |
|---|---|---|
| 单文件 exe | `dist/WingetManager.exe` | 双击即用，首次启动会解包到临时目录（启动稍慢） |
| 便携目录（推荐） | `dist/WingetManager/` | 冷启动快，整个文件夹可打包分发 |
| 源码运行 | `qt_app.py` | 需要 Python + PySide6，见「开发」一节 |

## 三、环境要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 1809+ / Windows 11 |
| winget | 1.6+（`winget --version` 能出版本号）；缺失时从 <https://aka.ms/getwinget> 安装「应用安装程序」 |
| 运行 exe | 无（已内置 Python 运行时） |
| 源码运行 | Python 3.14 + `pip install -r requirements.txt` |

**装到只读目录不会导致启动失败**：找不到 winget 或目录不可写时，程序照常起来并给出引导，不再弹框退出。

## 四、使用

1. 启动后自动扫描（`winget list` + `winget export`，通常 4-20 秒），底部显示进度，可随时点「取消扫描」——**取消会在 0.2 秒内真正终止 winget 子进程**，不会卡住界面。
2. 用搜索框（支持多个关键词、`id:` 前缀）、分类胶囊、「仅看已选」缩小范围。
3. 勾选要保留的软件。「全选/反选」只作用于**当前筛选出的可见项**，不动屏幕外的勾选。
4. 点右下角「导出配置」，选保存位置。

导出会在同一目录生成三个文件：

| 文件 | 作用 |
|---|---|
| `apps.json` | winget 官方 import 格式，`winget import -i apps.json` 可直接用 |
| `恢复-apps.bat` | 双击即恢复：自动检查 winget/管理员权限，带 `--ignore-unavailable` 跳过已下架包 |
| `无法自动重装清单-apps.txt` | 无法自动重装的软件清单，供重装后手动安装时核对 |

> `apps.json` 是主产物；后两个是便利文件，删掉也不影响清单本身。

### 重装系统后怎么恢复

把导出目录带到新系统，二选一：

```bat
:: 方式一：双击
恢复-apps.bat

:: 方式二：命令行
winget import -i apps.json --accept-package-agreements --ignore-unavailable
```

部分机器级安装包需要管理员权限；msstore 来源的包可能要求先登录微软商店。

## 五、配置项

只保留三个真正生效的环境变量（其余历史项已随导入功能一起移除）：

| 变量 | 默认 | 说明 |
|---|---|---|
| `WPM_WINGET_EXE` | 自动查找 | 指定 winget.exe 路径；指向不存在文件时会给出明确提示 |
| `WPM_CACHE_TTL` | 60 | 目录缓存秒数，0 表示每次都重扫 |
| `WPM_COMMAND_TIMEOUT` | 180 | 单条 winget 命令超时（秒），10-3600 |

## 六、数据与日志

| 内容 | 位置 |
|---|---|
| 勾选状态 | `%LOCALAPPDATA%\WingetManager\selection.json`（原子写，损坏会自动回退为空） |
| 运行日志 | `%LOCALAPPDATA%\WingetManager\logs\app.log`（512KB × 2 轮转） |
| 窗口/筛选/上次导出路径 | 注册表 `HKCU\Software\Shibeng\WingetManager`（QSettings） |
| 导出默认位置 | 上次使用的路径 → `D:\apps.json`（若存在 D 盘）→ `文档\apps.json` |

日志里会记录 winget 的完整命令行参数（含路径），对外反馈日志时请留意脱敏。

## 七、开发

### 目录结构

```
qt_app.py          入口：主窗口、ScanWorker、状态机、快捷键
qt_ui.py           自绘控件与对话框（PackageRow / ExportDialog / AboutDialog）
qt_common.py       设计令牌、绘制工具、路径常量
core/              config（环境变量）/ errors（类型化异常）/ fsio（原子写）
                   logging_setup（唯一日志入口）/ version（唯一版本号）
services/          winget_cli（唯一碰子进程的地方）/ catalog（枚举+分类+缓存）
                   winget_table（解析 winget list 表格）/ profiles（清单与恢复脚本生成）
tools/             test_smoke（测试）/ check_globals、lint_unused（静态检查）
                   gen_version_info（版本资源生成）/ _bench、_shot 等一次性脚本
legacy/            历史 Tk / 网页版归档，不参与构建
docs/              设计规范与历史截图，不参与构建
```

### 跑起来 / 跑测试

```bat
:: 运行（会挑一个装了 PySide6 的解释器）
启动工具.bat

:: 或直接跑
py -3.14 qt_app.py

:: 测试（22 例，全部离屏，不依赖真实 winget）
set QT_QPA_PLATFORM=offscreen
py -3.14 tools\test_smoke.py

:: 静态检查
py -3.14 tools\lint_unused.py
py -3.14 tools\check_globals.py
```

测试覆盖：清单结构、跳包、分组与吸顶、勾选与范围全选、搜索分词、恢复脚本转义/编码、
原子写、BOM 解码、勾选损坏恢复、取消生命周期、可取消子进程、缓存与导出失败可见性。

### 打包

```bat
py -3.14 -m PyInstaller --noconfirm WingetSoftwareManagerQt-onedir.spec   :: 便携目录（推荐）
py -3.14 -m PyInstaller --noconfirm WingetSoftwareManagerQt.spec          :: 单文件
```

两个 spec 都输出 `WingetManager`，且 `upx=False`（UPX 压缩 Qt DLL 容易触发杀软误报和加载失败）。

### 改版本号

只改 `core/version.py` 里的 `APP_VERSION`，然后：

```bat
py -3.14 tools\gen_version_info.py
```

`version_info.txt` 由脚本生成（幂等），不要手改；界面显示的版本号与 exe 属性都取这一个来源。

## 八、已知限制

- 仅 Windows；依赖 winget 本体。
- 无法重装的软件只能列出来，不能自动安装。
- 分类靠名称/包 ID 的关键词匹配，个别软件可能落到「其他」，可在导出后手工调整 JSON。
- `winget list` 表格按「显示宽度 + 中文表头」解析，切换系统显示语言后若列错位，日志里会有兜底解析记录。

## 九、变更记录

**2.14.0（2026-11-05）**
- 移除「导入恢复」功能与相关死代码；恢复改由 winget 官方命令 / 生成的 bat 完成。
- 修复：扫描中「取消」导致进程崩溃（QThread 被提前析构，exit 127）。
- 修复：冻结版把 exe 目录插入 `sys.path` 的加载劫持风险。
- 修复：导出/勾选写盘改为原子写，覆盖失败不再毁掉旧清单。
- 修复：装到只读目录或缺少 winget 时无法启动。
- 修复：恢复脚本对 `&`、`%` 的文件名注入与 GBK 编码失败静默丢字符。
- 修复：取消扫描现在会真正终止 winget 子进程（0.2s 内），重扫不再被内部锁堵住。
- 修复：`winget export` 失败导致全部软件变灰且无提示；BOM 导致的列解析退化。
- 版本号收敛为单一来源；打包统一命名并关闭 UPX。
