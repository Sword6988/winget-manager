using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 解析 <c>winget list</c> 的表格输出（对应 <c>services/winget_table.py</c>，纯静态纯函数）。
///
/// 难点：表头是中文（名称/ID/版本/可用/源），且列宽按「显示宽度」对齐（一个汉字占 2 列）。
/// 因此不能用字符下标切片：先把表头里每个标签的起始位置换算成「显示列号」，
/// 再按显示宽度把每行映射回字符位置切片。
///
/// 表头识别失败时退化到 <see cref="ParseByGaps"/>：按「2 个以上空格」切分，从右往左认领字段。
/// </summary>
public static class TableParser
{
    /// <summary>分隔线的最小长度（正则 <c>^-{5,}$</c> 的等价实现）。</summary>
    private const int MinSeparatorLength = 5;

    /// <summary>表头标签 → 字段名。中英文变体都认（含全角/半角差异）。</summary>
    private static readonly Dictionary<string, HashSet<string>> HeaderLabels = new(StringComparer.Ordinal)
    {
        ["name"] = new HashSet<string>(StringComparer.Ordinal) { "名称", "name" },
        ["id"] = new HashSet<string>(StringComparer.Ordinal) { "id", "包 id", "标识符", "identifier" },
        ["version"] = new HashSet<string>(StringComparer.Ordinal) { "版本", "version" },
        ["available"] = new HashSet<string>(StringComparer.Ordinal) { "可用", "available" },
        ["source"] = new HashSet<string>(StringComparer.Ordinal) { "源", "source" },
    };

    /// <summary>表头列顺序（识别不出标签时按位置兜底赋名）。</summary>
    private static readonly string[] FieldOrder = { "name", "id", "version", "available", "source" };

    /// <summary>
    /// 噪声行前缀：winget 会在表格前后打印「已找到 N 个…」之类，一律丢掉。
    /// </summary>
    private static readonly string[] NoisePrefixes =
    {
        "已找到",
        "找不到",
        "以下",
        "the following",
        "no installed package",
        "没有找到",
        "已安装的程序包",
    };

    /// <summary>兜底解析时认得的源名。</summary>
    private static readonly HashSet<string> KnownSources = new(StringComparer.Ordinal) { "winget", "msstore" };

    private static readonly Regex VersionHeadPattern = new(@"^[0-9v]", RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析 <c>winget list</c> 的完整输出。
    /// </summary>
    /// <param name="text">已解码的 stdout。</param>
    /// <returns>原始行记录；ID 为空的行走不到结果里。</returns>
    public static IReadOnlyList<RawPackage> ParseListOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<RawPackage>();
        }

        var lines = SplitLines(text);
        var separatorIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsSeparator(lines[i].Trim()))
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex <= 0)
        {
            // 没有分隔线（或分隔线在第一行）：表头结构不可信，走空格兜底
            return ParseByGaps(lines);
        }

        var located = LocateColumns(lines[separatorIndex - 1]);
        if (located is null)
        {
            return ParseByGaps(lines);
        }

        var (offsets, fields) = located.Value;
        var packages = new List<RawPackage>();
        for (var i = separatorIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            var stripped = line.Trim();
            if (stripped.Length == 0 || IsSeparator(stripped))
            {
                continue;
            }

            if (StartsWithNoise(stripped))
            {
                continue;
            }

            var cells = SplitRow(line, offsets);
            var record = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < fields.Length; index++)
            {
                record[fields[index]] = index < cells.Count ? cells[index].Trim() : string.Empty;
            }

            var packageId = record.TryGetValue("id", out var idValue) ? idValue : string.Empty;
            if (packageId.Length == 0)
            {
                continue;
            }

            packages.Add(new RawPackage(
                Name: record.TryGetValue("name", out var nameValue) ? nameValue.Trim() : string.Empty,
                PackageId: packageId,
                Version: record.TryGetValue("version", out var versionValue) ? versionValue : string.Empty,
                Available: record.TryGetValue("available", out var availableValue) ? availableValue : string.Empty,
                Source: record.TryGetValue("source", out var sourceValue) ? sourceValue : string.Empty));
        }

        // 切列结果全空说明表头偏移猜错了，再用兜底解析试一次
        return packages.Count > 0 ? packages : ParseByGaps(lines);
    }

    /// <summary>按行拆文本：CRLF / CR / LF 都认（等价 Python <c>str.splitlines()</c> + <c>rstrip("\r")</c>）。</summary>
    /// <param name="text">原始文本。</param>
    /// <returns>行列表（不含行尾换行符）。</returns>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\r' && ch != '\n')
            {
                continue;
            }

            lines.Add(text[start..i]);
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>文本的显示宽度：汉字/全角字符算 2 列，组合字符算 0 列。</summary>
    /// <param name="text">任意文本。</param>
    /// <returns>显示列数。</returns>
    public static int DisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }

        return width;
    }

    /// <summary>单个字符的显示宽度（对应 Python <c>_char_width</c>）。</summary>
    private static int RuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
        {
            // 等价 unicodedata.combining(ch) != 0：组合字符不占列
            return 0;
        }

        return IsWideRune(rune.Value) ? 2 : 1;
    }

    /// <summary>
    /// East Asian Width = W/F 的近似判定（.NET 没有对应的字符属性 API）。
    /// 覆盖 CJK、假名、谚文、全角标点、以及常见 emoji 区段。
    /// </summary>
    private static bool IsWideRune(int codePoint)
    {
        return codePoint is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x20000 and <= 0x2FFFD
            or >= 0x30000 and <= 0x3FFFD;
    }

    /// <summary>这一行是不是 <c>^-----$</c> 形式的分隔线。</summary>
    private static bool IsSeparator(string stripped)
    {
        if (stripped.Length < MinSeparatorLength)
        {
            return false;
        }

        foreach (var ch in stripped)
        {
            if (ch != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithNoise(string stripped)
    {
        var lowered = stripped.ToLowerInvariant();
        foreach (var prefix in NoisePrefixes)
        {
            if (lowered.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 返回每个列的「显示列号」起点与字段名；识别不出 ID 列则返回 null。
    /// </summary>
    private static (int[] Offsets, string[] Fields)? LocateColumns(string header)
    {
        var trimmed = header.Trim();
        var tokens = FindTokens(trimmed);
        if (tokens.Count < 2)
        {
            return null;
        }

        var offsets = new int[tokens.Count];
        var fields = new string[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            var (position, label) = tokens[index];
            // Python 版从原始 header 前缀量宽度；这里量的是去掉首尾空白后的前缀，
            // 只在「表头行有前导空格」这种异常输入下才有差异（真实输出没有）。
            offsets[index] = DisplayWidth(trimmed[..position]);

            var lowered = label.Trim().ToLowerInvariant();
            var matched = MatchLabel(lowered);
            fields[index] = matched ?? (index < FieldOrder.Length
                ? FieldOrder[index]
                : "extra" + index.ToString(CultureInfo.InvariantCulture));
        }

        if (Array.IndexOf(fields, "id") < 0)
        {
            return null;
        }

        return (offsets, fields);
    }

    private static string? MatchLabel(string loweredLabel)
    {
        foreach (var pair in HeaderLabels)
        {
            if (pair.Value.Contains(loweredLabel))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>等价 <c>re.finditer(r"\S+", text)</c>：返回（字符下标, 词）序列。</summary>
    private static List<(int Position, string Label)> FindTokens(string text)
    {
        var tokens = new List<(int, string)>();
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            tokens.Add((start, text[start..index]));
        }

        return tokens;
    }

    /// <summary>
    /// 按显示列号切分一行，返回与 <paramref name="offsets"/> 等长的单元格列表。
    /// </summary>
    private static List<string> SplitRow(string line, int[] offsets)
    {
        var cells = new List<string>(offsets.Length);
        var start = 0;
        var width = 0;
        var boundary = 1; // offsets[0] 恒为 0，第一列不需要切
        var index = 0;

        while (index < line.Length)
        {
            if (boundary < offsets.Length && width >= offsets[boundary])
            {
                cells.Add(line[start..index]);
                start = index;
                boundary++;
                continue;
            }

            var status = Rune.DecodeFromUtf16(line.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                // 落单的代理项：按 1 列算，跳过 1 个 char，绝不越界
                consumed = 1;
            }

            width += RuneWidth(rune);
            index += consumed;
        }

        cells.Add(line[start..]);
        while (cells.Count < offsets.Length)
        {
            cells.Add(string.Empty);
        }

        return cells;
    }

    /// <summary>
    /// 兜底解析：表头识别失败时，按「2 个以上空格」切分并从右往左认领字段
    /// （源 → 可用 → 版本），剩下的左起两列就是名称与 ID。
    /// </summary>
    private static List<RawPackage> ParseByGaps(IReadOnlyList<string> lines)
    {
        var packages = new List<RawPackage>();
        var headerSeen = false;

        foreach (var line in lines)
        {
            var stripped = line.Trim();
            if (stripped.Length == 0 || IsSeparator(stripped))
            {
                continue;
            }

            if (StartsWithNoise(stripped))
            {
                continue;
            }

            var parts = SplitOnWideGaps(stripped);
            if (parts.Count < 2)
            {
                continue;
            }

            if (!headerSeen)
            {
                var first = parts[0].ToLowerInvariant();
                if (first is "名称" or "name")
                {
                    headerSeen = true;
                    continue;
                }
            }

            var source = string.Empty;
            if (KnownSources.Contains(parts[^1].ToLowerInvariant()))
            {
                source = parts[^1];
                parts.RemoveAt(parts.Count - 1);
            }

            var version = string.Empty;
            if (parts.Count >= 3 && VersionHeadPattern.IsMatch(parts[^1]))
            {
                version = parts[^1];
                parts.RemoveAt(parts.Count - 1);
            }

            if (parts.Count >= 4 && VersionHeadPattern.IsMatch(parts[^1]))
            {
                // 还有一列版本样式的尾巴：那是「可用」列，兜底解析不认领它（与 Python 版一致）
                parts.RemoveAt(parts.Count - 1);
            }

            if (parts.Count < 2)
            {
                continue;
            }

            packages.Add(new RawPackage(
                Name: parts[0],
                PackageId: parts[1],
                Version: version,
                Available: string.Empty,
                Source: source));
        }

        return packages;
    }

    /// <summary>按「2 个以上空白」切分并丢掉空片段（等价 <c>re.split(r"\s{2,}")</c> + strip）。</summary>
    private static List<string> SplitOnWideGaps(string text)
    {
        var parts = new List<string>();
        var index = 0;
        var start = 0;
        while (index < text.Length)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var runStart = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index - runStart >= 2)
            {
                var chunk = text[start..runStart].Trim();
                if (chunk.Length > 0)
                {
                    parts.Add(chunk);
                }

                start = index;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            parts.Add(tail);
        }

        return parts;
    }
}
