# QA-REPORT 原始数据（由 tools/qa_verify.py 生成，请勿手工编辑）

- 被测工程: .
- 文件数: .xaml=14, .cs=58
- 问题统计: BLOCKER=0, MAJOR=0, MINOR=39

| severity | code | file:line | message |
| --- | --- | --- | --- |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Border.Card" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Border.Glass" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.Accent2" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.BarOnSelected" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.GradH1" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.GradH2" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.HeadBot" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.HeadTop" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.TagDimBg" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.TagDimFg" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.TagDimLine" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.ToastOk" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Brush.WarnLine" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Button.Pill" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Button.Soft" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Button.ToggleSoft" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Color.Accent" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Color.Accent2" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Color.AccentDeep" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Color.Bg" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Color.FocusRing" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Corner.Menu" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "FontSize.Bottom" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "FontSize.Stat" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "FontSize.Sub" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "FontSize.Title" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Space.Card" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Space.Page" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Space.Row" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Text.Aux" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Text.Link" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Thickness.Page" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Thickness.PageCompact" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Thickness.PageGap" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Thickness.PageGapCompact" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Thickness.PageTop" 定义后未被任何文件引用（死令牌） |
| MINOR | RES-UNUSED | Theme:0 | 资源键 "Thickness.PageTopCompact" 定义后未被任何文件引用（死令牌） |
| MINOR | XAML-DUPNAME | Theme/Styles.xaml:0 | x:Name="Bd" 在同文件出现 15 次（均在 ControlTemplate 内，独立 namescope 不冲突） |
| MINOR | XAML-DUPNAME | Theme/Styles.xaml:0 | x:Name="PART_Track" 在同文件出现 2 次（均在 ControlTemplate 内，独立 namescope 不冲突） |

## 文案一致性抽查（18 条，PASS 的不列）

- [PASS] '该分组下暂无可重装的软件'  ← 原版 qt_app.py:1503
- [PASS] '可尝试搜索软件名或包 ID，多个词用空格分隔'  ← 原版 qt_app.py:1501
- [PASS] '正在扫描已安装软件…'  ← 原版 qt_app.py:329
- [PASS] '搜索名称或包 ID（空格分隔多个词）'  ← 原版 qt_app.py:361
- [PASS] '扫描进行中，请点「取消扫描」中断'  ← 原版 qt_app.py:288
- [PASS] '已取消扫描，后台 winget 进程会在数秒内退出，本次结果不再采用。'  ← 原版 qt_app.py:1058
- [PASS] '已恢复上次的分组筛选「'  ← 原版 qt_app.py:1098
- [PASS] '（没有这类软件，全部都能自动重装）'  ← 原版 services/profiles.py:165
- [PASS] 'chcp 936 >nul'  ← 原版 services/profiles.py:103
- [PASS] 'title 恢复软件清单'  ← 原版 services/profiles.py:104
- [PASS] 'cd /d "%~dp0"'  ← 原版 services/profiles.py:105
- [PASS] 'echo   Winget 软件管家 · 恢复脚本'  ← 原版 services/profiles.py:126
- [PASS] 'echo   源里已下架的包会被自动跳过，不影响其余安装。'  ← 原版 services/profiles.py:132
- [PASS] 'echo [x] 未找到 winget，请先安装「应用安装程序 / App Installer」。'  ← 原版 services/profiles.py:110
- [PASS] '恢复脚本不会处理它们'  ← 原版 ExportProfiles.cs（改版后）:0
- [PASS] 'required to run'  ← 原版 services/metadata.py:27
- [PASS] '个来自微软商店 msstore'  ← 原版 services/profiles.py:99
- [PASS] ' · 勾选后可导出重装配置'  ← 原版 qt_app.py:1599
