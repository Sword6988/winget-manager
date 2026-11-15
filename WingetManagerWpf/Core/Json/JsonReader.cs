using System;
using System.Globalization;
using System.Text;

namespace WingetManagerWpf.Core.Json
{
    /// <summary>
    /// 轻量 JSON 解析器。策略：
    /// <list type="bullet">
    /// <item>容忍 UTF-8 BOM（原版 <c>read_text(encoding="utf-8")</c> 会把 BOM 留在文本里，
    /// 老文件确实可能带 BOM）；</item>
    /// <item>**不容忍**注释与尾逗号 —— 沉默接受非法格式会让「文件损坏」变成「数据莫名丢失」；</item>
    /// <item>解析失败抛 <see cref="BadRequestException"/>，由调用方决定回退（勾选/缓存都退回空）；
    /// 需要「不抛」的调用点用 <see cref="TryParse"/>。</item>
    /// </list>
    /// </summary>
    public static class JsonReader
    {
        /// <summary>
        /// 允许的最大嵌套层数（对象 + 数组的总深度）。真实数据文件最深也就 3~4 层
        /// （<c>{"entries":{"id":{"dep":true}}}</c>），64 层留足了余量。
        /// <para>
        /// 为什么必须有上限：这是递归下降解析器，每层嵌套都是一次真实的方法调用。
        /// 一个 10 万层 <c>[</c> 的文件（损坏、被别的程序写坏、或从不可信来源拷来）
        /// 会把线程栈打爆，而 <see cref="StackOverflowException"/> 在 .NET 里
        /// **不可捕获**——<c>catch</c> 拦不住、<c>AppDomain.UnhandledException</c> 也
        /// 收不到，进程直接消失，用户看到的是「双击没反应」，日志里一个字都没有。
        /// 有了上限，这类文件会走正常的 <see cref="BadRequestException"/> 分支，
        /// 上层按「文件损坏 → 回退空数据」处理并记 Warn。
        /// </para>
        /// </summary>
        public const int MaxDepth = 64;

        /// <summary>解析文本；失败抛 <see cref="BadRequestException"/>。</summary>
        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new BadRequestException("JSON 文本为 null。");
            }

            // BOM 直接剥掉：留着会让第一个键名变成 "\uFEFFschema"，导致 schema 校验永远失败
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            var parser = new Parser(text);
            JsonValue value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw parser.Error("根值之后还有多余内容");
            }

            return value;
        }

        /// <summary>解析文本；失败返回 false 且 <paramref name="value"/> 为 null。</summary>
        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (AppException)
            {
                value = null;
                return false;
            }
            catch (ArgumentException)
            {
                value = null;
                return false;
            }
        }

        /// <summary>递归下降解析器（显式游标，避免正则回溯带来的性能与错误定位问题）。</summary>
        private sealed class Parser
        {
            private readonly string _text;
            private int _index;
            private int _depth;

            public Parser(string text)
            {
                _text = text;
                _index = 0;
            }

            public bool AtEnd
            {
                get { return _index >= _text.Length; }
            }

            public JsonValue ParseValue()
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("内容意外结束");
                }

                char ch = _text[_index];

                // 只有「进入容器」才加深一层：深度计数放在这里而不是 ParseObject 内部，
                // 是为了让 try/finally 只包住一次递归调用，而不是把整个函数体重新缩进
                if (ch == '{' || ch == '[')
                {
                    if (_depth >= MaxDepth)
                    {
                        throw Error("嵌套层级超过 " + MaxDepth.ToString(CultureInfo.InvariantCulture)
                                    + " 层，文件可能已损坏");
                    }

                    _depth++;
                    try
                    {
                        return ch == '{' ? ParseObject() : ParseArray();
                    }
                    finally
                    {
                        // 异常路径也要还原：同一个 Parser 实例不会复用，但计数错乱会让
                        // 后续（例如 TryParse 之后的二次解析）误判深度
                        _depth--;
                    }
                }

                switch (ch)
                {
                    case '"':
                        return JsonValue.From(ParseString());
                    case 't':
                        ExpectLiteral("true");
                        return JsonValue.From(true);
                    case 'f':
                        ExpectLiteral("false");
                        return JsonValue.From(false);
                    case 'n':
                        ExpectLiteral("null");
                        return JsonValue.Null;
                    default:
                        if (ch == '-' || (ch >= '0' && ch <= '9'))
                        {
                            return ParseNumber();
                        }

                        throw Error("无法识别的字符 '" + ch + "'");
                }
            }

            public void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char ch = _text[_index];
                    if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                    {
                        _index++;
                        continue;
                    }

                    break;
                }
            }

            public BadRequestException Error(string message)
            {
                int line = 1;
                int column = 1;
                int limit = Math.Min(_index, _text.Length);
                for (int i = 0; i < limit; i++)
                {
                    if (_text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }

                return new BadRequestException(
                    string.Format(CultureInfo.InvariantCulture,
                        "JSON 解析失败（第 {0} 行第 {1} 列）：{2}", line, column, message),
                    "数据文件格式不正确，已忽略并回退到默认状态。");
            }

            private JsonValue ParseObject()
            {
                _index++; // '{'
                JsonValue obj = JsonValue.NewObject();
                SkipWhitespace();
                if (!AtEnd && _text[_index] == '}')
                {
                    _index++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("对象没有闭合");
                    }

                    if (_text[_index] != '"')
                    {
                        throw Error("对象的键必须是字符串");
                    }

                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _text[_index] != ':')
                    {
                        throw Error("键之后缺少冒号");
                    }

                    _index++; // ':'
                    JsonValue value = ParseValue();
                    obj.Set(key, value);
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("对象没有闭合");
                    }

                    char ch = _text[_index];
                    if (ch == ',')
                    {
                        _index++;
                        continue;
                    }

                    if (ch == '}')
                    {
                        _index++;
                        return obj;
                    }

                    throw Error("对象成员之间只允许逗号");
                }
            }

            private JsonValue ParseArray()
            {
                _index++; // '['
                JsonValue array = JsonValue.NewArray();
                SkipWhitespace();
                if (!AtEnd && _text[_index] == ']')
                {
                    _index++;
                    return array;
                }

                while (true)
                {
                    JsonValue item = ParseValue();
                    array.Add(item);
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("数组没有闭合");
                    }

                    char ch = _text[_index];
                    if (ch == ',')
                    {
                        _index++;
                        continue;
                    }

                    if (ch == ']')
                    {
                        _index++;
                        return array;
                    }

                    throw Error("数组元素之间只允许逗号");
                }
            }

            private string ParseString()
            {
                _index++; // '"'
                var sb = new StringBuilder(32);
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("字符串没有闭合");
                    }

                    char ch = _text[_index];
                    if (ch == '"')
                    {
                        _index++;
                        return sb.ToString();
                    }

                    if (ch == '\\')
                    {
                        _index++;
                        if (AtEnd)
                        {
                            throw Error("转义符后内容意外结束");
                        }

                        char esc = _text[_index];
                        _index++;
                        switch (esc)
                        {
                            case '"':
                                sb.Append('"');
                                break;
                            case '\\':
                                sb.Append('\\');
                                break;
                            case '/':
                                sb.Append('/');
                                break;
                            case 'b':
                                sb.Append('\b');
                                break;
                            case 'f':
                                sb.Append('\f');
                                break;
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case 'u':
                                sb.Append(ParseUnicodeEscape());
                                break;
                            default:
                                throw Error("不支持的转义 \\" + esc);
                        }

                        continue;
                    }

                    if (ch < 0x20)
                    {
                        throw Error("字符串里出现了未转义的控制字符");
                    }

                    sb.Append(ch);
                    _index++;
                }
            }

            private char ParseUnicodeEscape()
            {
                if (_index + 4 > _text.Length)
                {
                    throw Error("\\u 之后需要 4 位十六进制数字");
                }

                int code = 0;
                for (int i = 0; i < 4; i++)
                {
                    char ch = _text[_index + i];
                    int digit;
                    if (ch >= '0' && ch <= '9')
                    {
                        digit = ch - '0';
                    }
                    else if (ch >= 'a' && ch <= 'f')
                    {
                        digit = ch - 'a' + 10;
                    }
                    else if (ch >= 'A' && ch <= 'F')
                    {
                        digit = ch - 'A' + 10;
                    }
                    else
                    {
                        throw Error("\\u 之后需要 4 位十六进制数字");
                    }

                    code = (code * 16) + digit;
                }

                _index += 4;
                return (char)code;
            }

            private JsonValue ParseNumber()
            {
                int start = _index;
                bool isFraction = false;

                if (!AtEnd && (_text[_index] == '-' || _text[_index] == '+'))
                {
                    _index++;
                }

                while (!AtEnd)
                {
                    char ch = _text[_index];
                    if (ch >= '0' && ch <= '9')
                    {
                        _index++;
                        continue;
                    }

                    if (ch == '.' || ch == 'e' || ch == 'E' || ch == '+' || ch == '-')
                    {
                        isFraction = isFraction || ch == '.' || ch == 'e' || ch == 'E';
                        _index++;
                        continue;
                    }

                    break;
                }

                string raw = _text.Substring(start, _index - start);
                if (raw.Length == 0)
                {
                    throw Error("数字格式不正确");
                }

                if (!isFraction)
                {
                    long asLong;
                    if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out asLong))
                    {
                        return JsonValue.From(asLong);
                    }
                }

                double asDouble;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out asDouble))
                {
                    return JsonValue.From(asDouble);
                }

                throw Error("数字格式不正确：" + raw);
            }

            private void ExpectLiteral(string literal)
            {
                if (_index + literal.Length > _text.Length
                    || string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                {
                    throw Error("期望字面量 " + literal);
                }

                _index += literal.Length;
            }
        }
    }
}
