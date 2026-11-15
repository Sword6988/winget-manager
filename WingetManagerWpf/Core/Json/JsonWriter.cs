using System;
using System.Globalization;
using System.Text;

namespace WingetManagerWpf.Core.Json
{
    /// <summary>
    /// JSON 文本输出：2 空格缩进、`\n` 换行、非 ASCII **原样输出**（等价 Python 的
    /// <c>ensure_ascii=False</c>），整数不带多余的 <c>.0</c>。
    /// 缩进与换行必须是确定值，否则「原子写 + 与原版互通」都没法验证。
    /// </summary>
    public static class JsonWriter
    {
        private const string IndentUnit = "  ";

        /// <summary>序列化（不带尾随换行；需要尾随换行由 <see cref="AtomicFile.WriteJsonAtomic"/> 负责）。</summary>
        public static string Serialize(JsonValue value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value ?? JsonValue.Null, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue value, int depth)
        {
            switch (value.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    return;
                case JsonKind.Bool:
                    sb.Append(value.TryGetBool(out bool b) && b ? "true" : "false");
                    return;
                case JsonKind.Number:
                    WriteNumber(sb, value);
                    return;
                case JsonKind.String:
                    WriteString(sb, value.AsString());
                    return;
                case JsonKind.Array:
                    WriteArray(sb, value, depth);
                    return;
                case JsonKind.Object:
                    WriteObject(sb, value, depth);
                    return;
                default:
                    sb.Append("null");
                    return;
            }
        }

        private static void WriteNumber(StringBuilder sb, JsonValue value)
        {
            if (value.IsIntegral)
            {
                sb.Append(value.AsLong().ToString(CultureInfo.InvariantCulture));
                return;
            }

            double number = value.AsNumber();
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                // JSON 不支持 NaN/Infinity；写成 null 比写出非法文本更安全
                sb.Append("null");
                return;
            }

            // "R" 保证往返可读，且不会产生 1.0 这种尾数
            sb.Append(number.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteArray(StringBuilder sb, JsonValue value, int depth)
        {
            if (value.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append('[');
            bool first = true;
            foreach (JsonValue item in value.Items)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('\n');
                AppendIndent(sb, depth + 1);
                WriteValue(sb, item, depth + 1);
            }

            sb.Append('\n');
            AppendIndent(sb, depth);
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, JsonValue value, int depth)
        {
            if (value.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            bool first = true;
            foreach (var member in value.Members)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append('\n');
                AppendIndent(sb, depth + 1);
                WriteString(sb, member.Key);
                sb.Append(": ");
                WriteValue(sb, member.Value, depth + 1);
            }

            sb.Append('\n');
            AppendIndent(sb, depth);
            sb.Append('}');
        }

        private static void AppendIndent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++)
            {
                sb.Append(IndentUnit);
            }
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                switch (ch)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 0x20)
                        {
                            // 其余控制字符一律 \u00XX（JSON 不允许裸控制字符）
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (ch == '\u2028' || ch == '\u2029')
                        {
                            // 行/段分隔符在 JS 里会被当换行，稳妥起见也转义
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // 关键：非 ASCII（中文）原样输出，等价 ensure_ascii=False
                            sb.Append(ch);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }
}
