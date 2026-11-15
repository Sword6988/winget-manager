using System;
using System.Globalization;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 全部中文文案的唯一来源（逐字对齐 <c>_ref/qt_app.py</c> 与 <c>_ref/qt_ui.py</c>）。
    /// 键名按区域前缀分组：<c>Title.*</c> 之外的成员用 <c>区域 + 语义</c> 命名。
    /// <para>
    /// 为什么集中：文案散落在 XAML / 代码里时，改一句话要全文搜索，而且极容易
    /// 在「底栏」和「副标题」里写出两种不同的说法。改文案只改这里。
    /// </para>
    /// 注意：{0} 之类的占位符顺序不要动，格式化助手已经按原版输出顺序固定。
    /// </summary>
    public static class Strings
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ══════════════ 窗口 / 品牌 ══════════════
        public const string WindowTitle = "Winget 软件管家";
        public const string AppTag = "WPF 桌面版";
        public const string AboutWindowTitle = "关于 Winget 软件管家";
        public const string SearchPlaceholder = "搜索名称或包 ID（空格分隔多个词）";

        // ══════════════ 分组 / 标签 ══════════════
        public const string CategoryAll = "全部";
        public const string TagNotReinstallable = "不可重装";
        public const string TagDependency = "运行库";

        // ══════════════ 按钮 / 链接 ══════════════
        public const string ButtonScan = "重新扫描";
        public const string ButtonCancelScan = "取消扫描";
        public const string ButtonOnlySelected = "仅看已选";
        public const string ButtonSelectAll = "全选";
        public const string ButtonSelectNone = "全不选";
        public const string ButtonInvert = "反选";
        public const string ButtonExport = "导出配置";
        public const string ButtonClearFilters = "清除筛选";
        public const string LinkSelectGroupAll = "全选本组";
        public const string LinkSelectGroupNone = "全不选";

        // ══════════════ 日志面板 ══════════════
        public const string LogPanelTitle = "运行日志";
        public const string LogCollapse = "收起";
        public const string LogExpand = "展开";
        public const string LogCopy = "复制";
        public const string LogSave = "保存…";
        public const string LogClear = "清空";
        public const string LogCopyTip = "复制全部日志到剪贴板，方便贴给别人排查";
        public const string LogSaveTip = "把日志另存为文本文件";
        public const string LogSaveDialogTitle = "保存日志";
        public const string LogEmpty = "日志还是空的";

        // ══════════════ 副标题 / 底栏 ══════════════
        // v3.4：标题栏只讲「扫描状态」，数字交给状态条与分段器。
        // 原来标题栏和状态条各说一遍同一组数字（95 / 60 / 41+19 / 35），
        // 在 1240 下标题栏那句还会被截成「…（归入「winget 不支持」…」。
        private const string SubtitleDoneTemplate = "扫描完成 · 用时 {0}s";

        // 位置描述去掉了：导出按钮 v3 起在底部居中的浮动条里，不再在右下角。
        // 现在只说「做了什么之后能导出」，只要按钮还在底部，这句话就不会再过期。
        private const string StatusIdleTemplate =
            "{0} 个{1} + {2} 个{3}可一键重装 · {4} 项 winget 装不上，已归入「{5}」 · 勾选后可导出重装配置";

        private const string StatusFilteredTemplate = "筛选出 {0} / 共 {1} 项";

        private const string StatusScanningTemplate = "正在扫描 · 已用 {0}s · 点「取消扫描」可中断等待";

        public const string StatusOnlySelectedSuffix = " · 仅显示已勾选";
        public const string StatusBoot = "正在扫描已安装软件…";
        public const string StatusScanFailed = "扫描失败";
        public const string StatusNoWinget = "未检测到 winget，无法扫描已安装软件";

        /// <summary>副标题（扫描完成）：只讲状态与耗时，数量交给状态条和分段器。</summary>
        public static string SubtitleDone(double seconds)
        {
            return string.Format(Inv, SubtitleDoneTemplate, seconds.ToString("0.0", Inv));
        }

        /// <summary>底栏（无筛选）：a 个软件 + b 个运行库可一键重装 · U 项 winget 装不上…</summary>
        public static string StatusIdle(int software, int dependency, int unsupported)
        {
            return string.Format(Inv, StatusIdleTemplate,
                software.ToString(Inv),
                Groups.Software,
                dependency.ToString(Inv),
                Groups.Dependency,
                unsupported.ToString(Inv),
                Groups.Unsupported);
        }

        /// <summary>底栏（有筛选）：筛选出 M / 共 N 项[ · 仅显示已勾选]</summary>
        public static string StatusFiltered(int matched, int total, bool onlySelected)
        {
            string text = string.Format(Inv, StatusFilteredTemplate,
                matched.ToString(Inv), total.ToString(Inv));
            return onlySelected ? text + StatusOnlySelectedSuffix : text;
        }

        /// <summary>底栏（扫描中）：正在扫描 · 已用 X.Xs · 点「取消扫描」可中断等待</summary>
        public static string StatusScanning(double seconds)
        {
            return string.Format(Inv, StatusScanningTemplate, seconds.ToString("0.0", Inv));
        }

        // ══════════════ 底栏数字区 ══════════════
        private const string SelectedSuffixTemplate = "/ {0} 项已勾选";

        /// <summary>底栏「/ R 项已勾选」。</summary>
        public static string SelectedSuffix(int reinstallable)
        {
            return string.Format(Inv, SelectedSuffixTemplate, reinstallable.ToString(Inv));
        }

        // ══════════════ flash 提示 ══════════════
        public const string FlashScanningBusy = "扫描进行中，请点「取消扫描」中断";
        public const string FlashCancelled = "已取消扫描 · 稍候可重新扫描";
        public const string FlashScanPending = "上一轮扫描仍在收尾，请稍候";
        public const string FlashDownloadLinkCopied = "下载地址已复制到剪贴板";
        public const string FlashExportNeedsSelection = "请先勾选需要保留的软件。";

        /// <summary>「已全选当前 N 项」/「已取消选中当前 N 项」。</summary>
        public static string FlashSelectAll(bool selected, int count)
        {
            return "已" + (selected ? "全选" : "取消选中") + "当前 " + count.ToString(Inv) + " 项";
        }

        /// <summary>「已反选当前 N 项」。</summary>
        public static string FlashInvert(int count)
        {
            return "已反选当前 " + count.ToString(Inv) + " 项";
        }

        /// <summary>「已复制 N 行日志」。</summary>
        public static string FlashLogCopied(int lines)
        {
            return "已复制 " + lines.ToString(Inv) + " 行日志";
        }

        /// <summary>「日志已保存：xxx.log」。</summary>
        public static string FlashLogSaved(string fileName)
        {
            return "日志已保存：" + (fileName ?? string.Empty);
        }

        /// <summary>「保存失败：…」。</summary>
        public static string FlashLogSaveFailed(string detail)
        {
            return "保存失败：" + (detail ?? string.Empty);
        }

        // ══════════════ 空态 ══════════════
        public const string EmptyNoWinget =
            "未检测到 winget\n安装「应用安装程序 / App Installer」后点击「重新扫描」";

        public const string EmptyCancelled = "扫描已取消\n点击「重新扫描」重新开始";

        public const string EmptyScanFailed = "扫描失败：winget 未安装或不可用\n可点击「重新扫描」重试";

        public const string EmptyNoResult = "未发现可重装的软件";

        public const string EmptyCategoryNoSoftware = "该分组下暂无可重装的软件";

        public const string EmptyOnlySelected = "还没有勾选任何软件\n取消「仅看已选」可继续浏览并勾选";

        /// <summary>搜索无结果（未限定分组）。</summary>
        public static string EmptyNoSearchResult(string keyword)
        {
            return "未找到与“" + (keyword ?? string.Empty) + "”匹配的软件\n"
                   + "可尝试搜索软件名或包 ID，多个词用空格分隔";
        }

        /// <summary>搜索无结果（限定了分组）。</summary>
        public static string EmptyNoSearchResultInGroup(string group, string keyword)
        {
            return "在「" + (group ?? string.Empty) + "」中未找到与“" + (keyword ?? string.Empty) + "」匹配的软件";
        }

        /// <summary>「仅看已选」但当前分组里没有勾选项。</summary>
        public static string EmptyOnlySelectedInGroup(string group)
        {
            return "「" + (group ?? string.Empty) + "」里没有已勾选的软件";
        }

        /// <summary>「仅看已选」+「winget 不支持」分组（该组本来就不可勾选）。</summary>
        public const string EmptyOnlySelectedUnsupported =
            "「winget 不支持」里的软件 winget 都装不上，本来就不可勾选\n关闭「仅看已选」即可查看这些条目";

        // ══════════════ v3 视图三态的空态 ══════════════
        /// <summary>视图=已选，但一个都没勾。</summary>
        public const string EmptyViewSelected = "还没有勾选任何软件\n切回「全部」视图，挑几行要保留的再回来";

        /// <summary>视图=未选，且已全部勾完（这才是任务完成的样子，不是错误）。</summary>
        public const string EmptyViewUnselected = "没有漏掉的软件了\n全部可勾选的行都已勾上，可以直接导出";

        /// <summary>视图=未选 + 搜索/分组限定下的说明。</summary>
        public static string EmptyViewUnselectedInScope(string scope)
        {
            return "「" + (scope ?? string.Empty) + "」里没有漏掉的软件了";
        }

        // ══════════════ 日志 ══════════════
        public const string LogScanStart = "开始扫描已安装软件…";
        public const string LogScanCancelled = "已取消扫描，后台 winget 进程会在数秒内退出，本次结果不再采用。";

        /// <summary>扫描完成汇总行。</summary>
        public static string LogScanDone(int total, int reinstallable, int software, int dependency)
        {
            return "扫描完成：共 " + total.ToString(Inv) + " 个软件，其中可重装 " + reinstallable.ToString(Inv)
                   + " 个（" + Groups.Software + " " + software.ToString(Inv)
                   + " 个 + " + Groups.Dependency + " " + dependency.ToString(Inv) + " 个）；"
                   + "另有 " + (total - reinstallable).ToString(Inv) + " 项 winget 装不上，"
                   + "统一归入「" + Groups.Unsupported + "」分组（灰显、不可勾选），排在最后。";
        }

        /// <summary>扫描失败。</summary>
        public static string LogScanFailed(string detail)
        {
            return "扫描失败：" + (detail ?? string.Empty);
        }

        /// <summary>winget 不可用（只取首行前 160 字符）。</summary>
        public static string LogWingetMissing(string detail)
        {
            string firstLine = FirstLine(detail, 160);
            return "winget 不可用：" + firstLine;
        }

        /// <summary>恢复上次分组筛选。</summary>
        public static string LogCategoryRestored(string category)
        {
            return "已恢复上次的分组筛选「" + (category ?? string.Empty) + "」";
        }

        /// <summary>清理已卸载项残留勾选。</summary>
        public static string LogSelectionPurged(int count)
        {
            return "已清理 " + count.ToString(Inv) + " 项已卸载软件的勾选记录。";
        }

        // ── 元数据核查 ──
        public static string LogEnrichStart(int pending)
        {
            return "元数据核查：" + pending.ToString(Inv) + " 个新包待核查（后台进行，不占用界面，结果本地缓存）";
        }

        /// <summary>「元数据核查：{name} → 归入「{group}」（描述/标记命中「{signal}」）」。</summary>
        public static string LogEnrichChange(string name, string group, string signal)
        {
            string reason = string.IsNullOrEmpty(signal)
                ? "描述/标记未见运行库信号"
                : "描述/标记命中「" + signal + "」";
            return "元数据核查：" + (name ?? string.Empty) + " → 归入「" + group + "」（" + reason + "）";
        }

        public static string LogEnrichDone(int checkedCount, int promoted, int kept, int failed)
        {
            return "元数据核查完成：" + checkedCount.ToString(Inv) + " 个新包，"
                   + promoted.ToString(Inv) + " 个归入运行库，"
                   + kept.ToString(Inv) + " 个确认为软件，"
                   + failed.ToString(Inv) + " 个暂无法判定（下次扫描自动重试）。";
        }

        public static string LogEnrichInterrupted(int checkedCount, int changed)
        {
            return "元数据核查被新扫描打断：已核查 " + checkedCount.ToString(Inv)
                   + " 个（其中改判 " + changed.ToString(Inv) + " 个），未查完的下次扫描自动续上。";
        }

        // ── 导出相关失败 ──
        public const string LogRestoreScriptFailedPrefix = "恢复脚本生成失败（清单已导出）：";
        public const string LogUninstallListFailedPrefix = "无法重装清单生成失败：";
        public const string LogExportFailedPrefix = "导出失败：";

        /// <summary>导出开始（写进日志的首行）。</summary>
        public static string LogExportStart(int count, string path)
        {
            return "开始导出 " + count.ToString(Inv) + " 个软件包到 " + (path ?? string.Empty);
        }

        /// <summary>导出成功日志标题行的冒号（配合 <see cref="ExportDoneTitle"/> 用）。</summary>
        public const string LogExportDoneSuffix = "：";

        /// <summary>「其中 M 个来自 msstore，恢复时可能需登录微软商店」。</summary>
        public static string LogExportMsStoreSkip(int msStore)
        {
            return "其中 " + msStore.ToString(Inv) + " 个来自 msstore，恢复时可能需登录微软商店";
        }

        // ══════════════ 导出对话框 ══════════════
        public const string ExportTitle = "导出配置";
        public const string ExportPickTitle = "选择导出位置";
        public const string ExportPickFilter = "JSON 文件 (*.json)";
        public const string ExportButtonCancel = "取消";
        public const string ExportButtonChange = "更改…";
        public const string ExportButtonNow = "立即导出";
        public const string ExportButtonOverwrite = "覆盖导出";
        public const string ExportButtonClose = "关闭";
        public const string ExportBusy = "正在导出…";

        public const string ExportDescTemplate =
            "将已勾选的 {0} 个软件包以 winget 官方格式导出，重装系统后执行 winget import -i 即可一键恢复。";

        public const string ExportHintNormal = "winget import 标准格式，重装系统后可直接恢复";
        public const string ExportHintOverwrite = "目标文件已存在，导出将覆盖它";

        /// <summary>「 · 同目录另存「恢复-stem.bat」与「无法自动重装清单-stem.html」」。</summary>
        public static string ExportSameDirSuffix(string stem)
        {
            return " · 同目录另存「恢复-" + (stem ?? string.Empty) + ".bat」"
                   + "与「无法自动重装清单-" + (stem ?? string.Empty) + ".html」";
        }

        /// <summary>「 · 含 N 个 msstore 包（恢复时可能需登录商店）」。</summary>
        public static string ExportMsStoreSuffix(int msStore)
        {
            return " · 含 " + msStore.ToString(Inv) + " 个 msstore 包（恢复时可能需登录商店）";
        }

        /// <summary>导出前的描述行。</summary>
        public static string ExportDescription(int count)
        {
            return string.Format(Inv, ExportDescTemplate, count.ToString(Inv));
        }

        /// <summary>成功页标题「已导出 N 个软件包」。</summary>
        public static string ExportDoneTitle(int count)
        {
            return "已导出 " + count.ToString(Inv) + " 个软件包";
        }

        public const string ExportKindManifest = "清单文件";
        public const string ExportKindScript = "恢复脚本";
        public const string ExportKindChecklist = "待装页面";

        public const string ExportSuccessBatTip =
            "重装系统后不必记命令 —— 双击「恢复软件.bat」，它会自动执行 winget import 并逐包安装。";

        public const string ExportSuccessListTip =
            "「无法自动重装清单.html」是那份软件的手动待办页：双击用浏览器打开，按分类逐个装、勾选自动记住进度。";

        public const string ExportOpenFolder = "导出后打开文件夹";

        public const string ExportInvalidFileName = "文件名不能包含 < > : \" / \\ | ? * 等字符。";

        /// <summary>「文件名里有 emoji 等 GBK 无法表示的字符，请改用中文/英文/数字后重试。」</summary>
        public const string ExportGbkUnsupported =
            "文件名里有 emoji 等 GBK 无法表示的字符，请改用中文/英文/数字后重试。";

        // ══════════════ 关于对话框 ══════════════
        public const string AboutTitle = "Winget 软件管家";
        public const string AboutIntro =
            "清点本机已安装软件，把可自动重装的包导出为 winget 官方清单；重装系统后执行 winget import -i apps.json 即可批量恢复。";

        /// <summary>便携模式追加的一行说明（只在便携模式下拼到 intro 后面）。</summary>
        public const string AboutPortableHint =
            "当前为便携模式：配置、勾选记录与日志都在 exe 同目录的 Data 文件夹里，整个文件夹拷走即可换机，删除后不留任何残留。";

        public const string AboutCopyDiag = "复制诊断信息";
        public const string AboutCopied = "已复制";
        public const string AboutOpenDataDir = "打开数据目录";
        public const string AboutClose = "关闭";
        public const string AboutRowExportPath = "导出路径";
        public const string AboutRowSelectionFile = "配置文件";
        public const string AboutRowLogFile = "运行日志";
        public const string AboutLogNotCreated = "本次未产生";

        /// <summary>「版本 2.18.0 · WPF 桌面版」。</summary>
        public static string AboutVersionLine()
        {
            return "版本 " + AppVersion.Version + " · " + AppTag;
        }

        // ══════════════ winget 引导对话框 ══════════════
        public const string GuideWindowTitle = "未检测到 winget";
        public const string GuideText = "本工具依赖 Windows 包管理器 winget，当前无法调用。";
        public const string GuideOpenStore = "打开应用商店";
        public const string GuideCopyLink = "复制下载地址";
        public const string GuideOk = "知道了";
        public const string GuideStoreUri = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";
        public const string GuideDownloadUrl = "https://github.com/microsoft/winget-cli/releases";
        public const string GuideInstallerUrl = "https://aka.ms/getwinget";

        private const string GuideDetailTemplate =
            "winget 由微软「应用安装程序 / App Installer」提供。\n"
            + "Windows 10 1809 及以上可在 Microsoft Store 安装，或从 GitHub 下载 winget-cli 的 msixbundle 手动安装。\n\n"
            + "系统返回：{0}";

        /// <summary>引导框正文（含系统返回的首行摘要）。</summary>
        public static string GuideDetail(string detail)
        {
            return string.Format(Inv, GuideDetailTemplate, FirstLine(detail, 140));
        }

        // ══════════════ 右键菜单 ══════════════
        public const string MenuCopyPackageId = "复制包 ID";
        public const string MenuCopyName = "复制软件名称";
        public const string MenuCopyNameAndId = "复制「名称 (包ID)」";
        public const string MenuCopyInstallCommand = "复制安装命令";
        public const string MenuToggleCheck = "勾选 / 取消勾选";
        public const string FlashCopiedPackageId = "已复制包 ID";
        public const string FlashCopiedName = "已复制软件名称";
        public const string FlashCopiedNameAndId = "已复制「名称 (包ID)」";
        public const string FlashCopiedInstallCommand = "已复制安装命令（可直接粘到终端）";

        /// <summary>取首行并截断（过长的 winget 报错会把引导框撑爆）。</summary>
        private static string FirstLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "命令不存在";
            }

            string normalized = EncodedText.NormalizeNewlines(text);
            int index = normalized.IndexOf('\n');
            string first = index >= 0 ? normalized.Substring(0, index) : normalized;
            first = first.Trim();
            if (first.Length > maxLength)
            {
                first = first.Substring(0, maxLength);
            }

            return first.Length == 0 ? "命令不存在" : first;
        }
    }
}
