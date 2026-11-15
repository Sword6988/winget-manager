using System;
using System.Globalization;
using System.Text;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 编码与「显示宽度」工具。
    /// <para>
    /// 解码顺序 <c>utf-8-sig → utf-8（严格）→ 本地编码(936) → UTF-8 替换</c> 不能换：
    /// BOM 必须先剥，否则第一行表头会变成 "\uFEFF名称"，表头标签匹配全失败；
    /// 严格 UTF-8 必须先于 GBK，否则 UTF-8 的字节流会被 936 解成乱码而不报错。
    /// </para>
    /// <para>
    /// 显示宽度是 <c>winget list</c> 切列的基础：汉字/全角占 2 列、组合字符占 0 列、
    /// emoji（代理对）整体占 2 列（**不能按 char 累加**，否则每行都会错位）。
    /// .NET Framework 没有 East Asian Width API，故自带区段表。
    /// </para>
    /// </summary>
    public static class EncodedText
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);
        private static readonly Encoding Gbk;

        static EncodedText()
        {
            try
            {
                // 936 = GBK。Windows 上一定有；万一取不到就留 null，解码时跳过这一档
                Gbk = Encoding.GetEncoding(936);
            }
            catch (Exception)
            {
                Gbk = null;
            }
        }

        /// <summary>把子进程的原始字节解成文本（见类注释里的顺序说明）。</summary>
        public static string Decode(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
            {
                return string.Empty;
            }

            int offset = 0;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            {
                offset = 3;
            }

            // ① utf-8-sig / utf-8（严格）
            try
            {
                return StrictUtf8.GetString(raw, offset, raw.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                // 不是合法 UTF-8，继续尝试本地编码
            }

            string text;

            // ② 本机首选 ANSI 代码页（中文 Windows 即 936）
            //    坑：系统若开了「Beta: 使用 UTF-8 提供全球语言支持」，默认代码页就是 65001，
            //    此时 GetString **不会抛异常**，只会把 GBK 字节解成一串 U+FFFD（替换字符）。
            //    这种「静默乱码」比异常更糟 —— 它会提前 return，把下面真正的 GBK 兜底整段跳过，
            //    于是 winget 的中文输出在界面上变成一片「口口口」。所以必须检查替换字符：
            //    解出乱码就当作这一档失败，继续往下走。
            try
            {
                text = Encoding.Default.GetString(raw);
                if (!string.IsNullOrEmpty(text) && !ContainsReplacementChar(text))
                {
                    return text;
                }
            }
            catch (Exception)
            {
                // 忽略，继续下一档
            }

            // ③ 显式 GBK（某些系统的默认代码页被改成 65001 时兜底）
            if (Gbk != null)
            {
                try
                {
                    return Gbk.GetString(raw);
                }
                catch (Exception)
                {
                    // 继续下一档
                }
            }

            // ④ 最后容错：UTF-8 + 替换字符（信息有损，但界面能继续跑）
            return LenientUtf8.GetString(raw, offset, raw.Length - offset);
        }

        /// <summary>
        /// 文本里是否含 Unicode 替换字符（U+FFFD）。有就说明这一档解码已经失真 ——
        /// 用错编码去解一段字节时，.NET 的默认解码器**不抛异常**而是逐字节换成 U+FFFD，
        /// 于是「解码成功」和「解得一塌糊涂」在返回值上看起来一模一样。
        /// </summary>
        public static bool ContainsReplacementChar(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf('\uFFFD') >= 0;
        }

        /// <summary>把 \r\n / \r 统一成 \n（仅用于内部比较/解析，不用于写盘）。</summary>
        public static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>单个 UTF-16 码元的显示宽度：组合字符 0，Wide/Fullwidth 2，其余 1。</summary>
        public static int CharWidth(char ch)
        {
            if (ch == '\0')
            {
                return 0;
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            // 与 Python unicodedata.combining() 对齐：把组合类标记视作 0 宽
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
            {
                return 0;
            }

            return IsWide(ch) ? 2 : 1;
        }

        /// <summary>
        /// 字符串显示宽度。代理对（emoji / 增补平面字符）整体按 2 计，
        /// 与终端/控制台的渲染一致；这里也是「行按显示列切分」的唯一入口。
        /// </summary>
        public static int DisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int width = 0;
            int i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // 增补平面字符（绝大多数是 emoji）：宽度 2，且必须成对跳过
                    width += 2;
                    i += 2;
                    continue;
                }

                width += CharWidth(ch);
                i++;
            }

            return width;
        }

        /// <summary>
        /// East Asian Width 为 Wide(2)/Fullwidth(2) 的区段（Unicode 12 常用区间）。
        /// 判定顺序无关，区间互不重叠。
        /// </summary>
        private static bool IsWide(char ch)
        {
            if (ch < 0x1100)
            {
                return false;
            }

            return (ch <= 0x115F)                                  // 谚文字母
                || (ch >= 0x2E80 && ch <= 0x303E)                  // 中日韩部首、标点
                || (ch >= 0x3041 && ch <= 0x33FF)                  // 假名、注音、CJK 兼容
                || (ch >= 0x3400 && ch <= 0x4DBF)                  // CJK 扩展 A
                || (ch >= 0x4E00 && ch <= 0x9FFF)                  // CJK 基本区
                || (ch >= 0xA000 && ch <= 0xA4CF)                  // 彝文
                || (ch >= 0xA960 && ch <= 0xA97F)                  // 谚文字母扩展 A
                || (ch >= 0xAC00 && ch <= 0xD7A3)                  // 谚文音节
                || (ch >= 0xF900 && ch <= 0xFAFF)                  // CJK 兼容表意
                || (ch >= 0xFE10 && ch <= 0xFE19)                  // 竖排标点
                || (ch >= 0xFE30 && ch <= 0xFE6F)                  // CJK 兼容形式
                || (ch >= 0xFF00 && ch <= 0xFF60)                  // 全角 ASCII / 标点
                || (ch >= 0xFFE0 && ch <= 0xFFE6);                 // 全角符号
        }
    }
}
