using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WingetManagerWpf.Core;
using WingetManagerWpf.Core.Json;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Services
{
    /// <summary>
    /// 清单生成：把勾选的软件包转成 winget 官方 import 格式，并生成「双击即恢复」的 bat
    /// 与「无法自动重装清单」txt（逐行移植 <c>_ref/services/profiles.py</c>）。
    /// <para>
    /// 三种产物的编码**故意不同**（最容易被「统一成 UTF-8」的善意重构毁掉）：
    /// <list type="bullet">
    /// <item><c>apps.json</c>：UTF-8 **无 BOM** + 2 空格缩进 + 尾随换行；</item>
    /// <item><c>恢复-*.bat</c>：**GBK + CRLF**（cmd 代码页 936，UTF-8 会乱码）；</item>
    /// <item><c>无法自动重装清单-*.html</c>：**UTF-8 无 BOM**（<c>meta charset</c> 声明编码）+ CRLF。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ExportProfiles
    {
        /// <summary>winget import 的 profile schema 2.0 地址（官方常量，别改）。</summary>
        public const string SchemaUrl = "https://aka.ms/winget-packages.schema.2.0.json";

        /// <summary>日志级别标记（与 UI 的 LogTag 字符串对齐）。</summary>
        public const string LogInfo = "info";
        public const string LogDone = "done";
        public const string LogFail = "fail";
        public const string LogSkip = "skip";

        /// <summary>winget 的 import schema 要求 WinGetVersion 数字开头，故用正则提取。</summary>
        private static readonly Regex VersionRegex = new Regex(
            @"^v?(\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.\-]+)?)", RegexOptions.CultureInvariant);

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// HTML 清单的内联样式（浅色为主，跟随系统深色；打印时去掉交互元素并转黑白）。
        /// 不引用任何外部字体/图片/脚本 —— 新装的系统往往还没联网，也可能没装思源之类的字体。
        /// </summary>
        private const string UninstallableHtmlStyle =
            ":root{--bg:#f6f7f9;--card:#fff;--ink:#1a1d23;--ink2:#5b6270;--ink3:#8b93a1;--line:#e6e8ec;--brand:#2f6fed;--warn:#a4620a}" +
            "@media (prefers-color-scheme:dark){:root{--bg:#15171b;--card:#1c1f25;--ink:#e8eaee;--ink2:#a7aeba;--ink3:#7b8394;--line:#2a2e36;--brand:#5b93ff;--warn:#e0a33a}}" +
            "*{box-sizing:border-box}" +
            "body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.6 system-ui,-apple-system,'Segoe UI','Microsoft YaHei',sans-serif}" +
            ".wrap{max-width:820px;margin:0 auto;padding:28px 20px 56px}" +
            "h1{font-size:22px;margin:0 0 6px;letter-spacing:.01em}" +
            ".meta{margin:0;color:var(--ink3);font-size:13px}" +
            ".lead{margin:14px 0 0;color:var(--ink2);font-size:14px}" +
            ".stats{display:flex;gap:10px;margin:18px 0 8px}" +
            ".stat{flex:1;background:var(--card);border:1px solid var(--line);border-radius:11px;padding:11px 14px}" +
            ".stat b{display:block;font-size:22px;line-height:1.15}" +
            ".stat span{font-size:12px;color:var(--ink3)}" +
            ".stat.todo b{color:var(--brand)}" +
            ".bar{height:6px;background:var(--line);border-radius:3px;overflow:hidden;margin:0 0 22px}" +
            ".bar i{display:block;height:100%;width:0;background:var(--brand);transition:width .25s ease}" +
            "nav{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:0 0 18px}" +
            "nav button{border:1px solid var(--line);background:var(--card);color:var(--ink2);border-radius:8px;padding:6px 13px;font:inherit;font-size:13px;cursor:pointer}" +
            "nav button.on{background:var(--brand);border-color:var(--brand);color:#fff}" +
            "#q{margin-left:auto;border:1px solid var(--line);background:var(--card);color:var(--ink);border-radius:8px;padding:7px 12px;font:inherit;font-size:13px;min-width:190px}" +
            "section{margin:0 0 26px}" +
            "h2{display:flex;align-items:center;gap:8px;margin:0 0 4px;font-size:14px;color:var(--ink2);letter-spacing:.02em}" +
            "h2 em{font-style:normal;background:var(--line);color:var(--ink2);border-radius:20px;padding:1px 9px;font-size:12px}" +
            ".hint{margin:0 0 10px;color:var(--ink3);font-size:13px}" +
            "ul{list-style:none;margin:0;padding:0;background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden}" +
            "li{display:flex;gap:12px;align-items:flex-start;padding:12px 14px;border-top:1px solid var(--line)}" +
            "li:first-child{border-top:0}" +
            "li.done{opacity:.55}" +
            "li.done .nm{text-decoration:line-through}" +
            "li.hide{display:none}" +
            "input[type=checkbox]{appearance:none;-webkit-appearance:none;width:20px;height:20px;margin:1px 0 0;flex:0 0 auto;position:relative;cursor:pointer;background:transparent;border:1.5px solid var(--ink3);border-radius:6px}" +
            "input[type=checkbox]:checked{background:var(--brand);border-color:var(--brand)}" +
            "input[type=checkbox]:checked::after{content:'';position:absolute;left:6px;top:2px;width:6px;height:11px;border:solid #fff;border-width:0 2px 2px 0;transform:rotate(45deg)}" +
            ".body{flex:1;min-width:0}" +
            ".nm{font-weight:600;cursor:pointer;word-break:break-word}" +
            ".ver{margin-left:6px;color:var(--ink3);font-size:13px;font-weight:400}" +
            ".tag{display:inline-block;margin-left:8px;padding:1px 7px;border-radius:6px;font-size:11px;background:var(--line);color:var(--ink2);vertical-align:1px;white-space:nowrap}" +
            ".tag.warn{background:rgba(180,110,10,.14);color:var(--warn)}" +
            ".note{margin:3px 0 0;color:var(--ink3);font-size:13px;word-break:break-all}" +
            ".go{flex:0 0 auto;font-size:12px;color:var(--brand);text-decoration:none;border:1px solid var(--line);border-radius:8px;padding:4px 10px;white-space:nowrap}" +
            ".go:hover{border-color:var(--brand)}" +
            "footer{display:flex;gap:14px;flex-wrap:wrap;margin-top:4px;color:var(--ink3);font-size:12px}" +
            "footer button{border:0;background:none;padding:0;font:inherit;font-size:12px;color:var(--ink3);text-decoration:underline;cursor:pointer}" +
            "@media print{body{background:#fff}.wrap{max-width:none;padding:0}.stats,.bar,nav,.go,footer{display:none!important}" +
            "h1{font-size:18px}ul,li{border-color:#c8c8c8}.tag{border:1px solid #999}li{break-inside:avoid}}";

        /// <summary>
        /// HTML 清单的内联脚本：勾选持久化（localStorage）+ 进度条 + 全部/未装/已装筛选 + 搜索。
        /// <para>
        /// 全部操作都包在 <c>try/catch</c> 里 —— <c>file://</c> 下个别浏览器把 localStorage 当
        /// 不可用（抛 SecurityError），这时勾选照样当次可用，只是关掉页面不记得，不能因此让整页脚本挂掉。
        /// </para>
        /// </summary>
        private const string UninstallableHtmlScript =
            "(function(){" +
            "var KEY='wpm.uninstallable.';" +
            "var items=Array.prototype.slice.call(document.querySelectorAll('li.item'));" +
            "var boxes=items.map(function(li){return li.querySelector('input[type=checkbox]');});" +
            "function keyOf(b){return KEY+(b.getAttribute('data-key')||'');}" +
            "function store(b){try{if(b.checked){localStorage.setItem(keyOf(b),'1');}else{localStorage.removeItem(keyOf(b));}}catch(e){}}" +
            "function restore(){for(var i=0;i<boxes.length;i++){try{if(localStorage.getItem(keyOf(boxes[i]))==='1'){boxes[i].checked=true;}}catch(e){}}}" +
            "var filter='all',query='';" +
            "function apply(){for(var i=0;i<items.length;i++){var li=items[i],b=boxes[i];" +
            "var okFilter=(filter==='all')||(filter==='done'?b.checked:!b.checked);" +
            "var okQuery=!query||(li.getAttribute('data-name')||'').indexOf(query)>=0;" +
            "li.className='item'+(b.checked?' done':'')+((okFilter&&okQuery)?'':' hide');}" +
            "var secs=document.querySelectorAll('section');" +
            "for(var s=0;s<secs.length;s++){var vis=secs[s].querySelectorAll('li.item:not(.hide)').length;secs[s].style.display=vis?'':'none';}}" +
            "function refresh(){var done=0;for(var i=0;i<boxes.length;i++){if(boxes[i].checked){done++;}}" +
            "var total=boxes.length,pct=total?Math.round(done*100/total):100;" +
            "document.getElementById('done').textContent=done;" +
            "document.getElementById('left').textContent=total-done;" +
            "document.getElementById('pct').textContent=pct+'%';" +
            "document.getElementById('bar').style.width=pct+'%';apply();}" +
            "for(var i=0;i<boxes.length;i++){(function(b){b.addEventListener('change',function(){store(b);refresh();});})(boxes[i]);}" +
            "for(var k=0;k<items.length;k++){(function(li,b){var nm=li.querySelector('.nm');" +
            "if(nm){nm.addEventListener('click',function(){b.checked=!b.checked;store(b);refresh();});}})(items[k],boxes[k]);}" +
            "var btns=document.querySelectorAll('nav button');" +
            "for(var j=0;j<btns.length;j++){(function(btn){btn.addEventListener('click',function(){" +
            "for(var m=0;m<btns.length;m++){btns[m].className='';}btn.className='on';" +
            "filter=btn.getAttribute('data-filter');apply();});})(btns[j]);}" +
            "var q=document.getElementById('q');" +
            "if(q){q.addEventListener('input',function(){query=(q.value||'').trim().toLowerCase();apply();});}" +
            "var reset=document.getElementById('reset');" +
            "if(reset){reset.addEventListener('click',function(){if(!confirm('清空全部勾选？')){return;}" +
            "for(var i=0;i<boxes.length;i++){boxes[i].checked=false;store(boxes[i]);}refresh();});}" +
            "restore();refresh();})();";


        /// <summary>把 winget 版本号规范成 import schema 能接受的形式；不匹配回落 "1.0.0"。</summary>
        public static string NormalizeWingetVersion(string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            Match match = VersionRegex.Match(text);
            return match.Success ? match.Groups[1].Value : "1.0.0";
        }

        /// <summary>
        /// winget import schema 2.0 的 JSON 文本（UTF-8 无 BOM、2 空格缩进、键序固定：
        /// <c>$schema</c> → <c>CreationDate</c> → <c>Sources</c> → <c>WinGetVersion</c>）。
        /// <para>
        /// Sources 按源名的**字典序**排列：原版用 <c>sorted(dict.items())</c>，
        /// 而 <c>"msstore" &lt; "winget"</c>，所以 **msstore 在前**（不是 winget 在前，别按直觉改）。
        /// </para>
        /// Packages 只写 <c>PackageIdentifier</c>：写死 <c>Version</c> 会在该版本从源下架后
        /// 让 <c>--ignore-unavailable</c> 之外的情况直接失败（见架构文档 §5-1）。
        /// </summary>
        public static string BuildManifest(IEnumerable<Package> selected, string wingetVersion)
        {
            var bySource = new Dictionary<string, List<Package>>(StringComparer.Ordinal);
            if (selected != null)
            {
                foreach (Package package in selected)
                {
                    if (package == null || !package.Reinstallable)
                    {
                        continue;
                    }

                    string source = IsKnownSource(package.Source) ? package.Source : "winget";
                    List<Package> bucket;
                    if (!bySource.TryGetValue(source, out bucket))
                    {
                        bucket = new List<Package>();
                        bySource[source] = bucket;
                    }

                    bucket.Add(package);
                }
            }

            JsonValue root = JsonValue.NewObject();
            root.Set("$schema", JsonValue.From(SchemaUrl));
            // 毫秒精度 + 时区偏移，与原版 isoformat(timespec="milliseconds") 等价
            root.Set("CreationDate", JsonValue.From(DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", Inv)));

            var sourceNames = new List<string>(bySource.Keys);
            sourceNames.Sort(StringComparer.Ordinal);

            JsonValue sources = JsonValue.NewArray();
            for (int i = 0; i < sourceNames.Count; i++)
            {
                string sourceName = sourceNames[i];
                JsonValue entry = JsonValue.NewObject();

                JsonValue packages = JsonValue.NewArray();
                List<Package> items = bySource[sourceName];
                for (int p = 0; p < items.Count; p++)
                {
                    JsonValue item = JsonValue.NewObject();
                    item.Set("PackageIdentifier", JsonValue.From(items[p].PackageIdentifier));
                    packages.Add(item);
                }

                entry.Set("Packages", packages);
                entry.Set("SourceDetails", BuildSourceDetails(sourceName));
                sources.Add(entry);
            }

            root.Set("Sources", sources);
            root.Set("WinGetVersion", JsonValue.From(NormalizeWingetVersion(wingetVersion)));

            // 与 WriteJsonAtomic 一致：序列化后补一个尾随换行
            return JsonWriter.Serialize(root) + "\n";
        }

        /// <summary>按来源统计可重装包（winget / msstore），用于导出后的提示。</summary>
        public static Models.SourceBreakdown SourceBreakdown(IEnumerable<Package> selected)
        {
            // 类型与方法是同名（方法名是架构文档定的），这里用 Models. 前缀避免「同名解析」歧义
            var breakdown = new Models.SourceBreakdown();
            if (selected == null)
            {
                return breakdown;
            }

            foreach (Package package in selected)
            {
                if (package == null || !package.Reinstallable)
                {
                    continue;
                }

                if (IsKnownSource(package.Source) && string.Equals(package.Source, "msstore", StringComparison.Ordinal))
                {
                    breakdown.MsStore++;
                }
                else
                {
                    breakdown.Winget++;
                }
            }

            return breakdown;
        }

        /// <summary>
        /// 生成「双击即恢复」的批处理，返回 **GBK 字节**（调用方直接写盘）。
        /// <para>
        /// 安全要点：清单名是用户可控的，直接拼进 cmd 会被 <c>&amp;</c>、<c>|</c>、
        /// <c>%VAR%</c> 解释成命令。这里只取 basename 防目录穿越，<c>%</c> 双写防变量展开。
        /// </para>
        /// GBK 编不进去（emoji 等）时抛 <see cref="BadRequestException"/>，文案与原版一致。
        /// </summary>
        public static byte[] BuildRestoreBat(string jsonFileName, int count, int msStoreCount)
        {
            string name = Path.GetFileName(jsonFileName ?? string.Empty);
            if (name.Length == 0)
            {
                name = "apps.json";
            }

            name = name.Replace("%", "%%");
            string text = RenderRestoreBat(name, count, msStoreCount);

            Encoding gbk;
            try
            {
                // 必须用 ExceptionFallback：默认的替换回退会把 emoji 静默改成 '?'，
                // 生成一个「看起来正常、实际跑不了」的脚本
                gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (Exception ex)
            {
                throw new BadRequestException("当前系统不支持 GBK(936) 编码：" + ex.Message, Strings.ExportGbkUnsupported);
            }

            try
            {
                return gbk.GetBytes(text);
            }
            catch (EncoderFallbackException)
            {
                // 只有文件名里含 emoji 之类 GBK 表示不了的字符才会走到这里
                throw new BadRequestException("清单文件名无法用 GBK 编码: " + name, Strings.ExportGbkUnsupported);
            }
        }

        /// <summary>
        /// 「无法自动重装清单」：**单文件 HTML**（调用方以 UTF-8 无 BOM 写盘，<c>meta charset</c> 声明编码）。
        /// 范围是**全部非可重装包**（不是勾选项）—— 勾选项里不可能有不可重装包。
        /// <para>
        /// 为什么不是 txt：这份东西的用途只有一个 —— 重装系统后照着它把软件一个个装回来。
        /// 纯文本能写的只有「名称 + 版本」，没法告诉用户「还剩几个没装」「哪些其实不用装」，
        /// 更不能一键跳到下载页。HTML 把这些都变成现成的：进度条、可勾选的待办、按用途分节、
        /// 每项一个「搜官网」按钮、以及打印样式（想手写勾也可以直接打印）。
        /// </para>
        /// <para>
        /// 三条硬约束：
        /// <list type="bullet">
        /// <item>**单文件、零外部资源**（CSS/JS 全内联、图标用字符）—— 重装后的新机器常常没网，
        /// 也不能指望它装了什么字体；</item>
        /// <item>**勾选状态存 localStorage**，随包 ID 记住，重复导出/重开页面都不会丢；
        /// 存不进去（file:// 下个别浏览器限制）也不报错，勾选照样当次可用；</item>
        /// <item>**所有用户数据必须转义**：软件名里出现 <c>&amp;</c>、<c>&lt;</c> 是常事，
        /// 不转义会把页面结构冲坏（见 <see cref="HtmlText"/>）。</item>
        /// </list>
        /// </para>
        /// </summary>
        public static string BuildUninstallableHtml(IEnumerable<Package> packages)
        {
            var apps = new List<Package>();
            var deps = new List<Package>();
            if (packages != null)
            {
                foreach (Package package in packages)
                {
                    if (package == null || package.Reinstallable)
                    {
                        continue;
                    }

                    // 运行库单独一节：重装时最该跳过的就是这些（别的软件会自动带上）
                    if (package.IsDependency)
                    {
                        deps.Add(package);
                    }
                    else
                    {
                        apps.Add(package);
                    }
                }
            }

            apps.Sort(CompareUninstallable);
            deps.Sort(CompareUninstallable);

            int total = apps.Count + deps.Count;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv);
            var sb = new StringBuilder(24 * 1024);

            sb.Append("<!DOCTYPE html>\r\n<html lang=\"zh-CN\">\r\n<head>\r\n");
            sb.Append("<meta charset=\"utf-8\">\r\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\r\n");
            sb.Append("<title>无法自动重装清单 · ").Append(total.ToString(Inv)).Append(" 项</title>\r\n");
            sb.Append("<style>\r\n").Append(UninstallableHtmlStyle).Append("</style>\r\n");
            sb.Append("</head>\r\n<body>\r\n<div class=\"wrap\">\r\n");

            sb.Append("<h1>无法自动重装清单</h1>\r\n");
            sb.Append("<p class=\"meta\">导出时间 ").Append(HtmlText(stamp)).Append(" · 共 ")
              .Append(total.ToString(Inv)).Append(" 项 · 本页离线可用</p>\r\n");

            if (total == 0)
            {
                sb.Append("<p class=\"lead\">（没有这类软件，全部都能自动重装）</p>\r\n");
                sb.Append("</div>\r\n</body>\r\n</html>\r\n");
                return sb.ToString();
            }

            sb.Append("<p class=\"lead\">这些软件在 winget 里没有可用的安装源，<b>恢复脚本不会处理它们</b>。")
              .Append("重装系统后照本页手动装回来 —— 装好一项就在左边勾上，进度会自动保存。</p>\r\n");

            sb.Append("<div class=\"stats\">\r\n");
            sb.Append("  <div class=\"stat todo\"><b id=\"left\">").Append(total.ToString(Inv))
              .Append("</b><span>待装</span></div>\r\n");
            sb.Append("  <div class=\"stat\"><b id=\"done\">0</b><span>已装好</span></div>\r\n");
            sb.Append("  <div class=\"stat\"><b id=\"pct\">0%</b><span>完成度</span></div>\r\n");
            sb.Append("</div>\r\n<div class=\"bar\"><i id=\"bar\"></i></div>\r\n");

            sb.Append("<nav>\r\n  <button type=\"button\" data-filter=\"all\" class=\"on\">全部</button>\r\n");
            sb.Append("  <button type=\"button\" data-filter=\"todo\">未装</button>\r\n");
            sb.Append("  <button type=\"button\" data-filter=\"done\">已装</button>\r\n");
            sb.Append("  <input id=\"q\" type=\"search\" placeholder=\"搜软件名…\" autocomplete=\"off\">\r\n</nav>\r\n");

            AppendHtmlSection(sb, "普通软件", apps, "标「无独立安装包」的，装的时候直接写进了系统，没有安装包可留 —— 到对应软件官网下最新版重装即可。");
            AppendHtmlSection(sb, "系统组件 / 运行库", deps, "一般由别的软件自动装进来，多数不用单独装；缺了对应软件会提示找不到 DLL。");

            sb.Append("<footer>");
            sb.Append("<span>勾选状态只存在本机浏览器里。</span>");
            sb.Append("<button type=\"button\" id=\"reset\">清空全部勾选</button>");
            sb.Append("</footer>\r\n</div>\r\n");
            sb.Append("<script>\r\n").Append(UninstallableHtmlScript).Append("</script>\r\n");
            sb.Append("</body>\r\n</html>\r\n");
            return sb.ToString();
        }

        /// <summary>写一节：标题（带计数）→ 一句说明 → 条目列表。空节整节省略。</summary>
        private static void AppendHtmlSection(StringBuilder sb, string title, List<Package> items, string hint)
        {
            if (items.Count == 0)
            {
                return;
            }

            sb.Append("<section>\r\n<h2>").Append(HtmlText(title)).Append(" <em>")
              .Append(items.Count.ToString(Inv)).Append("</em></h2>\r\n");
            if (!string.IsNullOrEmpty(hint))
            {
                sb.Append("<p class=\"hint\">").Append(HtmlText(hint)).Append("</p>\r\n");
            }

            sb.Append("<ul>\r\n");

            for (int i = 0; i < items.Count; i++)
            {
                Package package = items[i];
                string name = string.IsNullOrEmpty(package.Name) ? package.PackageIdentifier ?? string.Empty : package.Name;
                string tag = UninstallableTag(package);
                string note = ExplainUninstallableNote(package);

                // data-name 用于搜索：只放名称与版本。包 ID 是内部路径（ARP\.../msix\...），
                // 写进来对搜索没帮助，反而会把几十个字符的路径塞进页面；它只留在 data-key 里当存储键。
                string haystack = (name + " " + (package.Version ?? string.Empty)).ToLowerInvariant();

                sb.Append("<li class=\"item\" data-name=\"").Append(HtmlText(haystack)).Append("\">\r\n");
                sb.Append("  <input type=\"checkbox\" data-key=\"").Append(HtmlText(package.Key)).Append("\" aria-label=\"")
                  .Append(HtmlText(name)).Append("\">\r\n");
                sb.Append("  <div class=\"body\">\r\n    <span class=\"nm\">").Append(HtmlText(name)).Append("</span>");
                if (!string.IsNullOrEmpty(package.Version))
                {
                    sb.Append("<span class=\"ver\">v").Append(HtmlText(package.Version)).Append("</span>");
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    sb.Append("<span class=\"tag").Append(tag == "无独立安装包" ? string.Empty : " warn")
                      .Append("\">").Append(HtmlText(tag)).Append("</span>");
                }

                sb.Append("\r\n");
                if (!string.IsNullOrEmpty(note))
                {
                    sb.Append("    <p class=\"note\">").Append(HtmlText(note)).Append("</p>\r\n");
                }

                // 搜官网：用必应而不是 Google —— 国内可直连，且「软件名 + 官网」的命中率足够高
                sb.Append("  </div>\r\n  <a class=\"go\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"")
                  .Append(HtmlText("https://www.bing.com/search?q=" +
                                   Uri.EscapeDataString(name + " 官网 下载")))
                  .Append("\">搜官网</a>\r\n");
                sb.Append("</li>\r\n");
            }

            sb.Append("</ul>\r\n</section>\r\n");
        }

        /// <summary>
        /// 短标签（一眼看出这条为什么装不上），返回 <c>null</c> 表示不标。
        /// 与 <see cref="ExplainUninstallableNote"/> 的分工：标签只回答「哪一类」，
        /// 说明只回答「那我该怎么办」；默认情况（注册表登记）用标签就够了，不再多写一行。
        /// </summary>
        private static string UninstallableTag(Package package)
        {
            switch (package.IdKind)
            {
                case IdKind.Msix:
                    return "商店应用";

                case IdKind.Registry:
                    return "无独立安装包";

                default:
                    return "源不可用";
            }
        }

        /// <summary>
        /// 该项的补充说明，**只写与标签之外还需要解释的部分**，返回 <c>null</c> 表示不用写。
        /// <para>
        /// 绝大多数不可重装包都是「装的时候直接写进系统注册表、根本没有独立安装包」，
        /// 这件事在节说明里讲一次就够 —— 每项再重复一遍，12 项就有 11 行完全相同的文字，
        /// 反而把真正需要留意的那几项（商店应用、源下架）淹掉了。
        /// </para>
        /// <para>
        /// 包 ID 只在 <see cref="IdKind.Winget"/> 时给出：那种条目本来就是 winget 包，
        /// 源下架后按 ID 去 winget 仓库还能查到官方下载页；而 <c>ARP\...</c>/<c>msix\...</c>
        /// 是系统内部路径，几十个字符长且对人毫无意义，写上去只会把清单撑乱。
        /// </para>
        /// </summary>
        private static string ExplainUninstallableNote(Package package)
        {
            switch (package.IdKind)
            {
                case IdKind.Msix:
                    return "到「Microsoft Store → 库」里重新获取";

                case IdKind.Registry:
                    // 默认情况，节说明已覆盖，不再逐项重复
                    return null;

                default:
                    if (string.IsNullOrEmpty(package.PackageIdentifier))
                    {
                        return null;
                    }

                    return "可按 ID 搜官方下载页：" + package.PackageIdentifier;
            }
        }

        /// <summary>
        /// HTML 文本/属性转义。<c>&amp;</c> 必须第一个换（否则会把后面换出来的实体再转一遍），
        /// 单引号也要转 —— 属性值用双引号包着，但历史经验是漏一个就够坏一次页面。
        /// </summary>
        private static string HtmlText(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            return raw.Replace("&", "&amp;")
                      .Replace("<", "&lt;")
                      .Replace(">", "&gt;")
                      .Replace("\"", "&quot;")
                      .Replace("'", "&#39;");
        }

        /// <summary>
        /// 三产物落盘：apps.json → 恢复-&lt;stem&gt;.bat → 无法自动重装清单-&lt;stem&gt;.html。
        /// <para>
        /// 失败策略：清单（json）失败向上抛（那是主产物）；bat / html 失败**只记日志不中断**
        /// —— 少一个辅助文件也比整次导出白做要好。
        /// </para>
        /// <paramref name="log"/> 回调签名 <c>(level, text)</c>，level 取
        /// <see cref="LogInfo"/>/<see cref="LogFail"/>/<see cref="LogDone"/>/<see cref="LogSkip"/>。
        /// 标题行与文件清单由调用方（导出对话框）负责打，这里只报失败。
        /// </summary>
        public static ExportResult WriteAll(string destJsonPath, IList<Package> selected, IList<Package> allPackages,
                                            string wingetVersion, Action<string, string> log)
        {
            if (string.IsNullOrEmpty(destJsonPath))
            {
                throw new BadRequestException("导出路径为空。");
            }

            var selectedList = selected == null ? new List<Package>() : new List<Package>(selected);
            var all = allPackages == null ? new List<Package>() : new List<Package>(allPackages);

            var result = new ExportResult();
            result.JsonPath = destJsonPath;
            result.PackageCount = selectedList.Count;
            result.Breakdown = SourceBreakdown(selectedList);

            // 1) apps.json（主产物：失败必须让用户知道）
            string manifest = BuildManifest(selectedList, wingetVersion);
            AtomicFile.WriteTextAtomic(destJsonPath, manifest, AtomicFile.Utf8NoBom);
            result.Files.Add(destJsonPath);

            string stem = Path.GetFileNameWithoutExtension(destJsonPath);
            string directory = Path.GetDirectoryName(Path.GetFullPath(destJsonPath));
            if (string.IsNullOrEmpty(directory))
            {
                directory = ".";
            }

            // 2) 恢复脚本（GBK + CRLF）
            try
            {
                byte[] batBytes = BuildRestoreBat(destJsonPath, result.Breakdown.Total, result.Breakdown.MsStore);
                string batPath = Path.Combine(directory, "恢复-" + stem + ".bat");
                AtomicFile.WriteAllBytesAtomic(batPath, batBytes);
                result.Files.Add(batPath);
            }
            catch (Exception ex)
            {
                Report(log, LogFail, Strings.LogRestoreScriptFailedPrefix + ex.Message);
            }

            // 3) 无法自动重装清单（单文件 HTML，UTF-8 无 BOM；集合为空时跳过）
            var others = new List<Package>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && !all[i].Reinstallable)
                {
                    others.Add(all[i]);
                }
            }

            if (others.Count > 0)
            {
                try
                {
                    string html = BuildUninstallableHtml(others);
                    string htmlPath = Path.Combine(directory, "无法自动重装清单-" + stem + ".html");
                    AtomicFile.WriteTextAtomic(htmlPath, html, AtomicFile.Utf8NoBom);
                    result.Files.Add(htmlPath);
                    result.UninstallableCount = others.Count;
                }
                catch (Exception ex)
                {
                    Report(log, LogFail, Strings.LogUninstallListFailedPrefix + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// <see cref="AppLogger"/> 版本的重载（架构文档 §3.3 的签名），内部转成 level 回调。
        /// </summary>
        public static ExportResult WriteAll(string destJsonPath, IList<Package> selected, IList<Package> allPackages,
                                            string wingetVersion, AppLogger logger)
        {
            Action<string, string> sink = null;
            if (logger != null)
            {
                sink = (level, text) =>
                {
                    if (string.Equals(level, LogFail, StringComparison.Ordinal))
                    {
                        logger.Error(text);
                    }
                    else if (string.Equals(level, LogSkip, StringComparison.Ordinal))
                    {
                        logger.Warn(text);
                    }
                    else
                    {
                        logger.Info(text);
                    }
                };
            }

            return WriteAll(destJsonPath, selected, allPackages, wingetVersion, sink);
        }

        private static void Report(Action<string, string> log, string level, string text)
        {
            if (log == null)
            {
                return;
            }

            try
            {
                log(level, text);
            }
            catch (Exception)
            {
                // 日志回调异常不能打断导出
            }
        }

        private static bool IsKnownSource(string source)
        {
            return string.Equals(source, "winget", StringComparison.Ordinal)
                   || string.Equals(source, "msstore", StringComparison.Ordinal);
        }

        private static JsonValue BuildSourceDetails(string sourceName)
        {
            JsonValue details = JsonValue.NewObject();
            if (string.Equals(sourceName, "msstore", StringComparison.Ordinal))
            {
                details.Set("Argument", JsonValue.From("https://storeedgefd.dsx.mp.microsoft.com/v9.0"));
                details.Set("Identifier", JsonValue.From("StoreEdgeFD"));
                details.Set("Name", JsonValue.From("msstore"));
                details.Set("Type", JsonValue.From("Microsoft.Rest"));
                return details;
            }

            details.Set("Argument", JsonValue.From("https://cdn.winget.microsoft.com/cache"));
            details.Set("Identifier", JsonValue.From("Microsoft.Winget.Source_8wekyb3d8bbwe"));
            details.Set("Name", JsonValue.From("winget"));
            details.Set("Type", JsonValue.From("Microsoft.PreIndexed.Package"));
            return details;
        }

        /// <summary>按成品清单名渲染批处理文本（name 已做 basename 与 % 转义）。</summary>
        private static string RenderRestoreBat(string name, int count, int msStoreCount)
        {
            string msTip = string.Empty;
            if (msStoreCount > 0)
            {
                msTip = "echo   其中 " + msStoreCount.ToString(Inv) + " 个来自微软商店 msstore，"
                        + "可能要求先登录商店账号";
            }

            var lines = new List<string>
            {
                "@echo off",
                "chcp 936 >nul",
                "title 恢复软件清单",
                "cd /d \"%~dp0\"",
                string.Empty,
                "where winget >nul 2>nul",
                "if errorlevel 1 (",
                "  echo.",
                "  echo [x] 未找到 winget，请先安装「应用安装程序 / App Installer」。",
                "  echo     打开 Microsoft Store 搜索 App Installer 即可安装。",
                "  echo.",
                "  pause",
                "  exit /b 1",
                ")",
                string.Empty,
                "net session >nul 2>nul",
                "if errorlevel 1 (",
                "  echo.",
                "  echo [!] 当前不是管理员权限。部分机器级安装包会失败，",
                "  echo     建议右键本文件 - 以管理员身份运行。继续执行请按任意键。",
                "  pause >nul",
                ")",
                string.Empty,
                "echo.",
                "echo   Winget 软件管家 · 恢复脚本",
                "echo   ----------------------------------------",
                "echo   清单: \"" + name + "\"（" + count.ToString(Inv) + " 个软件包）",
                msTip.Length > 0 ? msTip : "echo.",
                "echo.",
                "echo   即将执行 winget import，请保持网络畅通。",
                "echo   源里已下架的包会被自动跳过，不影响其余安装。",
                "echo.",
                "pause",
                string.Empty,
                "winget import -i \"%~dp0" + name + "\" --accept-package-agreements "
                + "--accept-source-agreements --ignore-unavailable",
                string.Empty,
                "echo.",
                "echo   ----------------------------------------",
                "echo   执行结束。按任意键关闭。",
                "pause >nul"
            };

            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }

        private static int CompareUninstallable(Package left, Package right)
        {
            int byName = string.CompareOrdinal((left.Name ?? string.Empty).ToLowerInvariant(),
                                               (right.Name ?? string.Empty).ToLowerInvariant());
            if (byName != 0)
            {
                return byName;
            }

            return string.CompareOrdinal((left.PackageIdentifier ?? string.Empty).ToLowerInvariant(),
                                         (right.PackageIdentifier ?? string.Empty).ToLowerInvariant());
        }
    }
}
