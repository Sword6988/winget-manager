using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WingetManagerWpf.Core;
using WingetManagerWpf.Core.Json;
using WingetManagerWpf.Models;
using WingetManagerWpf.Services;
using WingetManagerWpf.ViewModels;
using WingetManagerWpf.Views;

namespace WingetManagerWpf.SelfTest
{
    /// <summary>
    /// WingetManagerWpf 逻辑自测（QA 独立验证）。
    /// <para>
    /// 每个用例注明「对应原版哪个行为」——原版指 GitHub 项目 Sword6988/winget-manager
    /// 的 Python 参考实现（_ref/），测试输入样例已用原版解析器逐条验证过。
    /// </para>
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static string _current = "(init)";

        private static void Main()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些宿主不支持改控制台编码：中文输出可能乱码但不影响结果判定
            }

            Console.WriteLine("WingetManagerWpf SelfTest (net48, 零依赖)");
            Console.WriteLine("对照组：_ref（Python 原版）行为");
            Console.WriteLine(new string('=', 64));

            // ── 1~3 显示宽度 / 编码解码（对应 _ref/services/winget_table.py 的 display_width
            //    与 _ref/services/winget_cli.py 的输出解码顺序） ──
            RunCase("T01 显示宽度：中文=2列、ASCII=1列（winget_table.py:53 display_width）", CaseDisplayWidth);
            RunCase("T02 中文表头 winget list 解析（winget_table.py:100 parse_list_output）", CaseParseChineseHeader);
            RunCase("T03 英文表头 winget list 解析（winget_table.py:18 _HEADER_LABELS）", CaseParseEnglishHeader);
            RunCase("T04 表头缺失时按 2+ 空格兜底（winget_table.py:140 _parse_by_gaps）", CaseParseFallbackGaps);
            RunCase("T05 空输出 → 0 条且不抛（winget_table.py:100）", CaseParseEmpty);
            RunCase("T06 带 BOM 的 UTF-8 解码（winget_cli.py 输出解码 utf-8-sig 优先）", CaseDecodeUtf8Bom);
            RunCase("T07 GBK 字节解码（cmd 代码页 936 输出）", CaseDecodeGbk);
            RunCase("T08 emoji 名称宽度与解析（display_width 代理对整体算 2 列）", CaseEmojiWidthAndParse);

            // ── 4~10 目录判定（对应 _ref/services/catalog.py） ──
            RunCase("T09 IsReinstallable 只看源列（catalog.py:74 _is_reinstallable）", CaseIsReinstallable);
            RunCase("T10 IsDependencyKeyword 关键词初判 + 19 词命中词（catalog.py:82 _is_dependency）", CaseIsDependencyKeyword);
            RunCase("T11 IdKindOf 包 ID 形态（catalog.py:88 _id_kind）", CaseIdKindOf);
            RunCase("T12 Groups 三分组与顺序（qt_app.py:1588 SOFTWARE_GROUP 常量）", CaseGroups);

            // ── 11~16 导出三产物（对应 _ref/services/profiles.py） ──
            RunCase("T13 NormalizeWingetVersion（profiles.py:20）", CaseNormalizeVersion);
            RunCase("T14 BuildManifest 结构（profiles.py:26 build_winget_manifest）", CaseBuildManifest);
            RunCase("T15 BuildManifest 空选择不抛（profiles.py:26 空列表）", CaseBuildManifestEmpty);
            RunCase("T16 BuildRestoreBat 主体（profiles.py:95 _render_restore_bat）", CaseRestoreBatBody);
            RunCase("T17 BuildRestoreBat % 转义与防目录穿越（profiles.py:83 basename）", CaseRestoreBatEscape);
            RunCase("T18 BuildUninstallableHtml（单文件 HTML：统计/筛选/勾选 key/搜索/空集合）", CaseUninstallableList);
            RunCase("T18b HTML 清单分节、类型标签、转义与内部路径不外泄", CaseUninstallableListSections);

            // ── 17~21 原子写盘与 JSON（对应 _ref/core/fsio.py） ──
            RunCase("T19 WriteTextAtomic 覆盖写、无 .tmp 残留（fsio.py:17 atomic_write_text）", CaseAtomicText);
            RunCase("T20 WriteJsonAtomic 无 BOM/2 空格缩进/尾换行（fsio.py:40 atomic_write_json）", CaseAtomicJson);
            RunCase("T21 JSON 序列化↔解析回环（json.dumps ensure_ascii=False 等价）", CaseJsonRoundtrip);
            RunCase("T22 JSON 解析容忍 BOM（metadata.py:150 read_text utf-8 留 BOM 的老文件）", CaseJsonBom);
            RunCase("T23 非法 JSON 抛 BadRequestException（core/errors.py BadRequest）", CaseJsonInvalid);

            // ── 24~28 状态文件（对应原版 settings/selection/metadata-cache 读写） ──
            RunCase("T24 SettingsStore 存取回环与损坏回退（原版 QSettings→JSON 等价语义）", CaseSettingsStore);
            RunCase("T25 SelectionStore 严格布尔读入（原版 isinstance(x,bool) 语义）", CaseSelectionTruthy);
            RunCase("T26 SelectionStore 保存后为 {id:true} 严格布尔（qt_app.py _save_selection）", CaseSelectionSave);
            RunCase("T27 RuntimeCache 严格 bool 与 schema 校验（metadata.py:131 RuntimeCache）", CaseRuntimeCache);
            RunCase("T27b RuntimeCache TTL：过期不采信、不删条目、不落盘（本轮改造 1）", CaseRuntimeCacheTtl);
            RunCase("T28 MetadataProbe.Analyze 信号词判定（metadata.py:91 analyze_runtime_signal）", CaseMetadataAnalyze);

            // ── 29~32 搜索 / 渲染 / ViewModel ──
            RunCase("T29 SearchQuery 语法与 AND（qt_app.py:742 _search_tokens / 758 _pkg_matches）", CaseSearchQuery);
            RunCase("T30 ListPresenter.Rebuild 三组顺序与过滤（qt_app.py 分组渲染等价）", CaseListPresenter);
            RunCase("T31 MainViewModel 批量勾选作用域（qt_app.py:1521 _scoped_packages）", CaseViewModelScopedKeys);
            RunCase("T32 EncodeText.NormalizeNewlines / JsonWriter 控制字符转义（fsio.py CRLF 保真）", CaseMiscUtils);

            // ── 33~37 运行库判定三项改造（TTL / 计数口径 / 关键词补漏） ──
            RunCase("T33 CatalogCounts 双口径与 JSON 双键（本轮改造 2）", CaseCatalogCountsJson);
            RunCase("T34 计数口径端到端：1 可重装运行库 + 1 不可重装运行库 + 1 软件（本轮改造 2）",
                CaseCatalogCountsScope);
            RunCase("T35 关键词命中诊断日志：明细排序 + 去重前口径差异（本轮改造 3）", CaseKeywordHitLog);
            RunCase("T36 TTL 与 PendingMetadataIds 联动：过期条目会重新核查（本轮改造 1）", CasePendingMetadataWithTtl);
            RunCase("T37 AppConfig 元数据 TTL：可选参数 + WPM_META_TTL_DAYS 校验（本轮改造 1）", CaseAppConfigMetadataTtl);

            // ── 38~40 全面审计修复的回归锁 ──
            RunCase("T38 JSON 嵌套深度上限：超深文件抛可捕获异常而不是 StackOverflow（审计修复）", CaseJsonDepthLimit);
            RunCase("T39 解码兜底识别 U+FFFD 乱码，GBK 兜底不会被静默跳过（审计修复）", CaseDecodeReplacementGuard);
            RunCase("T40 打包脚本只替换 exe，不删除 dist\\Data 便携数据（审计修复）", CaseBuildScriptKeepsData);

            Console.WriteLine(new string('=', 64));
            Console.WriteLine("总计: " + (_passed + _failed) + " 项断言, 通过 " + _passed + ", 失败 " + _failed);
            if (_failed > 0)
            {
                Console.WriteLine("结果: FAIL");
            }
            else
            {
                Console.WriteLine("结果: PASS");
            }

            Environment.ExitCode = _failed == 0 ? 0 : 1;
        }

        // ══════════════ 样例（已用 _ref/services/winget_table.py 验证过期望值） ══════════════

        private const string WingetListCn =
            "已找到 3 个已安装的、与可用升级版本兼容的程序包。" + "\r\n" +
            "名称                 ID                           版本      可用    源" + "\r\n" +
            "---------------------------------------------------------------------------" + "\r\n" +
            "谷歌浏览器           Google.Chrome                126.0.6           winget" + "\r\n" +
            "微软商店示例         9NBLGGH4NNS1                 2.7.1     3.0.0   msstore" + "\r\n" +
            "Visual Studio Code   Microsoft.VisualStudioCode   1.90.0            winget" + "\r\n" +
            "" + "\r\n";

        private const string WingetListEn =
            "The following packages are installed." + "\r\n" +
            "Name            Id                             Version   Available   Source" + "\r\n" +
            "--------------------------------------------------------------------------------" + "\r\n" +
            "Google Chrome   Google.Chrome                  126.0.6               winget" + "\r\n" +
            "VC++ Redist     Microsoft.VCRedist.2015+.x64   14.40.3   14.42.3     winget" + "\r\n" +
            "" + "\r\n";

        private const string WingetListEmoji =
            "名称        ID           版本   可用   源" + "\r\n" +
            "----------------------------------------------" + "\r\n" +
            "测试🦊App   Test.Emoji   1.0           winget" + "\r\n" +
            "" + "\r\n";

        private const string WingetListFallback =
            "名称  ID  版本  源" + "\r\n" +
            "谷歌浏览器  Google.Chrome  126.0.6  winget" + "\r\n" +
            "记事本  Microsoft.WindowsNotepad  11.2404  msstore" + "\r\n" +
            "" + "\r\n";

        // ══════════════ 运行库判定改造（TTL / 计数口径 / 关键词补漏）专用样例 ══════════════
        // 这三份样例故意不带「-----」分隔线，走 ParseByGaps 兜底路径（按 2+ 空格切列），
        // 每行的期望解析结果已用 parse_list_output 的等价逻辑逐行推演过：
        //   ① 可重装运行库（源=winget，ID 命中 vcredist）
        //   ② 不可重装运行库（无源列，ID 命中 vc_redist）
        //   ③ 普通软件（源=winget，不命中任何关键词）

        /// <summary>1 可重装运行库 + 1 不可重装运行库 + 1 普通软件。</summary>
        private const string WingetListCounts =
            "已找到 3 个已安装的、与可用升级版本兼容的程序包。" + "\r\n" +
            "名称            ID                              版本      源" + "\r\n" +
            "VC++ Redist     Microsoft.VCRedist.2015+.x64    14.40.3   winget" + "\r\n" +
            "Legacy Redist   Some.VC_redist.9               4.0" + "\r\n" +
            "谷歌浏览器      Google.Chrome                   126.0.6   winget" + "\r\n" +
            "" + "\r\n";

        /// <summary>全是普通软件（0 个关键词命中）。</summary>
        private const string WingetListPlain =
            "已找到 2 个已安装的、与可用升级版本兼容的程序包。" + "\r\n" +
            "名称            ID                              版本      源" + "\r\n" +
            "谷歌浏览器      Google.Chrome                   126.0.6   winget" + "\r\n" +
            "记事本          Microsoft.WindowsNotepad        11.2404   msstore" + "\r\n" +
            "" + "\r\n";

        /// <summary>同一 ID 出现两行（x86/x64），用于验证「初判计数按 raw 行累加」。</summary>
        private const string WingetListDuplicate =
            "已找到 3 个已安装的、与可用升级版本兼容的程序包。" + "\r\n" +
            "名称            ID                              版本      源" + "\r\n" +
            "VC++ x64        Microsoft.VCRedist.2015+.x64    14.40.3   winget" + "\r\n" +
            "VC++ x86        Microsoft.VCRedist.2015+.x64    14.40.3   winget" + "\r\n" +
            "谷歌浏览器      Google.Chrome                   126.0.6   winget" + "\r\n" +
            "" + "\r\n";

        // ══════════════ 断言工具 ══════════════

        private static void RunCase(string name, Action body)
        {
            _current = name;
            Console.WriteLine();
            Console.WriteLine("── " + name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail("用例意外抛出异常: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Fail(string detail)
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + detail + "   (case: " + _current + ")");
        }

        private static void True(bool condition, string what)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Equal(object expected, object actual, string what)
        {
            string e = expected == null ? "null" : expected.ToString();
            string a = actual == null ? "null" : actual.ToString();
            if (string.Equals(e, a, StringComparison.Ordinal))
            {
                _passed++;
                Console.WriteLine("  [PASS] " + what + " == " + Truncate(e));
            }
            else
            {
                Fail(what + " 期望 " + Truncate(e) + " 实际 " + Truncate(a));
            }
        }

        private static void Throws<T>(Action action, string what) where T : Exception
        {
            try
            {
                action();
                Fail(what + " 期望抛出 " + typeof(T).Name + "，但没有抛");
            }
            catch (T)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + what + " → " + typeof(T).Name);
            }
            catch (Exception ex)
            {
                Fail(what + " 期望 " + typeof(T).Name + "，实际 " + ex.GetType().Name);
            }
        }

        private static string Truncate(string text)
        {
            if (text == null || text.Length <= 60)
            {
                return text;
            }

            return text.Substring(0, 57) + "...";
        }

        // ══════════════ 用例实现 ══════════════

        /// <summary>原版 winget_table.py:53 display_width：汉字 2 列、ASCII 1 列。</summary>
        private static void CaseDisplayWidth()
        {
            Equal(4, EncodedText.DisplayWidth("名称"), "DisplayWidth(\"名称\")");
            Equal(4, EncodedText.DisplayWidth("Name"), "DisplayWidth(\"Name\")");
            Equal(0, EncodedText.DisplayWidth(""), "DisplayWidth(\"\")");
            Equal(2, EncodedText.DisplayWidth("\U0001F9A9"), "DisplayWidth(emoji) 代理对整体 2 列");
            Equal(1, EncodedText.DisplayWidth("e\u0301"), "DisplayWidth 组合字符 0 宽（e + 组合符 = 1）");
        }

        /// <summary>原版 winget_table.py:100 parse_list_output 中文表头 + 噪音行 + 分隔线。</summary>
        private static void CaseParseChineseHeader()
        {
            IList<RawPackage> result = WingetTableParser.Parse(WingetListCn);
            Equal(3, result.Count, "解析出 3 条记录");
            Equal("谷歌浏览器", result[0].Name, "[0] 名称");
            Equal("Google.Chrome", result[0].PackageId, "[0] ID");
            Equal("126.0.6", result[0].Version, "[0] 版本");
            Equal("", result[0].Available, "[0] 可用（空列）");
            Equal("winget", result[0].Source, "[0] 源");
            Equal("微软商店示例", result[1].Name, "[1] 名称");
            Equal("9NBLGGH4NNS1", result[1].PackageId, "[1] ID");
            Equal("2.7.1", result[1].Version, "[1] 版本");
            Equal("3.0.0", result[1].Available, "[1] 可用（有新版本）");
            Equal("msstore", result[1].Source, "[1] 源");
            Equal("Visual Studio Code", result[2].Name, "[2] 名称（含空格不切碎）");
            Equal("Microsoft.VisualStudioCode", result[2].PackageId, "[2] ID");
            Equal("1.90.0", result[2].Version, "[2] 版本");
            Equal("winget", result[2].Source, "[2] 源");
        }

        /// <summary>原版 winget_table.py:18 英文表头 {名称/Name} 双语标签。</summary>
        private static void CaseParseEnglishHeader()
        {
            IList<RawPackage> result = WingetTableParser.Parse(WingetListEn);
            Equal(2, result.Count, "解析出 2 条记录");
            Equal("Google Chrome", result[0].Name, "[0] Name");
            Equal("Google.Chrome", result[0].PackageId, "[0] Id");
            Equal("126.0.6", result[0].Version, "[0] Version");
            Equal("winget", result[0].Source, "[0] Source");
            Equal("VC++ Redist", result[1].Name, "[1] Name");
            Equal("Microsoft.VCRedist.2015+.x64", result[1].PackageId, "[1] Id");
            Equal("14.40.3", result[1].Version, "[1] Version");
            Equal("14.42.3", result[1].Available, "[1] Available");
            Equal("winget", result[1].Source, "[1] Source");
        }

        /// <summary>原版 winget_table.py:140 表头认不出来时按 2+ 空格兜底、从右认领字段。</summary>
        private static void CaseParseFallbackGaps()
        {
            IList<RawPackage> result = WingetTableParser.Parse(WingetListFallback);
            Equal(2, result.Count, "兜底解析出 2 条记录");
            Equal("谷歌浏览器", result[0].Name, "[0] name");
            Equal("Google.Chrome", result[0].PackageId, "[0] id");
            Equal("126.0.6", result[0].Version, "[0] version");
            Equal("", result[0].Available, "[0] available（兜底不产可用列）");
            Equal("winget", result[0].Source, "[0] source");
            Equal("记事本", result[1].Name, "[1] name");
            Equal("Microsoft.WindowsNotepad", result[1].PackageId, "[1] id");
            Equal("11.2404", result[1].Version, "[1] version");
            Equal("msstore", result[1].Source, "[1] source");
        }

        /// <summary>原版 winget_table.py:100 空文本返回空列表不抛。</summary>
        private static void CaseParseEmpty()
        {
            IList<RawPackage> result = WingetTableParser.Parse("");
            True(result != null, "空输入返回非 null");
            Equal(0, result.Count, "空输入 → 0 条");
            True(WingetTableParser.Parse(null) != null, "null 输入不抛");
        }

        /// <summary>原版 winget_cli.py 输出解码顺序 utf-8-sig → utf-8 → 936；BOM 必须剥掉。</summary>
        private static void CaseDecodeUtf8Bom()
        {
            byte[] raw = Encoding.UTF8.GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes("名称 ID 版本");
            byte[] all = new byte[raw.Length + body.Length];
            Array.Copy(raw, 0, all, 0, raw.Length);
            Array.Copy(body, 0, all, raw.Length, body.Length);

            string decoded = EncodedText.Decode(all);
            Equal("名称 ID 版本", decoded, "BOM 被剥掉且内容不变");
            True(!decoded.StartsWith("\uFEFF", StringComparison.Ordinal), "首字符不是 \\uFEFF");
            Equal("名称 ID 版本", EncodedText.Decode(body), "无 BOM 的 UTF-8 正常解码");
        }

        /// <summary>原版 winget_cli.py：非 UTF-8 字节按本机代码页 936 解码。</summary>
        private static void CaseDecodeGbk()
        {
            byte[] raw = Encoding.GetEncoding(936).GetBytes("名称版本 winget");
            string decoded = EncodedText.Decode(raw);
            Equal("名称版本 winget", decoded, "GBK 字节解码为原文");
        }

        /// <summary>emoji 名称：显示宽度 2 列且不把代理对切半（winget_table.py:86-97 SplitRow）。</summary>
        private static void CaseEmojiWidthAndParse()
        {
            Equal(9, EncodedText.DisplayWidth("测试🦊App"), "\"测试🦊App\" 宽度 = 4+2+2 = 9? 应为 4+2+3=9");
            IList<RawPackage> result = WingetTableParser.Parse(WingetListEmoji);
            Equal(1, result.Count, "emoji 行解析出 1 条");
            Equal("测试🦊App", result[0].Name, "emoji 名称完整不切半");
            Equal("Test.Emoji", result[0].PackageId, "ID 正确");
            Equal("1.0", result[0].Version, "版本正确");
            Equal("winget", result[0].Source, "源正确");
        }

        /// <summary>原版 catalog.py:74：源列 ∈ {winget, msstore} 才可重装。</summary>
        private static void CaseIsReinstallable()
        {
            True(PackageCatalog.IsReinstallable(new RawPackage("A", "a.b", "1.0", "", "winget")), "source=winget → true");
            True(PackageCatalog.IsReinstallable(new RawPackage("A", "a.b", "1.0", "", "msstore")), "source=msstore → true");
            True(PackageCatalog.IsReinstallable(new RawPackage("A", "a.b", "1.0", "", " winget ")), "source 带空白仍算（Trim）");
            True(!PackageCatalog.IsReinstallable(new RawPackage("A", "a.b", "1.0", "", "")), "source 空 → false");
            True(!PackageCatalog.IsReinstallable(new RawPackage("A", "MSIX\\Foo", "1.0", "", "")), "MSIX → false");
            True(!PackageCatalog.IsReinstallable(new RawPackage("A", "ARP\\Foo", "1.0", "", "")), "ARP（注册表）→ false");
            True(!PackageCatalog.IsReinstallable(null), "null → false");
        }

        /// <summary>
        /// 原版 catalog.py:36 关键词表 + catalog.py:82 名称或 ID 命中即算。
        /// 本轮改造 3：尾部追加 4 个窄词（vc_redist / net runtime / windowsdesktop.app / aspnetcore），
        /// 并新增 <see cref="PackageCatalog.MatchedDependencyKeyword"/> 暴露「命中的是哪个词」。
        /// </summary>
        private static void CaseIsDependencyKeyword()
        {
            True(PackageCatalog.IsDependencyKeyword(
                new RawPackage("x", "Microsoft.VCRedist.2015+.x64", "1", "", "winget")), "VCRedist ID → true");
            True(PackageCatalog.IsDependencyKeyword(
                new RawPackage("x", "Microsoft.DotNet.Runtime.8", "1", "", "winget")), "DotNet.Runtime ID → true");
            True(PackageCatalog.IsDependencyKeyword(
                new RawPackage("Visual C++ Redistributable", "Other.Id", "1", "", "winget")), "名称含 redistributable → true");
            True(!PackageCatalog.IsDependencyKeyword(
                new RawPackage("Google Chrome", "Google.Chrome", "1", "", "winget")), "普通软件 → false");
            True(!PackageCatalog.IsDependencyKeyword(null), "null → false");

            // 改造后 IsDependencyKeyword 复用 MatchedDependencyKeyword：新增词同样生效
            True(PackageCatalog.IsDependencyKeyword(
                new RawPackage("VC_redist.x64", "Microsoft.VC_redist.14", "1", "", "winget")),
                "vc_redist 命中 → IsDependencyKeyword true");

            // ── 关键词表：19 个，前 15 个顺序与拼写必须与原版逐字一致 ──
            Equal(19, PackageCatalog.DependencyKeywords.Length, "关键词表共 19 个（原版 15 + 新增 4）");
            string[] original15 =
            {
                "vcredist", "visualc++", "redistributable", "dotnet", "windowsappruntime", "vclibs",
                "ui.xaml", "appinstaller", "directx", "webview2", ".net framework", "net framework",
                "net.native", "desktopappruntime", "xna"
            };
            for (int i = 0; i < original15.Length; i++)
            {
                Equal(original15[i], PackageCatalog.DependencyKeywords[i],
                      "原版第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 个关键词未变");
            }

            True(HasDependencyKeyword("vc_redist"), "新增关键词 vc_redist 存在");
            True(HasDependencyKeyword("net runtime"), "新增关键词 net runtime 存在");
            True(HasDependencyKeyword("windowsdesktop.app"), "新增关键词 windowsdesktop.app 存在");
            True(HasDependencyKeyword("aspnetcore"), "新增关键词 aspnetcore 存在");

            // ── 19 个词各自的命中用例（期望值均按 MatchedDependencyKeyword 的「首个命中」规则推演） ──
            string[][] hitCases =
            {
                new[] { "x", "Microsoft.VCRedist.2015+.x64", "vcredist" },
                new[] { "VisualC++ Runtime", "x.vendor", "visualc++" },
                new[] { "Redistributable Package", "x.vendor", "redistributable" },
                new[] { "x", "Microsoft.DotNet.Runtime.8", "dotnet" },
                new[] { "x", "Microsoft.WindowsAppRuntime.1.6", "windowsappruntime" },
                new[] { "x", "Microsoft.VCLibs.140.00", "vclibs" },
                new[] { "x", "Microsoft.UI.Xaml.2.8", "ui.xaml" },
                new[] { "x", "Microsoft.DesktopAppInstaller", "appinstaller" },
                new[] { "x", "Microsoft.DirectX", "directx" },
                new[] { "x", "Microsoft.EdgeWebView2Runtime", "webview2" },
                new[] { ".NET Framework 4.8", "x.vendor", ".net framework" },
                new[] { "NET Framework 4.8", "x.vendor", "net framework" },
                new[] { "x", "Microsoft.NET.Native.Framework.2.2", "net.native" },
                new[] { "x", "Microsoft.DesktopAppRuntime.1.0", "desktopappruntime" },
                new[] { "x", "Microsoft.XNAClassic", "xna" },
                new[] { "VC_redist.x64", "Microsoft.VC_redist.14", "vc_redist" },
                new[] { ".NET Runtime 8.0.1", "Some.Vendor.Legacy", "net runtime" },
                new[] { "Microsoft Windows Desktop App", "Microsoft.WindowsDesktop.App.Runtime", "windowsdesktop.app" },
                new[] { "ASP.NET Core Runtime 8.0", "Microsoft.AspNetCore.App", "aspnetcore" }
            };
            for (int i = 0; i < hitCases.Length; i++)
            {
                string[] item = hitCases[i];
                Equal(item[2], PackageCatalog.MatchedDependencyKeyword(item[0], item[1]),
                      "命中词 [" + item[2] + "] ← " + item[1]);
            }

            // ── 关键词顺序副作用（追加在尾部，不得抢走原来的命中词） ──
            Equal("vcredist", PackageCatalog.MatchedDependencyKeyword("VCRedist", "VCRedist"),
                  "VCRedist → vcredist（新增 vc_redist 不抢）");
            Equal("dotnet", PackageCatalog.MatchedDependencyKeyword(".NET Runtime 8.0.1", "Microsoft.DotNet.Runtime.8"),
                  "ID 里有 dotnet 时仍归 dotnet（net runtime 排在后面，不抢）");
            Equal("windowsdesktop.app",
                  PackageCatalog.MatchedDependencyKeyword("Microsoft Windows Desktop App", "Microsoft.WindowsDesktop.App.Runtime"),
                  "WindowsDesktop.App 归 windowsdesktop.app（不是 desktopappruntime：ID 里 desktopapp 后没有紧跟 runtime）");

            // ── 不命中/不误判 ──
            True(PackageCatalog.MatchedDependencyKeyword("Google.Chrome", "Google.Chrome") == null,
                 "Google.Chrome → null");
            True(PackageCatalog.MatchedDependencyKeyword(null, null) == null, "(null,null) → null 且不抛");
            True(PackageCatalog.MatchedDependencyKeyword("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode") == null,
                 "VisualStudioCode 不被 visualc++ 误判");
            True(PackageCatalog.MatchedDependencyKeyword("Java Runtime", "Oracle.JavaRuntime") == null,
                 "单独的 runtime 不是关键词（Java 不被误判成运行库）");
        }

        private static bool HasDependencyKeyword(string keyword)
        {
            return Array.IndexOf(PackageCatalog.DependencyKeywords, keyword) >= 0;
        }

        /// <summary>原版 catalog.py:88 _id_kind：msix\ / arp\ / 含反斜杠 / 普通。</summary>
        private static void CaseIdKindOf()
        {
            Equal(IdKind.Msix, PackageCatalog.IdKindOf("MSIX\\App"), "msix\\ 前缀（大小写不敏感）→ Msix");
            Equal(IdKind.Registry, PackageCatalog.IdKindOf("ARP\\Demo"), "arp\\ 前缀 → Registry");
            Equal(IdKind.Registry, PackageCatalog.IdKindOf("HKCU\\Software\\Foo"), "其他反斜杠路径 → Registry");
            Equal(IdKind.Winget, PackageCatalog.IdKindOf("Google.Chrome"), "普通 ID → Winget");
            Equal(IdKind.Winget, PackageCatalog.IdKindOf(""), "空串 → Winget");
            Equal(IdKind.Winget, PackageCatalog.IdKindOf(null), "null → Winget");
        }

        /// <summary>原版 qt_app.py:1586-1590 分组常量与顺序（软件/运行库/winget 不支持）。</summary>
        private static void CaseGroups()
        {
            Equal("软件", Groups.Software, "分组名「软件」");
            Equal("运行库", Groups.Dependency, "分组名「运行库」");
            Equal("winget 不支持", Groups.Unsupported, "分组名「winget 不支持」");
            Equal(3, Groups.Order.Length, "固定三组");
            Equal(0, Groups.Rank(Groups.Software), "软件权重 0");
            Equal(1, Groups.Rank(Groups.Dependency), "运行库权重 1");
            Equal(2, Groups.Rank(Groups.Unsupported), "winget 不支持权重 2");

            var sw = new Package { Reinstallable = true, IsDependency = false };
            var dep = new Package { Reinstallable = true, IsDependency = true };
            var un = new Package { Reinstallable = false };
            Equal(Groups.Software, Groups.GroupOf(sw), "可重装∧非运行库 → 软件");
            Equal(Groups.Dependency, Groups.GroupOf(dep), "可重装∧运行库 → 运行库");
            Equal(Groups.Unsupported, Groups.GroupOf(un), "不可重装 → winget 不支持");
            True(Groups.IsKnown("软件") && Groups.IsKnown("winget 不支持") && !Groups.IsKnown("垃圾"), "IsKnown 校验");
        }

        /// <summary>原版 profiles.py:20 normalize_winget_version：v 前缀剥掉、不匹配回落 1.0.0。</summary>
        private static void CaseNormalizeVersion()
        {
            Equal("1.30.100-preview", ExportProfiles.NormalizeWingetVersion("v1.30.100-preview"), "v 前缀剥掉保留 preview");
            Equal("1.2.3", ExportProfiles.NormalizeWingetVersion("1.2.3"), "纯版本原样");
            Equal("1.0.0", ExportProfiles.NormalizeWingetVersion("unknown"), "unknown → 1.0.0");
            Equal("1.0.0", ExportProfiles.NormalizeWingetVersion(""), "空串 → 1.0.0");
            Equal("1.0.0", ExportProfiles.NormalizeWingetVersion(null), "null → 1.0.0");
        }

        /// <summary>
        /// 原版 profiles.py:26 build_winget_manifest：$schema、Sources 按源名排序
        /// （msstore &lt; winget，msstore 在前）、Packages 只有 PackageIdentifier。
        /// </summary>
        private static void CaseBuildManifest()
        {
            var wingetPkg = new Package { PackageIdentifier = "Google.Chrome", Source = "winget", Reinstallable = true };
            var msStorePkg = new Package { PackageIdentifier = "9NBLGGH4NNS1", Source = "msstore", Reinstallable = true };
            var wingetPkg2 = new Package { PackageIdentifier = "Microsoft.VisualStudioCode", Source = "winget", Reinstallable = true };
            var unsupported = new Package { PackageIdentifier = "ARP\\Legacy", Source = "", Reinstallable = false };

            string manifest = ExportProfiles.BuildManifest(
                new List<Package> { wingetPkg, msStorePkg, wingetPkg2, unsupported }, "v1.30.100-preview");

            True(!manifest.StartsWith("\uFEFF", StringComparison.Ordinal), "manifest 无 BOM 字符");
            True(manifest.EndsWith("\n", StringComparison.Ordinal), "manifest 以换行结尾");
            True(manifest.Contains("\"PackageIdentifier\""), "含 PackageIdentifier 键");
            True(!manifest.Contains("\"Version\""), "不写 Version（下架不炸）");

            JsonValue root = JsonReader.Parse(manifest);
            Equal(ExportProfiles.SchemaUrl, root.Get("$schema").AsString(), "$schema 为官方 2.0 地址");
            Equal(2, root.Get("Sources").Count, "Sources 只有 winget/msstore 两个");
            JsonValue firstSource = null;
            JsonValue secondSource = null;
            int i = 0;
            foreach (JsonValue entry in root.Get("Sources").Items)
            {
                if (i == 0)
                {
                    firstSource = entry;
                }
                else if (i == 1)
                {
                    secondSource = entry;
                }

                i++;
            }

            True(firstSource != null && secondSource != null, "Sources 两项都在");
            Equal("msstore", firstSource.Get("SourceDetails").Get("Name").AsString(), "msstore 排在前面（字典序）");
            Equal("StoreEdgeFD", firstSource.Get("SourceDetails").Get("Identifier").AsString(), "msstore SourceDetails");
            Equal("winget", secondSource.Get("SourceDetails").Get("Name").AsString(), "winget 排在后面");
            Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", secondSource.Get("SourceDetails").Get("Identifier").AsString(), "winget SourceDetails");
            Equal("1.30.100-preview", root.Get("WinGetVersion").AsString(), "WinGetVersion 数字开头");
            Equal(1, CountMembers(firstSource.Get("Packages").Items.GetEnumerator()), "msstore 包条目 1 个");
            foreach (JsonValue item in firstSource.Get("Packages").Items)
            {
                Equal(1, CountObjectMembers(item), "Packages 条目只有 PackageIdentifier 一个键");
                True(item.Get("PackageIdentifier").AsString().Length > 0, "PackageIdentifier 非空");
            }
        }

        private static int CountMembers(IEnumerator<JsonValue> enumerator)
        {
            int count = 0;
            while (enumerator.MoveNext())
            {
                count++;
            }

            return count;
        }

        private static int CountObjectMembers(JsonValue value)
        {
            int count = 0;
            foreach (KeyValuePair<string, JsonValue> member in value.Members)
            {
                count++;
            }

            return count;
        }

        /// <summary>原版 profiles.py:26 空选择列表 → Sources 空数组，不抛异常。</summary>
        private static void CaseBuildManifestEmpty()
        {
            string manifest = ExportProfiles.BuildManifest(new List<Package>(), "unknown");
            JsonValue root = JsonReader.Parse(manifest);
            Equal(0, root.Get("Sources").Count, "Sources 为空数组");
            Equal("1.0.0", root.Get("WinGetVersion").AsString(), "WinGetVersion 回落 1.0.0");
            Equal(ExportProfiles.SchemaUrl, root.Get("$schema").AsString(), "$schema 仍在");

            string manifestNull = ExportProfiles.BuildManifest(null, "unknown");
            JsonValue rootNull = JsonReader.Parse(manifestNull);
            Equal(0, rootNull.Get("Sources").Count, "null 选择集合同样不抛");
        }

        /// <summary>原版 profiles.py:95：chcp 936 / --ignore-unavailable / %~dp0 / CRLF / 结尾 CRLF。</summary>
        private static void CaseRestoreBatBody()
        {
            byte[] bytes = ExportProfiles.BuildRestoreBat("apps.json", 3, 1);
            Encoding gbk = Encoding.GetEncoding(936);
            string text = gbk.GetString(bytes);

            True(text.StartsWith("@echo off\r\n", StringComparison.Ordinal), "以 @echo off 开头");
            True(text.Contains("chcp 936"), "含 chcp 936（cmd 代码页）");
            True(text.Contains("title 恢复软件清单"), "标题与原版一致");
            True(text.Contains("cd /d \"%~dp0\""), "cd /d %~dp0");
            True(text.Contains("--ignore-unavailable"), "含 --ignore-unavailable");
            True(text.Contains("winget import -i \"%~dp0apps.json\""), "winget import 指向清单");
            True(text.Contains("其中 1 个来自微软商店 msstore"), "msstore 数量提示（msstore>0）");
            True(text.EndsWith("\r\n", StringComparison.Ordinal), "结尾是 CRLF");
            True(text.Contains("[x] 未找到 winget，请先安装「应用安装程序 / App Installer」。"),
                 "winget 缺失提示文案与原版一致");
            // 不允许孤立 \n（没有 \r 的换行）
            True(!HasLoneLf(text), "全篇没有孤立 \\n");
            // msstore=0 时提示行换成 echo.
            byte[] bytes0 = ExportProfiles.BuildRestoreBat("apps.json", 3, 0);
            string text0 = Encoding.GetEncoding(936).GetString(bytes0);
            True(text0.Contains("echo."), "msstore=0 时用 echo. 占位");
            True(!text0.Contains("个来自微软商店"), "msstore=0 时无商店提示");
        }

        private static int CountOccurrences(string text, string sub)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(sub, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += sub.Length;
            }

            return count;
        }

        private static bool HasLoneLf(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>原版 profiles.py:83：basename 防目录穿越、% 双写防变量展开。</summary>
        private static void CaseRestoreBatEscape()
        {
            string text = Encoding.GetEncoding(936).GetString(ExportProfiles.BuildRestoreBat("a%b.json", 1, 0));
            True(text.Contains("a%%b.json"), "% 双写为 %%");
            True(!text.Contains("a%b.json\""), "未转义的 % 不出现在 import 行");

            string safe = Encoding.GetEncoding(936).GetString(ExportProfiles.BuildRestoreBat("..\\..\\evil.json", 1, 0));
            True(safe.Contains("evil.json"), "只保留文件名 evil.json");
            True(!safe.Contains(".."), "不含 .. 目录穿越片段");

            string empty = Encoding.GetEncoding(936).GetString(ExportProfiles.BuildRestoreBat("", 1, 0));
            True(empty.Contains("apps.json"), "空文件名回落 apps.json");
        }

        /// <summary>
        /// 「无法自动重装清单」：**单文件 HTML**（有意替换掉原版 txt）。锁住结构、分节、
        /// 勾选 key、筛选/搜索、统计卡、搜官网链接、空集合、以及「不再输出内部源名」。
        /// </summary>
        private static void CaseUninstallableList()
        {
            var alpha = new Package { Name = "alpha", PackageIdentifier = "Alpha.Id", Version = "1.0", Source = "", Reinstallable = false };
            var beta = new Package
            {
                Name = "Beta", PackageIdentifier = "Beta.Id", Version = "", Source = "ARP",
                IdKind = WingetManagerWpf.Models.IdKind.Registry, Reinstallable = false
            };

            string html = ExportProfiles.BuildUninstallableHtml(new List<Package> { beta, alpha });
            True(html.StartsWith("<!DOCTYPE html>\r\n", StringComparison.Ordinal), "以 DOCTYPE 开头");
            True(html.Contains("<meta charset=\"utf-8\">"), "声明 UTF-8（写盘无 BOM 也能正确显示中文）");
            True(html.EndsWith("</html>\r\n", StringComparison.Ordinal), "以 </html> + CRLF 收尾");
            True(!HasLoneLf(html), "没有孤立 \\n");
            True(html.Contains("无法自动重装清单 · 2 项"), "标题带总项数");
            True(html.Contains("待装") && html.Contains("已装好") && html.Contains("完成度"), "三个统计卡都在");
            True(html.Contains("data-filter=\"all\"") && html.Contains("data-filter=\"todo\"")
                 && html.Contains("data-filter=\"done\""), "全部/未装/已装筛选");
            True(html.Contains("id=\"q\"") && html.Contains("搜软件名"), "带搜索框");
            True(html.Contains("普通软件 <em>2</em>"), "普通软件分节带计数");
            True(!html.Contains("<section>\r\n<h2>系统组件"), "没有运行库时不输出空节");
            True(html.Contains("data-key=\"alpha.id\""), "复选框带稳定 key（localStorage 用）");
            True(html.Contains(">alpha</span>") && html.Contains(">v1.0</span>"), "名称与版本分开渲染");
            True(html.Contains(">搜官网</a>"), "每项有搜官网入口");
            True(html.Contains("bing.com/search?q="), "搜索链接指向必应（国内可直连）");

            int alphaIndex = html.IndexOf("data-key=\"alpha.id\"", StringComparison.Ordinal);
            int betaIndex = html.IndexOf("data-key=\"beta.id\"", StringComparison.Ordinal);
            True(alphaIndex > 0 && betaIndex > 0 && alphaIndex < betaIndex, "按名称小写排序（alpha 在 Beta 前）");

            string empty = ExportProfiles.BuildUninstallableHtml(new List<Package>());
            True(empty.Contains("（没有这类软件，全部都能自动重装）"), "空集合文案");
            True(!empty.Contains("<ul>") && !empty.Contains("<section>"), "空集合不输出列表与分节");
        }

        /// <summary>
        /// HTML 清单的类型标签与说明：MSIX / 注册表 / 源不可用三类各给不同标签；
        /// 注册表登记是默认情况不再逐项写说明；用户数据必须转义；内部路径不出现在可见文本里。
        /// </summary>
        private static void CaseUninstallableListSections()
        {
            var app = new Package
            {
                Name = "A & B <工具>", PackageIdentifier = "Some.App", Version = "2.0", Source = "",
                IdKind = WingetManagerWpf.Models.IdKind.Winget, Reinstallable = false, IsDependency = false
            };
            var store = new Package
            {
                Name = "某商店应用", PackageIdentifier = "msix\\SomeStore_1.0_x64", Version = "1.0", Source = "msix",
                IdKind = WingetManagerWpf.Models.IdKind.Msix, Reinstallable = false, IsDependency = false
            };
            var vc = new Package
            {
                Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
                PackageIdentifier = "ARP\\Machine\\X64\\{GUID}", Version = "14.38.33130", Source = "",
                IdKind = WingetManagerWpf.Models.IdKind.Registry, Reinstallable = false, IsDependency = true
            };

            string html = ExportProfiles.BuildUninstallableHtml(new List<Package> { vc, store, app });

            True(html.Contains("普通软件 <em>2</em>"), "普通软件 2 项（app + store）");
            True(html.Contains("系统组件 / 运行库 <em>1</em>"), "运行库单独成节 1 项");
            True(html.IndexOf("普通软件", StringComparison.Ordinal) < html.IndexOf("系统组件", StringComparison.Ordinal),
                 "软件节在运行库节之前");
            True(html.Contains("多数不用单独装"), "运行库节带「不用单独装」提示");

            True(html.Contains(">商店应用</span>"), "MSIX 标签：商店应用");
            True(html.Contains("Microsoft Store → 库"), "MSIX 说明指到商店「库」");
            True(html.Contains(">无独立安装包</span>"), "注册表标签：无独立安装包");
            True(html.Contains(">源不可用</span>"), "winget 形态标签：源不可用");
            True(html.Contains("可按 ID 搜官方下载页：Some.App"), "winget 形态给出可搜的 ID");

            True(html.Contains("A &amp; B &lt;工具&gt;"), "名称里的 & < > 已转义");
            True(!html.Contains("A & B <工具>"), "不出现未转义的原始名");
            True(!html.Contains(">ARP\\Machine"), "注册表内部路径不作为可见文本");
            True(!html.Contains(">msix\\SomeStore"), "MSIX 内部 ID 不作为可见文本");
        }

        /// <summary>原版 fsio.py:17 atomic_write_text：覆盖写、失败清理、无临时文件残留。</summary>
        private static void CaseAtomicText()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "data.txt");
            File.WriteAllText(path, "OLD-CONTENT");

            AtomicFile.WriteTextAtomic(path, "NEW-CONTENT\r\n第二行", Encoding.UTF8);
            Equal("NEW-CONTENT\r\n第二行", File.ReadAllText(path), "覆盖后内容完整");
            True(Directory.GetFiles(dir, "*.tmp").Length == 0, "无 .tmp 残留");

            AtomicFile.WriteTextAtomic(path, "", null);
            Equal(string.Empty, File.ReadAllText(path), "空文本可写");
            True(Directory.GetFiles(dir, "*.tmp").Length == 0, "空文本写盘同样无残留");
        }

        /// <summary>原版 fsio.py:40：UTF-8 无 BOM、2 空格缩进、尾随换行、中文不转义。</summary>
        private static void CaseAtomicJson()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "out.json");
            var payload = JsonValue.NewObject();
            payload.Set("名称", JsonValue.From("值"));
            payload.Set("n", JsonValue.From(3));

            AtomicFile.WriteJsonAtomic(path, payload);
            byte[] bytes = File.ReadAllBytes(path);
            True(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "无 BOM");
            string text = Encoding.UTF8.GetString(bytes);
            True(text.Contains("\"名称\": \"值\""), "中文原样输出（ensure_ascii=False）且 2 空格缩进");
            True(text.Contains("\"n\": 3"), "整数不带小数尾巴");
            True(text.EndsWith("\n", StringComparison.Ordinal), "尾随换行");
            JsonValue round = JsonReader.Parse(text);
            Equal("值", round.Get("名称").AsString(), "写出的 JSON 可被自己解析");
        }

        /// <summary>原版 json.dumps(..., ensure_ascii=False) 的等价序列化与解析回环。</summary>
        private static void CaseJsonRoundtrip()
        {
            var root = JsonValue.NewObject();
            root.Set("中文", JsonValue.From("引号\"反斜杠\\换行\n制表\t"));
            var arr = JsonValue.NewArray();
            arr.Add(JsonValue.From(true));
            arr.Add(JsonValue.From(false));
            arr.Add(JsonValue.Null);
            arr.Add(JsonValue.From(42));
            arr.Add(JsonValue.From(-2.5));
            root.Set("list", arr);
            var nested = JsonValue.NewObject();
            nested.Set("deep", JsonValue.NewArray(new[] { JsonValue.From("x") }));
            root.Set("nested", nested);

            string text = JsonWriter.Serialize(root);
            JsonValue parsed = JsonReader.Parse(text);
            string text2 = JsonWriter.Serialize(parsed);
            Equal(text, text2, "序列化 → 解析 → 序列化 稳定不变");

            Equal("引号\"反斜杠\\换行\n制表\t", parsed.Get("中文").AsString(), "特殊字符回环不丢");
            int boolCount = 0;
            foreach (JsonValue item in parsed.Get("list").Items)
            {
                if (item.IsBool && item.TryGetBool(out bool flag) && flag)
                {
                    boolCount++;
                }
            }

            Equal(1, boolCount, "数组回环后恰好 1 个 true");
            True(text.Contains("\\\""), "引号被转义");
            True(text.Contains("\\\\"), "反斜杠被转义为 \\\\");
            True(text.Contains("\\n"), "换行被转义");
            True(text.Contains("中文"), "中文不转义");
        }

        /// <summary>原版读老文件时 BOM 会留在文本里：解析器必须容忍（metadata.py:150）。</summary>
        private static void CaseJsonBom()
        {
            JsonValue value = JsonReader.Parse("\uFEFF{\"a\": 1}");
            Equal(1, value.Get("a").AsLong(), "带 BOM 的 JSON 正常解析");
        }

        /// <summary>非法 JSON 抛 BadRequestException（原版 core/errors.py BadRequest，调用方回退默认）。</summary>
        private static void CaseJsonInvalid()
        {
            Throws<BadRequestException>(delegate { JsonReader.Parse("{\"a\": }"); }, "缺值");
            Throws<BadRequestException>(delegate { JsonReader.Parse("{\"a\": 1,}"); }, "尾逗号不容忍");
            Throws<BadRequestException>(delegate { JsonReader.Parse("not json"); }, "纯垃圾");
            Throws<BadRequestException>(delegate { JsonReader.Parse("{\"a\" 1}"); }, "缺冒号");
            JsonValue ignored;
            True(!JsonReader.TryParse("{bad", out ignored), "TryParse 失败返回 false");
        }

        /// <summary>原版 settings 读写：回环保存/读取；损坏文件静默回退默认值。</summary>
        private static void CaseSettingsStore()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path, null);

            // 文件缺失 → 默认
            AppSettings loaded = store.Load();
            Equal((int)Metrics.WindowWidth, loaded.WindowWidth, "缺文件 → 默认窗宽");
            True(!loaded.HasWindowPosition, "缺文件 → 无窗口坐标");

            var settings = new AppSettings
            {
                WindowX = 100,
                WindowY = 50,
                WindowWidth = 1300,
                WindowHeight = 900,
                Maximized = true,
                LogHeight = 200,
                LogExpanded = false,
                Category = "运行库",
                ExportPath = "D:\\apps.json"
            };
            store.Save(settings);

            AppSettings again = new SettingsStore(path, null).Load();
            Equal(100, again.WindowX, "回环 WindowX");
            Equal(50, again.WindowY, "回环 WindowY");
            Equal(1300, again.WindowWidth, "回环 WindowWidth");
            Equal(900, again.WindowHeight, "回环 WindowHeight");
            True(again.Maximized, "回环 Maximized");
            Equal(200, again.LogHeight, "回环 LogHeight");
            True(!again.LogExpanded, "回环 LogExpanded");
            Equal("运行库", again.Category, "回环 Category");
            Equal("D:\\apps.json", again.ExportPath, "回环 ExportPath");

            File.WriteAllText(path, "{ 这是半截 JSON", Encoding.UTF8);
            AppSettings corrupted = new SettingsStore(path, null).Load();
            Equal((int)Metrics.WindowWidth, corrupted.WindowWidth, "损坏文件 → 默认窗宽");
            Equal((int)Metrics.LogDefault, corrupted.LogHeight, "损坏文件 → 默认日志高度");
            Equal(string.Empty, corrupted.Category, "损坏文件 → 空分组");
        }

        /// <summary>原版 qt_app 勾选读取 isinstance(x,bool) / 老文件兼容串 {1,true,yes,on}。</summary>
        private static void CaseSelectionTruthy()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "selection.json");
            File.WriteAllText(path,
                "{\"a\": true, \"b\": \"false\", \"c\": 0, \"d\": \"yes\", \"e\": \"1\", \"f\": \"0\", \"g\": 1}",
                Encoding.UTF8);

            IDictionary<string, bool> map = new SelectionStore(path, null).Load();
            True(map.ContainsKey("a") && map["a"], "a: true → 勾选");
            True(map.ContainsKey("b") && !map["b"], "b: \"false\"（字符串）→ 不勾选（不能误判为真）");
            True(map.ContainsKey("c") && !map["c"], "c: 0（数字）→ 不勾选");
            True(map.ContainsKey("d") && map["d"], "d: \"yes\" → 勾选");
            True(map.ContainsKey("e") && map["e"], "e: \"1\" → 勾选");
            True(map.ContainsKey("f") && !map["f"], "f: \"0\" → 不勾选");
            True(map.ContainsKey("g") && !map["g"], "g: 1（数字）→ 不勾选（严格布尔）");
        }

        /// <summary>保存后文件必须是 {"id": true} 严格布尔（qt_app.py _save_selection 等价）。</summary>
        private static void CaseSelectionSave()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "selection.json");
            var selection = new Dictionary<string, bool>(StringComparer.Ordinal);
            selection["A.ID"] = true;

            new SelectionStore(path, null).Save(selection);
            string text = File.ReadAllText(path);
            Equal("{\n  \"a.id\": true\n}\n", text, "保存结果为小写键 + 严格 JSON bool + 尾换行");
            True(!(Encoding.UTF8.GetBytes(text)[0] == 0xEF), "无 BOM");
            True(!text.Contains("\"A.ID\"") && !text.Contains("false"), "只写 true 项且键已小写");

            IDictionary<string, bool> reload = new SelectionStore(path, null).Load();
            True(reload.ContainsKey("a.id") && reload["a.id"], "回环读回勾选");
        }

        /// <summary>原版 metadata.py:131 RuntimeCache：严格 bool、schema 校验、损坏回退空。</summary>
        private static void CaseRuntimeCache()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "metadata-cache.json");

            var cache = new RuntimeCache(path, null);
            True(cache.Get("Foo.Bar") == null, "空缓存未命中");
            cache.Put("Foo.Bar", true);
            True(cache.Get("foo.bar") == true, "写入 true 读回 true（键大小写归一）");
            cache.Put("Other", false);
            True(cache.Get("Other") == false, "写入 false 读回 false");
            True(cache.Get("OTHER") == false, "键大小写不敏感");
            cache.Put("Foo.Bar", false);
            True(cache.Get("foo.bar") == false, "覆写生效");
            True(File.Exists(path), "Put 立即落盘");

            // 字符串 "true" 不算命中
            string stringFile = Path.Combine(dir, "string-dep.json");
            File.WriteAllText(stringFile, "{\"schema\":1,\"entries\":{\"dep\":{\"dep\":\"true\",\"checkedAt\":\"x\"}}}", Encoding.UTF8);
            True(new RuntimeCache(stringFile, null).Get("dep") == null, "dep 为字符串 \"true\" → 未命中");

            // schema 不符 → 整体失效
            string schemaFile = Path.Combine(dir, "schema2.json");
            File.WriteAllText(schemaFile, "{\"schema\":2,\"entries\":{\"x\":{\"dep\":true}}}", Encoding.UTF8);
            True(new RuntimeCache(schemaFile, null).Count == 0, "schema 不匹配 → 回退空缓存");

            // 损坏文件 → 回退空缓存
            string badFile = Path.Combine(dir, "bad.json");
            File.WriteAllText(badFile, "{oops", Encoding.UTF8);
            True(new RuntimeCache(badFile, null).Count == 0, "损坏文件 → 回退空缓存不抛");
        }

        /// <summary>
        /// 元数据判定缓存的 TTL（本轮改造 1）：过期只表示「本次不采信」，不删条目、不落盘。
        /// 不依赖真实等待：直接手写 metadata-cache.json 里的 checkedAt 造出「100 天前」。
        /// </summary>
        private static void CaseRuntimeCacheTtl()
        {
            string dir = NewTempDir();
            DateTimeOffset now = DateTimeOffset.Now;
            string oldStamp = FormatStamp(now.AddDays(-100));
            string path = Path.Combine(dir, "ttl.json");
            File.WriteAllText(path, BuildCacheJson("old.dep", true, oldStamp), Encoding.UTF8);

            // ① 默认 90 天：100 天前的条目视为过期（可选参数不传 = 90）
            var byDefault = new RuntimeCache(path, null);
            True(byDefault.Get("old.dep") == null, "① checkedAt=-100 天 + 默认 ttl 90 → Get == null（过期）");
            Equal(1, byDefault.Count, "① 条目仍在缓存里（Count == 1，过期不删）");
            True(byDefault.Get("OLD.DEP") == null, "① 键大小写不敏感，同样判过期");

            // ② ttl = 0 → 永不过期
            var never = new RuntimeCache(path, null, 0);
            True(never.Get("old.dep") == true, "② ttl=0 → 返回原值 true（永不过期）");

            // ③ ttl = 3650 → 100 天远未到期
            var longLived = new RuntimeCache(path, null, 3650);
            True(longLived.Get("old.dep") == true, "③ ttl=3650 → 返回原值 true");

            // ④ 时间戳坏掉（乱码 / 空串 / 缺字段）→ 一律当过期，且不抛
            string broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken,
                "{\"schema\":1,\"entries\":{" +
                "\"bad.text\":{\"dep\":true,\"checkedAt\":\"x\"}," +
                "\"bad.empty\":{\"dep\":true,\"checkedAt\":\"\"}," +
                "\"bad.missing\":{\"dep\":true}}}", Encoding.UTF8);
            var brokenCache = new RuntimeCache(broken, null);
            True(brokenCache.Get("bad.text") == null, "④ checkedAt=\"x\" → null（解析失败当过期）");
            True(brokenCache.Get("bad.empty") == null, "④ checkedAt=\"\" → null");
            True(brokenCache.Get("bad.missing") == null, "④ 缺 checkedAt 字段 → null");
            Equal(3, brokenCache.Count, "④ 三条坏时间戳的条目都还在（只判过期，不删）");

            // ⑤ 未来时间（时钟回拨 / 文件来自未来）→ 判新鲜
            string futurePath = Path.Combine(dir, "future.json");
            File.WriteAllText(futurePath, BuildCacheJson("future.dep", true, FormatStamp(now.AddDays(5))), Encoding.UTF8);
            True(new RuntimeCache(futurePath, null).Get("future.dep") == true,
                 "⑤ checkedAt=+5 天（未来）→ 返回原值（视为新鲜，不反复重查）");

            // ⑥ 过期判定不落盘：文件内容逐字未变，原 checkedAt 仍在
            string before = File.ReadAllText(path, Encoding.UTF8);
            var reread = new RuntimeCache(path, null);
            True(reread.Get("old.dep") == null, "⑥ 重新打开仍是过期");
            string after = File.ReadAllText(path, Encoding.UTF8);
            Equal(before, after, "⑥ Get 判定过期不落盘（缓存文件内容逐字未变）");
            True(after.Contains(oldStamp), "⑥ 文件里原 checkedAt 仍在（未删条目）");

            // ⑦ Put 刷新 checkedAt → 过期立即解除
            reread.Put("old.dep", true);
            True(reread.Get("old.dep") == true, "⑦ Put 后立刻命中（刷新即解除过期）");
            Equal(1, reread.Count, "⑦ 刷新不新增条目（同键原位更新）");
            True(!File.ReadAllText(path, Encoding.UTF8).Contains(oldStamp), "⑦ 落盘后旧 checkedAt 已被替换");

            // ⑧ dep=false 的过期条目同样返回 null（不是 false）——
            //    PendingMetadataIds 用 Get().HasValue 判定，只有 null 才会重新 winget show
            string negative = Path.Combine(dir, "negative.json");
            File.WriteAllText(negative, BuildCacheJson("old.neg", false, oldStamp), Encoding.UTF8);
            True(new RuntimeCache(negative, null).Get("old.neg") == null, "⑧ 过期条目 dep=false → null（不是 false）");

            // ⑨ 同一文件里逐条独立判定过期
            string mixed = Path.Combine(dir, "mixed.json");
            File.WriteAllText(mixed,
                "{\"schema\":1,\"entries\":{" +
                "\"e.fresh\":{\"dep\":true,\"checkedAt\":\"" + FormatStamp(now.AddDays(-89)) + "\"}," +
                "\"e.stale\":{\"dep\":true,\"checkedAt\":\"" + FormatStamp(now.AddDays(-91)) + "\"}}}", Encoding.UTF8);
            var mixedCache = new RuntimeCache(mixed, null);
            True(mixedCache.Get("e.fresh") == true, "⑨ -89 天（< 90）→ 命中");
            True(mixedCache.Get("e.stale") == null, "⑨ -91 天（> 90）→ 未命中");
            Equal(2, mixedCache.Count, "⑨ 两条都还在（逐条独立判定）");
        }

        /// <summary>与原版/产品一致的 checkedAt 格式：yyyy-MM-dd'T'HH:mm:sszzz。</summary>
        private static string FormatStamp(DateTimeOffset value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        /// <summary>手写一条 metadata-cache.json（单条目），避免自测依赖产品代码写盘。</summary>
        private static string BuildCacheJson(string packageId, bool dep, string checkedAt)
        {
            return "{\"schema\":1,\"entries\":{\"" + (packageId ?? string.Empty).ToLowerInvariant()
                   + "\":{\"dep\":" + (dep ? "true" : "false")
                   + ",\"checkedAt\":\"" + checkedAt + "\"}}}";
        }

        /// <summary>原版 metadata.py:91：命中信号词 (True,词) / 有描述无信号 (False,null) / 解析不出 (null,null)。</summary>
        private static void CaseMetadataAnalyze()
        {
            RuntimeSignal hit = MetadataProbe.Analyze("描述: This package contains the runtime components.");
            True(hit.IsDependency == true, "英文描述含 runtime → 运行库");
            Equal("runtime", hit.SignalWord, "命中信号词 runtime");

            RuntimeSignal cn = MetadataProbe.Analyze("描述: 系统运行库组件");
            True(cn.IsDependency == true, "中文描述含 运行库 → 运行库");
            Equal("运行库", cn.SignalWord, "命中信号词 运行库");

            RuntimeSignal tags = MetadataProbe.Analyze("标记:\n  vc-runtime\n");
            True(tags.IsDependency == true, "标记段命中 redist 类信号词");

            RuntimeSignal none = MetadataProbe.Analyze("描述: A fast web browser for everyone");
            True(none.IsDependency == false, "有描述无信号 → false（不是 null）");
            True(none.SignalWord == null, "无命中词");

            RuntimeSignal blank = MetadataProbe.Analyze("");
            True(blank.IsDependency == null && blank.SignalWord == null, "空输出 → (null,null)，绝不当 false");

            RuntimeSignal noLabel = MetadataProbe.Analyze("本工具需要 x64 系统支持");
            True(noLabel.IsDependency == null, "解析不出描述/标记 → (null,null)");
        }

        /// <summary>原版 qt_app.py:742/758：id:/name: 限定、多词 AND、大小写不敏感。</summary>
        private static void CaseSearchQuery()
        {
            var chrome = new Package { Name = "Google Chrome", PackageIdentifier = "Google.Chrome" };
            var chrome32 = new Package { Name = "Google Chrome 32", PackageIdentifier = "Other.Chrome32" };
            var browser = new Package { Name = "Fast Browser", PackageIdentifier = "Google.Firefox" };

            IList<SearchToken> idTokens = SearchQuery.Tokenize("id:chrome");
            Equal("id", idTokens[0].Field, "id: 前缀识别");
            Equal("chrome", idTokens[0].Value, "id: 取值已小写");
            True(SearchQuery.Matches(chrome, idTokens), "id:chrome 命中包 ID");
            True(!SearchQuery.Matches(browser, idTokens), "id:chrome 不因名称命中（browser 名称无 chrome）");
            var nameOnly = new Package { Name = "Chrome 浏览器", PackageIdentifier = "Totally.Different" };
            True(!SearchQuery.Matches(nameOnly, idTokens), "id:chrome 不匹配只有名称含 chrome 的包");

            IList<SearchToken> nameTokens = SearchQuery.Tokenize("name:google chrome");
            Equal(2, nameTokens.Count, "多词拆成 2 个条件");
            Equal("name", nameTokens[0].Field, "name: 前缀识别");
            True(SearchQuery.Matches(chrome, nameTokens), "name:google chrome 名称 AND 命中");
            True(!SearchQuery.Matches(browser, nameTokens), "名称不含 google → 不命中");
            // 原版 qt_app.py:747：name: 只作用于「紧跟它的那一个词」，后面的 chrome 是 any 词。
            // 所以 Google Chrome 32 同时含 google（名称）与 chrome（名称或 ID）→ 命中。
            True(SearchQuery.Matches(chrome32, nameTokens), "name: 前缀不向后扩散，第二词按 any 匹配");

            // 真正验证 AND：两个词都限定在名称上，缺一个就不命中
            IList<SearchToken> strictAnd = SearchQuery.Tokenize("name:google name:32");
            True(SearchQuery.Matches(chrome32, strictAnd), "name:google name:32 双词 AND 命中");
            True(!SearchQuery.Matches(chrome, strictAnd), "AND 语义：缺 name:32 则不命中");

            IList<SearchToken> anyTokens = SearchQuery.Tokenize("CHROME");
            True(SearchQuery.Matches(chrome, anyTokens), "大小写不敏感（输入大写）");
            True(SearchQuery.Matches(chrome32, anyTokens), "any 词匹配名称或 ID");

            IList<SearchToken> mixed = SearchQuery.Tokenize("google id:chrome32");
            True(SearchQuery.Matches(chrome32, mixed), "普通词 + id: 限定组合 AND");
            True(!SearchQuery.Matches(chrome, mixed), "id:chrome32 不命中 Google.Chrome");

            True(SearchQuery.Matches(chrome, SearchQuery.Tokenize("")), "空条件全命中");
            True(SearchQuery.Matches(chrome, null), "null 条件全命中");
            True(!SearchQuery.Matches(null, anyTokens), "null 包在有条件时不命中");
            // 裸 "id:"（前缀后无内容）退回普通词 → 名称+ID 都不含 "id:" → 不命中（与原版一致）
            True(!SearchQuery.Matches(chrome, SearchQuery.Tokenize("id:")), "裸 id: 前缀退回普通词且不命中");

            string[] keywords = SearchQuery.HighlightKeywords(SearchQuery.Tokenize("chrome id:google chrome"));
            Equal(2, keywords.Length, "高亮关键词去重后 2 个");
            Equal("chrome", keywords[0], "高亮词已去 id: 前缀");
            Equal("google", keywords[1], "保序");
        }

        /// <summary>原版渲染顺序（软件→运行库→winget 不支持）、空组不产出、折叠不丢行。</summary>
        private static void CaseListPresenter()
        {
            var presenter = new ListPresenter();
            var software = new Package { PackageIdentifier = "a.app", Name = "App", Reinstallable = true };
            var dependency = new Package { PackageIdentifier = "b.dep", Name = "Runtime", Reinstallable = true, IsDependency = true };
            var unsupported = new Package { PackageIdentifier = "ARP\\c", Name = "Legacy", Reinstallable = false };

            IList<GroupRender> all = presenter.Rebuild(
                new List<Package> { software, dependency, unsupported }, "", null, false, null, null);
            Equal(3, all.Count, "三个非空分组都产出");
            Equal(Groups.Software, all[0].Title, "第一组 = 软件");
            Equal(Groups.Dependency, all[1].Title, "第二组 = 运行库");
            Equal(Groups.Unsupported, all[2].Title, "第三组 = winget 不支持");
            Equal(1, all[0].Rows.Count, "软件组 1 行");
            Equal(1, all[2].Rows.Count, "不支持组 1 行");
            True(all[2].Selectable == false, "winget 不支持组不可勾选");
            True(all[0].Selectable, "软件组可勾选");

            IList<GroupRender> partial = presenter.Rebuild(
                new List<Package> { software, unsupported }, "", null, false, null, null);
            Equal(2, partial.Count, "空组（运行库）不产出分组头");

            var checkedMap = new Dictionary<string, bool>(StringComparer.Ordinal);
            checkedMap["a.app"] = true;
            IList<GroupRender> selected = presenter.Rebuild(
                new List<Package> { software, dependency, unsupported }, "", null, true, checkedMap, null);
            Equal(1, selected.Count, "仅看已选：只剩软件组");
            Equal(1, selected[0].Rows.Count, "只含已勾选的 a.app");
            Equal(1, selected[0].SelectedCount, "组头已选计数 = 1");

            var expanded = new Dictionary<string, bool>(StringComparer.Ordinal);
            expanded[Groups.Dependency] = false;
            IList<GroupRender> collapsed = presenter.Rebuild(
                new List<Package> { software, dependency, unsupported }, "", null, false, null, expanded);
            GroupRender depGroup = null;
            foreach (GroupRender section in collapsed)
            {
                if (section.Title == Groups.Dependency)
                {
                    depGroup = section;
                }
            }

            True(depGroup != null, "折叠组仍返回（分组头要显示）");
            True(depGroup != null && depGroup.Expanded == false, "Expanded=false 正确传递");
            True(depGroup != null && depGroup.Rows.Count == 1, "折叠组的行不丢（重建时用于展开恢复）");

            IList<GroupRender> byCategory = presenter.Rebuild(
                new List<Package> { software, dependency, unsupported }, Groups.Dependency, null, false, null, null);
            Equal(1, byCategory.Count, "按分组筛选只出运行库组");
        }

        /// <summary>原版 qt_app.py:1521 _scoped_packages：有筛选只动可见项，无筛选动全部可重装包。</summary>
        private static void CaseViewModelScopedKeys()
        {
            // 不启动 UI：所有依赖传 null（MainViewModel 对 null shell/store 均有保护）
            var vm = new MainViewModel(null, null, null, null, null, null, null, null);
            var packages = new List<Package>
            {
                new Package { PackageIdentifier = "A.Chrome", Name = "Google Chrome", Reinstallable = true },
                new Package { PackageIdentifier = "B.Firefox", Name = "Firefox", Reinstallable = true },
                new Package { PackageIdentifier = "Microsoft.VCRedist", Name = "VC++ Redist", Reinstallable = true, IsDependency = true },
                new Package { PackageIdentifier = "ARP\\Legacy", Name = "Legacy", Reinstallable = false }
            };
            FieldInfo field = typeof(MainViewModel).GetField("_packages", BindingFlags.NonPublic | BindingFlags.Instance);
            True(field != null, "反射取到 _packages 字段");
            if (field == null)
            {
                return;
            }

            field.SetValue(vm, packages);

            // ① 无筛选全选：只作用于可重装包（含运行库组），不可重装的 ARP 项不动
            vm.SetAll(true);
            True(vm.IsChecked("a.chrome"), "全选勾上 A");
            True(vm.IsChecked("b.firefox"), "全选勾上 B");
            True(vm.IsChecked("microsoft.vcredist"), "全选勾上运行库组");
            True(!vm.IsChecked("arp\\legacy"), "不可重装项不受全选影响");

            // ② 有筛选（搜索 chrome）→ 全不选只作用于可见项
            vm.SearchText = "chrome";
            vm.SetAll(false);
            True(!vm.IsChecked("a.chrome"), "可见项 A 被取消勾选");
            True(vm.IsChecked("b.firefox"), "屏幕外的 B 保持勾选（当前视图语义，qt_app.py:1542）");
            True(vm.IsChecked("microsoft.vcredist"), "屏幕外的运行库组保持勾选");

            // ③ 分组筛选同样限定作用域
            vm.ClearFilters();
            vm.SetAll(true);           // 重新全勾，才能验证「分组全不选不动别的组」
            vm.SelectCategory(Groups.Dependency);
            vm.SetAll(false);
            True(vm.IsChecked("a.chrome"), "运行库分组下全不选不动软件组");
            True(!vm.IsChecked("microsoft.vcredist"), "运行库分组下全不选只清本组");
            vm.SelectCategory(string.Empty);

            // ④ 反选（无筛选 → 作用于全部可重装包）
            // 先显式清空，作为反选的确定起点；否则结果依赖 ③ 结束时的残留状态
            vm.ClearFilters();
            vm.SetAll(false);
            vm.Invert();
            True(vm.IsChecked("a.chrome"), "反选后 A 被勾上");
            True(vm.IsChecked("b.firefox"), "反选后 B 被勾上");
            True(vm.IsChecked("microsoft.vcredist"), "反选后运行库被勾上");
            True(!vm.IsChecked("arp\\legacy"), "反选不影响不可重装项");

            // ⑤ 导出范围 = 已勾选的可重装包
            IList<Package> selected = vm.SelectedPackages();
            Equal(3, selected.Count, "已勾选 3 项（A + B + VCRedist）");
            True(vm.AllPackages().Count == 4, "AllPackages 返回全部 4 项（导出清单用）");
            True(vm.HasActiveFilters() == false, "无筛选状态");
        }

        /// <summary>fsio.py:17 newline="" 保 CRLF；JsonWriter 控制字符转义（json.dumps 等价）。</summary>
        private static void CaseMiscUtils()
        {
            Equal("a\nb\nc", EncodedText.NormalizeNewlines("a\r\nb\rc"), "NormalizeNewlines 统一为 \\n");
            Equal(string.Empty, EncodedText.NormalizeNewlines(null), "null → 空串");

            var value = JsonValue.NewObject();
            value.Set("s", JsonValue.From("a\u0001b\u001Fc\u2028d/e"));
            string text = JsonWriter.Serialize(value);
            True(text.Contains("\\u0001"), "控制字符转义为 \\u00XX");
            True(text.Contains("\\u001f"), "0x1F 转义");
            True(text.Contains("\\u2028"), "行分隔符转义");
            True(text.Contains("/"), "斜杠不转义（与 Python json.dumps 一致）");
            JsonValue parsed = JsonReader.Parse(text);
            Equal("a\u0001b\u001Fc\u2028d/e", parsed.Get("s").AsString(), "控制字符回环");

            Equal(3, JsonReader.Parse("3").AsLong(), "顶层整数");
            True(JsonReader.Parse("null").IsNull, "顶层 null");
            bool topBool;
            True(JsonReader.Parse("true").TryGetBool(out topBool) && topBool, "顶层 true");
        }

        // ══════════════ 运行库判定三项改造（T33~T37） ══════════════

        /// <summary>
        /// 计数口径（本轮改造 2）：<see cref="CatalogCounts"/> 新增 <c>DependenciesAll</c>，
        /// <c>Dependencies</c> 语义改为「可重装 ∧ IsDependency」，JSON 输出两个键（键名与
        /// docs/ARCHITECTURE.md 一致：<c>counts.dependencies</c> / <c>counts.dependencies_all</c>）。
        /// </summary>
        private static void CaseCatalogCountsJson()
        {
            var counts = new CatalogCounts
            {
                Total = 3,
                Reinstallable = 2,
                Unsupported = 1,
                Dependencies = 1,
                DependenciesAll = 2
            };

            JsonValue node = new CatalogSnapshot { Counts = counts }.ToJson().Get("counts");
            True(node != null, "① counts 节点存在");
            True(node != null && node.Get("dependencies") != null, "① counts.dependencies 键存在（未改名）");
            True(node != null && node.Get("dependencies_all") != null, "① counts.dependencies_all 键存在（新增）");
            Equal(1, node.Get("dependencies").AsLong(), "① counts.dependencies = 1");
            Equal(2, node.Get("dependencies_all").AsLong(), "① counts.dependencies_all = 2");
            True(node.Get("dependencies_all").AsLong() >= node.Get("dependencies").AsLong(),
                 "① dependencies_all >= dependencies");
            Equal(3, node.Get("total").AsLong(), "① counts.total 不变");
            Equal(2, node.Get("reinstallable").AsLong(), "① counts.reinstallable 不变");
            Equal(1, node.Get("unsupported").AsLong(), "① counts.unsupported 不变");

            // 序列化后的 JSON 文本里键名必须是下划线形式（导出/诊断的消费方按此取值）
            string text = JsonWriter.Serialize(new CatalogSnapshot { Counts = counts }.ToJson());
            True(text.Contains("\"dependencies_all\""), "② JSON 文本含 \"dependencies_all\" 键名");
            True(text.Contains("\"dependencies\""), "② JSON 文本含 \"dependencies\" 键名");
            JsonValue round = JsonReader.Parse(text);
            Equal(2, round.Get("counts").Get("dependencies_all").AsLong(), "② 回环读回 dependencies_all = 2");

            // Counts 为 null 时 ToJson 仍要输出这两个键（防御分支，不能让消费方读到缺键）
            JsonValue nullNode = new CatalogSnapshot { Counts = null }.ToJson().Get("counts");
            True(nullNode.Get("dependencies") != null, "③ Counts=null → dependencies 键仍存在");
            True(nullNode.Get("dependencies_all") != null, "③ Counts=null → dependencies_all 键仍存在");
            Equal(0, nullNode.Get("dependencies_all").AsLong(), "③ Counts=null → dependencies_all = 0");
        }

        /// <summary>
        /// 计数口径端到端（本轮改造 2）：跑真实的 <c>PackageCatalog.BuildSnapshotAsync</c>
        /// （仓储层用假实现回放固定 winget list 输出，不启动任何子进程）。
        /// 场景：1 可重装运行库 + 1 不可重装运行库 + 1 普通软件。
        /// </summary>
        private static void CaseCatalogCountsScope()
        {
            var emptyCache = new RuntimeCache(Path.Combine(NewTempDir(), "empty.json"), null);
            PackageCatalog catalog = NewCatalog(WingetListCounts, emptyCache, null);
            CatalogSnapshot snapshot = Scan(catalog);

            Equal(3, snapshot.Packages.Count, "① 三个包都被解析出来");
            Equal(3, snapshot.Counts.Total, "① Counts.Total = 3");
            Equal(2, snapshot.Counts.Reinstallable, "① Counts.Reinstallable = 2");
            Equal(1, snapshot.Counts.Unsupported, "① Counts.Unsupported = 1");
            Equal(1, snapshot.Counts.Dependencies, "① Counts.Dependencies = 1（可重装 ∧ IsDependency）");
            Equal(2, snapshot.Counts.DependenciesAll, "① Counts.DependenciesAll = 2（含不可重装的运行库）");
            True(snapshot.Counts.DependenciesAll >= snapshot.Counts.Dependencies, "① DependenciesAll >= Dependencies");

            // 逐条核对判定（铁律：关键词命中即运行库，与能否重装无关）
            Package vcredist = FindPackage(snapshot, "Microsoft.VCRedist.2015+.x64");
            True(vcredist != null && vcredist.Reinstallable && vcredist.IsDependency,
                 "② VCRedist：可重装 ∧ 运行库");
            Package legacy = FindPackage(snapshot, "Some.VC_redist.9");
            True(legacy != null && !legacy.Reinstallable && legacy.IsDependency,
                 "② Some.VC_redist.9：不可重装，但关键词命中 → IsDependency 仍为 true");
            Package chrome = FindPackage(snapshot, "Google.Chrome");
            True(chrome != null && chrome.Reinstallable && !chrome.IsDependency, "② Chrome：可重装 ∧ 非运行库");

            // 界面口径 = 「运行库」分组行数 = Counts.Dependencies（三者必须相等）
            int groupRows = 0;
            int dependencyPackages = 0;
            for (int i = 0; i < snapshot.Packages.Count; i++)
            {
                Package package = snapshot.Packages[i];
                if (package != null && package.IsDependency)
                {
                    dependencyPackages++;
                }

                if (package != null && string.Equals(Groups.GroupOf(package), Groups.Dependency, StringComparison.Ordinal))
                {
                    groupRows++;
                }
            }

            Equal(1, groupRows, "③ 「运行库」分组行数 = 1（与界面「运行库 N」同口径）");
            Equal(2, dependencyPackages, "③ IsDependency 的包共 2 个");
            Equal(snapshot.Counts.Dependencies, groupRows, "③ Counts.Dependencies == 分组行数（口径统一）");
            Equal(snapshot.Counts.DependenciesAll, dependencyPackages, "③ Counts.DependenciesAll == IsDependency 总数");

            // 关键词命中的运行库不得进入待核查列表（铁律：不进 PendingMetadataIds）
            IList<string> pending = catalog.PendingMetadataIds(snapshot);
            Equal(1, pending.Count, "④ 待核查只剩 1 个（运行库不进 PendingMetadataIds）");
            Equal("Google.Chrome", pending[0], "④ 待核查列表 = [Google.Chrome]");

            // 快照 JSON 的两个计数与内存计数一致
            JsonValue counts = snapshot.ToJson().Get("counts");
            Equal(1, counts.Get("dependencies").AsLong(), "⑤ 快照 JSON counts.dependencies = 1");
            Equal(2, counts.Get("dependencies_all").AsLong(), "⑤ 快照 JSON counts.dependencies_all = 2");
        }

        /// <summary>
        /// 关键词命中诊断日志（本轮改造 3）：<c>FormatTopKeywordHits</c> 的排序（次数降序、
        /// 同次数按词名字母序）与文案；无命中词时文案保持改前原样。
        /// 顺带锁定「命中行数按 raw 行累加、去重前」这一口径差异（同 ID 的 x86/x64 两行各计一次）。
        /// </summary>
        private static void CaseKeywordHitLog()
        {
            // ① 两个命中词各 1 次 → 同次数按词名字母序（ordinal：'_' < 'r'）
            AppLogger bothLog = AppLogger.CreateInMemory();
            var bothLines = new List<string>();
            bothLog.LogEmitted += delegate(DateTime at, string tag, string text) { bothLines.Add(text); };
            Scan(NewCatalog(WingetListCounts, null, bothLog));
            string both = FindLogLine(bothLines, "目录刷新完成");
            True(both != null, "① 刷新结束日志存在");
            True(both != null && both.Contains("3 项（可重装 2 项，关键词命中 2 行：vc_redist×1, vcredist×1）"),
                 "① 日志 = 计数 + 命中词明细（同次数按词名字母序）");

            // ② 无命中词 → 文案与改前逐字一致（不追加「：」明细）
            AppLogger plainLog = AppLogger.CreateInMemory();
            var plainLines = new List<string>();
            plainLog.LogEmitted += delegate(DateTime at, string tag, string text) { plainLines.Add(text); };
            Scan(NewCatalog(WingetListPlain, null, plainLog));
            string plain = FindLogLine(plainLines, "目录刷新完成");
            True(plain != null && plain.Contains("2 项（可重装 2 项，关键词命中 0 行）"),
                 "② 无命中词 → 「关键词命中 0 行）」文案不变");
            True(plain != null && !plain.Contains("×"), "② 无命中词 → 日志里没有 × 明细");

            // ③ 去重前口径差异：同一 ID 的 x86/x64 两行各计一次
            AppLogger dupLog = AppLogger.CreateInMemory();
            var dupLines = new List<string>();
            dupLog.LogEmitted += delegate(DateTime at, string tag, string text) { dupLines.Add(text); };
            CatalogSnapshot dupSnapshot = Scan(NewCatalog(WingetListDuplicate, null, dupLog));
            string dup = FindLogLine(dupLines, "目录刷新完成");
            Equal(2, dupSnapshot.Counts.Total, "③ 同 ID 两行去重后 Total = 2");
            Equal(1, dupSnapshot.Counts.Dependencies, "③ 「运行库」只有 1 项（去重后）");
            True(dup != null && dup.Contains("关键词命中 2 行：vcredist×2"),
                 "③ 日志命中行数 = 2（按 raw 行累加，与去重后的 1 项不同——已知口径差异）");
        }

        /// <summary>
        /// TTL 与 <see cref="PackageCatalog.PendingMetadataIds"/> 的联动（本轮改造 1 的实际收益）：
        /// 条目过期后不再让该包被跳过，下一轮会重新 <c>winget show</c> 纠正旧结论；
        /// <c>ttl=0</c>（永不过期）时老结论仍被采信。
        /// </summary>
        private static void CasePendingMetadataWithTtl()
        {
            string dir = NewTempDir();
            string stalePath = Path.Combine(dir, "stale.json");
            // dep=false + 100 天前：旧结论「不是运行库」，但对 Google.Chrome 这种普通软件没影响，
            // 这里只关心「是否还会再去核查」
            File.WriteAllText(stalePath, BuildCacheJson("Google.Chrome", false, FormatStamp(DateTimeOffset.Now.AddDays(-100))),
                Encoding.UTF8);

            var staleCache = new RuntimeCache(stalePath, null);
            PackageCatalog staleCatalog = NewCatalog(WingetListPlain, staleCache, null);
            IList<string> stalePending = staleCatalog.PendingMetadataIds(Scan(staleCatalog));
            Equal(2, stalePending.Count, "① 过期 → 两个包都待核查");
            True(ContainsId(stalePending, "Google.Chrome"), "① 过期条目不生效 → Chrome 重新进入待核查列表");
            Equal(1, staleCache.Count, "① 过期条目没有被删除（Count 仍 1）");

            var neverCache = new RuntimeCache(stalePath, null, 0);
            PackageCatalog neverCatalog = NewCatalog(WingetListPlain, neverCache, null);
            IList<string> neverPending = neverCatalog.PendingMetadataIds(Scan(neverCatalog));
            Equal(1, neverPending.Count, "② ttl=0 → 只剩没有缓存记录的包待核查");
            True(!ContainsId(neverPending, "Google.Chrome"), "② ttl=0 → 老结论仍被采信 → Chrome 不再核查");
            True(ContainsId(neverPending, "Microsoft.WindowsNotepad"), "② 无缓存记录的包照旧待核查");
        }

        /// <summary>
        /// 配置项（本轮改造 1）：<c>metadataTtlDays</c> 是可选参数（既有四参调用点无需改动），
        /// 环境变量 <c>WPM_META_TTL_DAYS</c> 取值范围 0~3650，非法值抛 <see cref="ConfigInvalidException"/>。
        /// </summary>
        private static void CaseAppConfigMetadataTtl()
        {
            Equal(90, AppConfig.DefaultMetadataTtlDays, "① DefaultMetadataTtlDays = 90");
            Equal(90, new AppConfig("", "", 180, 60).MetadataTtlDays, "① 四参构造（既有调用点）→ 默认 90");
            Equal(30, new AppConfig("", "", 180, 60, 30).MetadataTtlDays, "① 五参构造 → 30");
            Equal(90, AppConfig.CreateDefault().MetadataTtlDays, "① CreateDefault → 90");

            string original = Environment.GetEnvironmentVariable("WPM_META_TTL_DAYS");
            try
            {
                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "7");
                Equal(7, AppConfig.Load().MetadataTtlDays, "② WPM_META_TTL_DAYS=7 → 7");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", " 30 ");
                Equal(30, AppConfig.Load().MetadataTtlDays, "② 两端空白被 Trim → 30");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "0");
                Equal(0, AppConfig.Load().MetadataTtlDays, "② 0（永不过期）合法");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "3650");
                Equal(3650, AppConfig.Load().MetadataTtlDays, "② 3650（上界）合法");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "3651");
                Throws<ConfigInvalidException>(delegate { AppConfig.Load(); }, "② 3651 超上界 → ConfigInvalidException");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "-1");
                Throws<ConfigInvalidException>(delegate { AppConfig.Load(); }, "② -1 低于下界 → ConfigInvalidException");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "abc");
                Throws<ConfigInvalidException>(delegate { AppConfig.Load(); }, "② 非整数 → ConfigInvalidException");

                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", "");
                Equal(90, AppConfig.Load().MetadataTtlDays, "② 空串 → 回落默认 90");
            }
            finally
            {
                Environment.SetEnvironmentVariable("WPM_META_TTL_DAYS", original);
            }
        }

        // ══════════════ 目录扫描替身（不启动任何子进程） ══════════════

        private static PackageCatalog NewCatalog(string listOutput, RuntimeCache cache, AppLogger log)
        {
            // 四参 AppConfig：顺带验证 TTL 可选参数没有破坏既有调用点
            var config = new AppConfig(string.Empty, string.Empty, 180, 60);
            return new PackageCatalog(config, new FakeWingetRepository(listOutput), cache, log);
        }

        private static CatalogSnapshot Scan(PackageCatalog catalog)
        {
            return catalog.GetCatalogAsync(true, CancellationToken.None, "1.0.0").GetAwaiter().GetResult();
        }

        /// <summary>假仓储层：只回放固定的 <c>winget list</c> 输出，绝不启动子进程。</summary>
        private sealed class FakeWingetRepository : IWingetRepository
        {
            private readonly string _listOutput;

            public FakeWingetRepository(string listOutput)
            {
                _listOutput = listOutput ?? string.Empty;
            }

            public string WingetExe
            {
                get { return string.Empty; }
            }

            public Task<CommandResult> RunAsync(string[] args, int? timeoutSeconds, CancellationToken cancellationToken)
            {
                return Task.FromResult(new CommandResult(args, 0, string.Empty, string.Empty, 0));
            }

            public Task<string> VersionAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult("1.0.0");
            }

            public Task<CommandResult> ListInstalledAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new CommandResult(new[] { "list" }, 0, _listOutput, string.Empty, 0));
            }

            public Task<CommandResult> ShowMetadataAsync(string packageId, CancellationToken cancellationToken)
            {
                return Task.FromResult(new CommandResult(new[] { "show" }, 0, string.Empty, string.Empty, 0));
            }
        }

        private static Package FindPackage(CatalogSnapshot snapshot, string packageId)
        {
            if (snapshot == null || snapshot.Packages == null)
            {
                return null;
            }

            for (int i = 0; i < snapshot.Packages.Count; i++)
            {
                Package package = snapshot.Packages[i];
                if (package != null && string.Equals(package.PackageIdentifier, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return package;
                }
            }

            return null;
        }

        private static bool ContainsId(IList<string> ids, string packageId)
        {
            if (ids == null)
            {
                return false;
            }

            for (int i = 0; i < ids.Count; i++)
            {
                if (string.Equals(ids[i], packageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FindLogLine(IList<string> lines, string marker)
        {
            if (lines == null)
            {
                return null;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line != null && line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    return line;
                }
            }

            return null;
        }

        // ══════════════ 公共小工具 ══════════════

        /// <summary>
        /// 本轮审计修复的回归锁：JSON 解析器必须有嵌套上限。
        /// <para>
        /// 递归下降解析器遇到「几万层 <c>[</c>」的损坏文件会打爆线程栈，而
        /// <c>StackOverflowException</c> 在 .NET 里**不可捕获**——进程直接消失，
        /// 连 <c>AppDomain.UnhandledException</c> 都收不到，用户看到的是「双击没反应」。
        /// 加了上限之后，这类文件走正常的 BadRequestException 分支（上层回退空数据 + 记 Warn）。
        /// </para>
        /// </summary>
        private static void CaseJsonDepthLimit()
        {
            string atLimit = new string('[', JsonReader.MaxDepth) + new string(']', JsonReader.MaxDepth);
            True(JsonReader.Parse(atLimit) != null, "嵌套 " + JsonReader.MaxDepth + " 层（上限）正常解析");

            string overLimit = new string('[', JsonReader.MaxDepth + 1) + new string(']', JsonReader.MaxDepth + 1);
            Throws<BadRequestException>(delegate { JsonReader.Parse(overLimit); }, "嵌套 " + (JsonReader.MaxDepth + 1) + " 层 → BadRequestException");

            // 真正的威胁输入：未闭合的 10 万层。旧实现会在这里栈溢出直接杀进程
            string huge = new string('[', 100000);
            Throws<BadRequestException>(delegate { JsonReader.Parse(huge); }, "10 万层未闭合 → 抛异常而不是栈溢出");

            JsonValue ignored;
            True(!JsonReader.TryParse(overLimit, out ignored), "TryParse 对超深文件返回 false（调用方回退空数据）");

            // 对象与数组共用同一个深度计数
            var sb = new StringBuilder();
            for (int i = 0; i <= JsonReader.MaxDepth; i++)
            {
                sb.Append("{\"a\":");
            }

            sb.Append('1');
            for (int i = 0; i <= JsonReader.MaxDepth; i++)
            {
                sb.Append('}');
            }

            Throws<BadRequestException>(delegate { JsonReader.Parse(sb.ToString()); }, "对象嵌套超限同样被拒");

            // 深度计数必须还原：同一个文本连续解析两次都要给出同样的结论
            True(!JsonReader.TryParse(overLimit, out ignored), "第二次解析超深文本仍返回 false（深度计数已还原）");
        }

        /// <summary>
        /// 本轮审计修复：解码兜底必须能识别「解出来全是 U+FFFD」。
        /// <para>
        /// 机器开了「Beta: 使用 UTF-8 提供全球语言支持」时，默认代码页是 65001，
        /// <c>Encoding.Default.GetString</c> 会把 GBK 字节解成一串替换字符且**不抛异常**；
        /// 旧实现看到「非空字符串」就 return，GBK 兜底永远轮不到，界面上就是一片「口口口」。
        /// </para>
        /// </summary>
        private static void CaseDecodeReplacementGuard()
        {
            True(EncodedText.ContainsReplacementChar("a\uFFFDb"), "含 U+FFFD → 判定为解码失真");
            True(!EncodedText.ContainsReplacementChar("正常中文"), "正常中文 → 不算失真");
            True(!EncodedText.ContainsReplacementChar(string.Empty), "空串 → 不算失真");
            True(!EncodedText.ContainsReplacementChar(null), "null → 不算失真（不抛）");

            byte[] gbk = Encoding.GetEncoding(936).GetBytes("运行库 中文测试");
            string decoded = EncodedText.Decode(gbk);
            Equal("运行库 中文测试", decoded, "GBK 字节解码结果正确");
            True(!EncodedText.ContainsReplacementChar(decoded), "GBK 解码结果不含替换字符");

            // UTF-8 与 BOM 两条老路径不能被这次改动带坏
            Equal("中文", EncodedText.Decode(Encoding.UTF8.GetBytes("\uFEFF中文")), "UTF-8 BOM 仍被剥掉");
            Equal(string.Empty, EncodedText.Decode(new byte[0]), "空字节数组 → 空串");
        }

        /// <summary>
        /// 本轮审计修复的回归锁：打包脚本不得整目录删除 <c>dist</c>。
        /// <para>
        /// 便携模式下 <c>dist\Data</c> 就是用户数据（勾选记录、判定缓存、日志），
        /// 旧脚本的 <c>rd /s /q "%DIST%"</c> 会在每次重打包时把它们静默删光。
        /// .bat 没法在自测进程里执行，只能用文本断言把「不许再出现整目录删除」钉住。
        /// </para>
        /// </summary>
        private static void CaseBuildScriptKeepsData()
        {
            string path = FindRepoFile("build-release.bat");
            True(path != null, "定位到 build-release.bat");
            if (path == null)
            {
                return;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);
            True(text.IndexOf("rd /s /q \"%DIST%\"", StringComparison.OrdinalIgnoreCase) < 0,
                 "不再整目录删除 dist（否则会带走 dist\\Data）");
            True(text.IndexOf("del /q \"%DIST%\\WingetManager.exe\"", StringComparison.OrdinalIgnoreCase) >= 0,
                 "只删除旧的 exe");
            True(text.IndexOf("\"%DIST%\\*.pdb\"", StringComparison.OrdinalIgnoreCase) >= 0,
                 "顺手清理旧 pdb 残留");
        }

        /// <summary>从测试程序所在目录向上找仓库根里的文件（最多 6 层）。</summary>
        private static string FindRepoFile(string fileName)
        {
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir.FullName, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception)
            {
                // 定位失败按「找不到」处理，由断言给出结论
            }

            return null;
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "wpm-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
