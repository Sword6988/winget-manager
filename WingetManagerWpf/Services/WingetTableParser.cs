using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Services
{
    /// <summary>
    /// 解析 <c>winget list</c> 的表格输出（逐行移植 <c>_ref/services/winget_table.py</c>）。
    /// <para>
    /// 难点：表头是中文（名称/ID/版本/可用/源），列宽按**显示宽度**对齐（汉字占 2 列）。
    /// 因此不能按字符下标切列：先把表头标签的起始位置换算成「显示列号」，
    /// 再把每行按显示宽度映射回字符位置切片。
    /// </para>
    /// <para>
    /// 表头定位失败（英文/未知语言/列被截断）时走兜底：按「2 个以上空格」切分，
    /// 从右往左认领 源 → 版本 → 可用列。兜底比精确切列脆弱，但比「一条都解析不出来」好。
    /// </para>
    /// </summary>
    public static class WingetTableParser
    {
        private static readonly Regex SeparatorRegex = new Regex(@"^-{5,}$", RegexOptions.CultureInvariant);
        private static readonly Regex TokenRegex = new Regex(@"\S+", RegexOptions.CultureInvariant);
        private static readonly Regex GapSplitRegex = new Regex(@"\s{2,}", RegexOptions.CultureInvariant);

        /// <summary>表头标签 → 字段名。键序就是字段优先级（name→id→…）。</summary>
        private static readonly List<KeyValuePair<string, string[]>> HeaderLabels = new List<KeyValuePair<string, string[]>>
        {
            new KeyValuePair<string, string[]>("name", new[] { "名称", "name" }),
            new KeyValuePair<string, string[]>("id", new[] { "id", "包 id", "标识符", "identifier" }),
            new KeyValuePair<string, string[]>("version", new[] { "版本", "version" }),
            new KeyValuePair<string, string[]>("available", new[] { "可用", "available" }),
            new KeyValuePair<string, string[]>("source", new[] { "源", "source" })
        };

        /// <summary>表头标签认不出来时的兜底字段顺序。</summary>
        private static readonly string[] FieldOrder = { "name", "id", "version", "available", "source" };

        /// <summary>噪音行前缀（提示语，不是数据）；比较时统一小写 + StartsWith。</summary>
        private static readonly string[] NoisePrefixes =
        {
            "已找到",
            "找不到",
            "以下",
            "the following",
            "no installed package",
            "没有找到",
            "已安装的程序包"
        };

        /// <summary>解析 <c>winget list</c> 输出；解析不出任何数据时返回空列表。</summary>
        public static IList<RawPackage> Parse(string listOutput)
        {
            List<string> lines = SplitLines(listOutput);
            int separatorIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (SeparatorRegex.IsMatch(lines[i].Trim()))
                {
                    separatorIndex = i;
                    break;
                }
            }

            // 没有分隔线（表头缺失 / 空输出）→ 直接走兜底
            if (separatorIndex <= 0)
            {
                return ParseByGaps(lines);
            }

            int[] offsets;
            string[] fields;
            if (!TryLocateColumns(lines[separatorIndex - 1], out offsets, out fields))
            {
                return ParseByGaps(lines);
            }

            var packages = new List<RawPackage>();
            for (int i = separatorIndex + 1; i < lines.Count; i++)
            {
                string line = lines[i];
                string stripped = line.Trim();
                if (stripped.Length == 0 || SeparatorRegex.IsMatch(stripped))
                {
                    continue;
                }

                if (IsNoise(stripped))
                {
                    continue;
                }

                string[] cells = SplitRow(line, offsets);
                var record = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int c = 0; c < fields.Length; c++)
                {
                    if (fields[c] == null)
                    {
                        continue;
                    }

                    // 同名标签重复出现时后者覆盖前者（与 Python 的 dict 推断一致）
                    record[fields[c]] = c < cells.Length ? cells[c].Trim() : string.Empty;
                }

                string packageId = GetCell(record, "id");
                if (packageId.Length == 0)
                {
                    // 同一行解析出的 ID 为空说明这行不是有效数据（或列错位），丢弃
                    continue;
                }

                packages.Add(new RawPackage(
                    GetCell(record, "name").Trim(),
                    packageId,
                    GetCell(record, "version"),
                    GetCell(record, "available"),
                    GetCell(record, "source")));
            }

            return packages.Count > 0 ? packages : ParseByGaps(lines);
        }

        /// <summary>定位表头：返回每个列的显示列号起点与字段名。</summary>
        private static bool TryLocateColumns(string headerLine, out int[] offsets, out string[] fields)
        {
            offsets = new int[0];
            fields = new string[0];

            if (headerLine == null)
            {
                return false;
            }

            // 只在去空白后的表头上算位置：真实输出里表头没有前导空格，
            // 用同一份文本计算可避免「标签位置」与「偏移基准」两套坐标系
            string header = headerLine.Trim();
            MatchCollection matches = TokenRegex.Matches(header);
            if (matches.Count < 2)
            {
                return false;
            }

            offsets = new int[matches.Count];
            fields = new string[matches.Count];

            for (int i = 0; i < matches.Count; i++)
            {
                offsets[i] = EncodedText.DisplayWidth(header.Substring(0, matches[i].Index));
                string label = matches[i].Value.Trim().ToLowerInvariant();
                string matched = MatchField(label);
                if (matched == null)
                {
                    matched = i < FieldOrder.Length ? FieldOrder[i] : "extra" + i;
                }

                fields[i] = matched;
            }

            return Array.IndexOf(fields, "id") >= 0;
        }

        private static string MatchField(string label)
        {
            for (int i = 0; i < HeaderLabels.Count; i++)
            {
                string[] names = HeaderLabels[i].Value;
                for (int j = 0; j < names.Length; j++)
                {
                    if (string.Equals(names[j], label, StringComparison.Ordinal))
                    {
                        return HeaderLabels[i].Key;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 按显示列号切分一行：累加显示宽度，跨过某个列起点就切一刀。
        /// 注意 offset[0] 恒为 0，第一列不需要切。
        /// </summary>
        private static string[] SplitRow(string line, int[] offsets)
        {
            var cells = new List<string>(offsets.Length);
            int start = 0;
            int width = 0;
            int index = 0;
            int boundary = 1;
            int length = line.Length;

            while (index < length)
            {
                if (boundary < offsets.Length && width >= offsets[boundary])
                {
                    cells.Add(line.Substring(start, index - start));
                    start = index;
                    boundary++;
                    continue;
                }

                char ch = line[index];
                if (char.IsHighSurrogate(ch) && index + 1 < length && char.IsLowSurrogate(line[index + 1]))
                {
                    // 代理对（emoji）：整体占 2 列，一起前进，避免半个字符被切开
                    width += 2;
                    index += 2;
                    continue;
                }

                width += EncodedText.CharWidth(ch);
                index++;
            }

            cells.Add(line.Substring(start));
            while (cells.Count < offsets.Length)
            {
                cells.Add(string.Empty);
            }

            return cells.ToArray();
        }

        /// <summary>
        /// 兜底解析：表头识别失败时按「2 个以上空格」切分，从右往左认领字段。
        /// 已知局限：名称含连续空格时会被切碎（真实输出里名称不会这样）。
        /// </summary>
        private static IList<RawPackage> ParseByGaps(List<string> lines)
        {
            var packages = new List<RawPackage>();
            bool headerSeen = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string stripped = (lines[i] ?? string.Empty).Trim();
                if (stripped.Length == 0 || SeparatorRegex.IsMatch(stripped))
                {
                    continue;
                }

                if (IsNoise(stripped))
                {
                    continue;
                }

                var parts = new List<string>();
                string[] chunks = GapSplitRegex.Split(stripped);
                for (int c = 0; c < chunks.Length; c++)
                {
                    string part = chunks[c].Trim();
                    if (part.Length > 0)
                    {
                        parts.Add(part);
                    }
                }

                if (parts.Count < 2)
                {
                    continue;
                }

                if (!headerSeen)
                {
                    string first = parts[0].ToLowerInvariant();
                    if (first == "名称" || first == "name")
                    {
                        headerSeen = true;
                        continue;
                    }
                }

                string source = string.Empty;
                int last = parts.Count - 1;
                string lastLower = parts[last].ToLowerInvariant();
                if (lastLower == "winget" || lastLower == "msstore")
                {
                    source = parts[last];
                    parts.RemoveAt(last);
                }

                string version = string.Empty;
                if (parts.Count >= 3 && StartsLikeVersion(parts[parts.Count - 1]))
                {
                    version = parts[parts.Count - 1];
                    parts.RemoveAt(parts.Count - 1);
                }

                if (parts.Count >= 4 && StartsLikeVersion(parts[parts.Count - 1]))
                {
                    // 「可用」列
                    parts.RemoveAt(parts.Count - 1);
                }

                if (parts.Count < 2)
                {
                    continue;
                }

                packages.Add(new RawPackage(parts[0], parts[1], version, string.Empty, source));
            }

            return packages;
        }

        private static bool StartsLikeVersion(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            char ch = text[0];
            // 与原版正则 ^[0-9v] 一致：只认小写 v 与数字
            return (ch >= '0' && ch <= '9') || ch == 'v';
        }

        private static bool IsNoise(string stripped)
        {
            string lowered = stripped.ToLowerInvariant();
            for (int i = 0; i < NoisePrefixes.Length; i++)
            {
                if (lowered.StartsWith(NoisePrefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetCell(Dictionary<string, string> record, string field)
        {
            string value;
            return record.TryGetValue(field, out value) && value != null ? value : string.Empty;
        }

        /// <summary>按 \r\n / \n / \r 切行（原版用 splitlines，注意别把 \r 留在行尾）。</summary>
        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return lines;
            }

            int start = 0;
            int i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '\r')
                {
                    lines.Add(text.Substring(start, i - start));
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    i++;
                    start = i;
                    continue;
                }

                if (ch == '\n')
                {
                    lines.Add(text.Substring(start, i - start));
                    i++;
                    start = i;
                    continue;
                }

                i++;
            }

            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
            }

            return lines;
        }
    }
}
