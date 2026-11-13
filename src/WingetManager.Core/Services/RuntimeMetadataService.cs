using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WingetManager.Services;

/// <summary>
/// 用 winget 目录的元数据自动判定「运行库 vs 软件」（对应 <c>metadata.py</c> 前半）。
///
/// 关键词只做初判：扫描完成后对判定存疑的包逐个 <c>winget show</c>，从输出里抠出
/// 描述/标记/安装器 URL，找运行库信号词。三态语义：
/// <list type="bullet">
/// <item>命中信号词 → <c>true</c>（含命中的词）；</item>
/// <item>解析出了描述/标记但没命中 → <c>false</c>；</item>
/// <item>输出为空、或解析不出描述/标记（包不在目录等）→ <c>null</c>「未知」，
///       绝不当成 <c>false</c> 用。</item>
/// </list>
/// </summary>
public static partial class RuntimeMetadataService
{
    /// <summary>描述/标记/安装器 URL 里出现这些词（大小写不敏感）即判为运行库。</summary>
    public static readonly IReadOnlyList<string> SignalWords = new[]
    {
        "runtime", "redistributable", "redist", "运行库", "运行时", "required to run",
    };

    /// <summary>
    /// winget show 的段落标签：中文/英文、半角/全角冒号都认。
    /// </summary>
    [GeneratedRegex(@"^(描述|Description|标记|标签|Tags|安装程序 ?URL|Installer URL)\s*[:：]\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    /// <summary>安装器 URL 抽取。</summary>
    [GeneratedRegex(@"https?://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    /// <summary>
    /// 从 show 输出里抠出（描述行, 标记词, 安装器 URL）。
    ///
    /// 标记是「标签行之后的缩进列表」，直到空行或下一个 <c>xxx:</c> 标签行为止；描述同理
    /// 支持折行。解析不出就返回空列表，由调用方判 <c>null</c>。
    /// </summary>
    /// <param name="text">winget show 的原始输出。</param>
    /// <returns>三段抽取结果。</returns>
    public static (IReadOnlyList<string> Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Urls)
        ExtractSections(string? text)
    {
        var description = new List<string>();
        var tags = new List<string>();
        var urls = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return (description, tags, urls);
        }

        string? mode = null; // null / "desc" / "tags"
        foreach (var raw in TableParser.SplitLines(text))
        {
            var line = raw.TrimEnd();
            var stripped = line.Trim();
            if (stripped.Length == 0)
            {
                mode = null;
                continue;
            }

            var match = LabelRegex().Match(stripped);
            if (match.Success)
            {
                var label = match.Groups[1].Value.ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
                var rest = match.Groups[2].Value.Trim();

                if (label is "描述" or "description")
                {
                    mode = "desc";
                    if (rest.Length > 0)
                    {
                        description.Add(rest);
                    }
                }
                else if (label is "标记" or "标签" or "tags")
                {
                    mode = "tags";
                    if (rest.Length > 0)
                    {
                        // 有的版本把标记写在同一行
                        AddWords(tags, rest);
                    }
                }
                else
                {
                    // 安装程序 URL / Installer URL
                    mode = null;
                    var urlMatch = UrlRegex().Match(rest);
                    if (urlMatch.Success)
                    {
                        urls.Add(urlMatch.Value);
                    }
                }

                continue;
            }

            if (mode is not null && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                // 缩进行：当前段落的延续
                if (mode == "desc")
                {
                    description.Add(stripped);
                }
                else
                {
                    AddWords(tags, stripped);
                }

                continue;
            }

            mode = null; // 顶格的非标签行：段落结束
        }

        return (description, tags, urls);
    }

    /// <summary>在（已拼接的）文本里找第一个命中的运行库信号词；没有则 null。</summary>
    /// <param name="text">待匹配文本。</param>
    /// <returns>命中的信号词或 null。</returns>
    public static string? MatchedSignal(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lowered = text.ToLowerInvariant();
        foreach (var word in SignalWords)
        {
            if (lowered.Contains(word, StringComparison.Ordinal))
            {
                return word;
            }
        }

        return null;
    }

    /// <summary>
    /// 综合判定：返回（是否运行库, 命中的信号词）。详见类型注释里的三态语义。
    /// </summary>
    /// <param name="text">winget show 的原始输出。</param>
    /// <returns>（True/False/null, 信号词）。</returns>
    public static (bool? IsDependency, string? Signal) AnalyzeRuntimeSignal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var (description, tags, urls) = ExtractSections(text);
        if (description.Count == 0 && tags.Count == 0)
        {
            return (null, null);
        }

        var haystack = string.Join("\n", description) + "\n" + string.Join("\n", tags);
        if (urls.Count > 0)
        {
            haystack += "\n" + string.Join("\n", urls);
        }

        var signal = MatchedSignal(haystack);
        return (signal is not null, signal);
    }

    /// <summary>是否运行库：True / False / null（未知）。详见 <see cref="AnalyzeRuntimeSignal"/>。</summary>
    /// <param name="text">winget show 的原始输出。</param>
    /// <returns>三态判定结果。</returns>
    public static bool? ParseRuntimeSignal(string? text) => AnalyzeRuntimeSignal(text).IsDependency;

    private static void AddWords(List<string> target, string text)
    {
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            target.Add(word);
        }
    }
}
