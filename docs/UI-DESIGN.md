# UI 重设计说明 — v2「清朗 Azure」

> 后续方案见 `ui-v3/UI-DESIGN-v3.md`（v3「清单工作台」：列表优先布局 + 上下文选择条 + 可交互原型）。
> 本文档描述的是当前线上实现，v3 尚未落地。

> 适用版本：WingetManagerWpf v2.18.0（WPF 重制版）
> 生效范围：`Theme/Tokens.xaml`（全量）、`Theme/Styles.xaml`、`MainWindow`、`PackageRowControl`、`app.ico`
> 兼容性：所有旧令牌键名保留，未引用任何新第三方库；`tests/SelfTest` 445 项断言全部通过

---

## 1. 视觉风格方向

从 v1（克隆原版的「靛紫玻璃拟态」）转向 **Fluent 式清晰浅色主题**，三个关键词：

| 关键词 | 含义 | 落地手段 |
|---|---|---|
| **清晰层级** | 页面底 → 卡片 → 控件三层明度差拉开，不再靠阴影撑层次 | 页面底压深为 `#F3F5F9`，卡片纯白 + hairline 描边 `#E4E8F0`；阴影减淡（深度 8→4、不透明度 0.10→0.06） |
| **克制的品牌色** | 主色只在「可点击 / 已选中 / 进行中」出现，其余一律中性色 | 主色 Azure 蓝 `#2563EB→#0EA5E9` 对角渐变；正文、ID、标签全部走灰阶 |
| **呼吸感栅格** | 4px 基准栅格，节奏统一 | 页面边距 24、组件间距 8/10/12、行内间距 4 的倍数 |

**换主色的理由**：靛紫 `#6366F1` 在白底上的对比度只有 3.9:1，作为文字色不满足 WCAG AA；
Azure 蓝 `#2563EB` 对比度 5.2:1，既能当品牌色也能当可读文字色，一套色两用。

## 2. 色彩系统（Tokens.xaml 全量刷新）

### 2.1 品牌色

| 令牌 | v1（靛紫） | v2（Azure） | 用途 |
|---|---|---|---|
| `Brush.Accent` | `#6366F1` | `#2563EB` | 焦点描边、光标、链接 |
| `Brush.AccentDeep` | `#4F46E5` | `#1D4ED8` | 按下态、强调文字 |
| `Brush.Accent2` | `#8B5CF6` | `#0EA5E9` | 渐变终点、次级强调 |
| `Brush.AccentGradient` | 靛→紫 | 蓝→青 | 主按钮、选中胶囊、统计卡数字、品牌标 |
| `Brush.AccentSoft` | `#EEF0FF` | `#EFF6FF` | 选中底、toggle 选中态 |
| `Brush.AccentLine` | `#D9DCFF` | `#BFDBFE` | hover 描边 |

### 2.2 中性色（整体压深一档）

| 令牌 | v1 | v2 | 说明 |
|---|---|---|---|
| `Brush.Bg` | `#F4F5FA` | `#F3F5F9` | 页面底，与卡片拉开明度差 |
| `Brush.Text` | `#181C28` | `#101828` | 正文更深，长列表更耐读 |
| `Brush.Muted` | `#6A7183` | `#5D6576` | 次级文字，对比度 7:1 |
| `Brush.Line` | `#E8EAF3` | `#E4E8F0` | 描边略清晰 |

### 2.3 语义与状态色

- 错误 `#DC2626`（原 `#C53030`，更接近 Fluent 惯例）、成功日志 `#067647`、跳过 `#0E7490`（原紫改青，日志色彩语义与品牌色解耦）
- **新增 `Brush.RowSelected` `#EDF3FF`**：勾选行整行底色（见 §5 可用性 #1）
- **新增 `Brush.FocusRing` `#93B4FB`**：键盘焦点环（见 §5 可用性 #2）
- 来源标签从紫改为品牌蓝系（`TagSrcFg #1D4ED8` / `Bg #EFF6FF` / `Line #DBEAFE`），全屏色彩家族从 4 个收敛到 2 个（蓝 + 灰 + 少量琥珀/红）

### 2.4 玻璃拟态修正

`Brush.Glass` 不透明度 0.78 → 0.85。没有实时模糊的「假玻璃」越透越显脏：透出的列表文字会与工具条文字叠影。提高一档后接近实心浅卡，层次交由描边与阴影表达——这是对 v1 近似方案缺陷的修正，而非风格偏好。

## 3. 字体与字号

- **字族**：`Segoe UI Variable Text` 提到回退链最前（Win11 自带），西文走 Fluent 原生字形，中文回退 HarmonyOS Sans SC → Microsoft YaHei UI；等宽 `Cascadia Mono → Consolas`
- **字号层级重排**（不再按原版 pt 换算，全部取 0.5 的倍数，避免 125%/150% DPI 下半像素模糊）：

| 层级 | 令牌 | v1 | v2 | 使用处 |
|---|---|---|---|---|
| H1 | `Title` | 25.3 | 24 | 窗口标题 |
| H2 | `DialogTitle` / `Stat` | 22.7 / 21.3 | 21 / 22 | 对话框标题 / 统计数字 |
| Body | `Row` / `Body` | 13.3 / 12.7 | 13.5 / 13 | 列表行主名 / 控件正文 |
| Caption | `RowId` / `Log` / `Sub` | 11.3 | 11.5 | ID 列 / 日志 / 副标题 |
| Mini | `Tag` / `Chip` | 10.7 / 11.7 | 11 / 12 | 标签 / 胶囊 |

原则：**正文与标题拉开、辅助信息不动**——v1 各层级挤在 11–25 之间，v2 让 H1↔Body 差距扩大到 1.8 倍，扫读时层级立现。

## 4. 形状、间距与组件样式

- **圆角阶收敛**：4 / 6 / 8 / 12（v1 的 16 大圆角 + 重阴影是「气球感」来源；卡片 16→12，行 9→8）
- **阴影策略**：工具条/底栏 `BlurRadius 26→22, ShadowDepth 8→4, Opacity 0.10→0.06`——hairline 描边承担边界，阴影只做「浮起」暗示
- **滚动条** 10→12px、thumb 圆角 4：可抓取面积 +44%（Fitts 定律）
- **按钮**：主/柔和按钮底框补 1px 透明描边，为焦点环预留位置而不引起布局位移；高度维持 38/34，命中目标不变
- **`app.ico`**：随新品牌色重新生成（`tools/make_icon.py` 的渐变端点同步改蓝青），任务栏/标题栏/对话框图标风格与界面一致

## 5. 交互与可用性改进点

| # | 改进 | 实现位置 | 解决的问题 |
|---|---|---|---|
| 1 | **勾选行整行着色**（不只复选框打勾） | `PackageRowControl.ApplyCheckedState` → `Brush.RowSelected` | 批量全选/筛选后，勾选状态原本只有一个 19px 方块承载；现在整行浅蓝，导出范围一眼可辨。行复用换绑时会清除残留底色 |
| 2 | **键盘焦点环** | `Styles.xaml` 四类按钮 + toggle 增加 `IsKeyboardFocused` 触发器 | v1 只有搜索框有可见焦点态，Tab 键用户不知道焦点在哪；现在主/次/柔和/胶囊按钮 Tab 上去有 1px 蓝环，鼠标点击不触发、不干扰普通操作 |
| 3 | **响应式页面留白** | `MainWindow.OnSizeChanged` 切换 `Thickness.Page*` 令牌（DynamicResource） | v1 固定 26px 边距：窄窗（<1080）时内容区被挤掉约 4% 宽度；现在宽窗 24px 呼吸感、窄窗 14px 让位给数据列。同一份 XAML，无需两套布局 |
| 4 | **滚动条加宽** 10→12px | `Styles.xaml` | 长列表（数百行）滚动抓取更稳 |
| 5 | **玻璃工具条去叠影** | `Brush.Glass` 0.78→0.85 | v1 工具条下的列表文字会透上来与搜索框文字叠影 |
| 6 | **色觉友好** | 语义色收敛为「蓝=品牌/信息、青=跳过、绿=成功、琥珀=警告、红=失败」 | v1 的紫色标签与靛紫品牌色难以区分；v2 各语义唯一占位，不依赖色相附近的微差 |

**保留不动的交互**（它们本来就对）：全部快捷键（Ctrl+F/A/I/E、F5、Esc、行内 Space/Enter）、右键菜单 5 项、130ms 搜索防抖、300ms 勾选落盘、吸顶分组头、<1080 隐藏标签/版本列、日志面板拖高与折叠。重设计只动「看起来如何」与「状态是否可见」，不动「怎么操作」。

## 6. 响应式适配规则（桌面窗口）

| 断点 | 行为 |
|---|---|
| `ActualWidth ≥ 1080` | 舒适模式：页面边距 24px，标签列 + 版本列可见 |
| `ActualWidth < 1080` | 紧凑模式：页面边距 14px，标签列与版本列隐藏（不重建行，滚动位置与焦点不丢） |
| `MinWidth 940` | 最小可用宽度；此时搜索框弹性收缩（`MaxWidth 420` + `ColumnDefinition *`），批量按钮组恒可见 |

说明：WPF 桌面应用的「响应式」是**窗口级**而非设备级——同一套视觉令牌在 940–2560px 宽度间通过「边距换挡 + 列裁剪 + 弹性搜索框」适配，不涉及触控目标改版。若后续要出小屏触控版，令牌系统已预留：改 `Metrics.RowHeight` 与按钮 Height 即可，无需动业务代码。

## 7. 落地文件清单

| 文件 | 改动 |
|---|---|
| `Theme/Tokens.xaml` | 全量重写（颜色/字族/字号/圆角/间距），新增 `Brush.RowSelected`、`Brush.FocusRing`、`Color.FocusRing`、`Thickness.Page*` 6 个令牌；旧键名全保留 |
| `Theme/Styles.xaml` | 主/柔和按钮加透明描边底框 + 焦点环触发器；Outline/ToggleSoft 加焦点环；滚动条 12px |
| `Views/MainWindow.xaml` | 6 处页面级边距改 `DynamicResource Thickness.Page*` |
| `Views/MainWindow.xaml.cs` | `ApplyResponsivePageMargin()`：宽窄窗切换边距令牌 |
| `Controls/PackageRowControl.xaml.cs` | 勾选行着色 + 行复用清色 + MouseLeave 回静止态 |
| `tools/make_icon.py` + `Assets/app.ico` | 图标渐变同步新品牌色并重新生成 |
| `.workbuddy/checks/check_csproj.py` | 字号契约表更新为 v2 值 |

验证：`check_csproj.py` 与 `qa_verify.py` 静态检查 PASS（仅 2 条预期 MINOR：紧凑边距令牌由运行时赋值，静态扫描不可见）；MSBuild 编译 0 error；`tests/SelfTest` 445 项断言全部通过。
