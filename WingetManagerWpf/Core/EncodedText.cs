using System;
using System.Globalization;
using System.Text;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 编码与「显示宽度」工具。
    /// <para>
    /// 解码顺序 <c>utf-8-sig → utf-8（严格）→ GBK（严格，视 ANSI 代码页而定）→
    /// 本地 ANSI → GBK（宽容）→ UTF-8 替换</c> 不能换：
    /// BOM 必须先剥，否则第一行表头会变成 "\uFEFF名称"，表头标签匹配全失败；
    /// 严格 UTF-8 必须先于 GBK，否则 UTF-8 的字节流会被 936 解成乱码而不报错。
    /// </para>
    /// <para>
    /// 为什么 GBK 有时要抢在 ANSI 前面：<c>Encoding.Default</c> 是「非 Unicode 程序的语言」
    /// 决定的 ANSI 代码页，中文 Windows 上是 936，但英文/西文 Windows 上是 1252 —— 而
    /// **单字节代码页永远不可能产出 U+FFFD**，它会把任何字节都映射成某个拉丁字符。
    /// 于是 GBK 的中文输出被解成 "ÔËÐÐ¿â" 这种看着「成功」的乱码，且没有任何异常可抓。
    /// 这类代码页（<see cref="Encoding.IsSingleByte"/>）与 UTF-8 页面（65001，见下）
    /// 根本装不下汉字，此时先试严格 GBK 只会更好，不会更差。
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
        private static readonly Encoding StrictGbk;

        static EncodedText()
        {
            try
            {
                // 936 = GBK。Windows 上一定有；万一取不到就留 null，解码时跳过这一档
                Gbk = Encoding.GetEncoding(936);

                // 严格版：遇到非法字节序列直接抛，而不是悄悄换成 U+FFFD。
                // 只有「会抛」才能区分「这段字节本来就不是 GBK」与「解出来了但有损」，
                // 否则无从判断该不该把解码权交回给 ANSI 代码页。
                StrictGbk = Encoding.GetEncoding(
                    936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (Exception)
            {
                Gbk = null;
                StrictGbk = null;
            }
        }

        /// <summary>把子进程的原始字节解成文本（见类注释里的顺序说明）。</summary>
        public static string Decode(byte[] raw)
        {
            return Decode(raw, Encoding.Default);
        }

        /// <summary>
        /// 同上，但显式指定「本机 ANSI 代码页」。存在的唯一目的是让自测能在中文机器上
        /// 复现 1252 机器才会走到的分支 —— 否则那条路径只有在英文 Windows / CI 上才暴露。
        /// </summary>
        public static string Decode(byte[] raw, Encoding ansi)
        {
            if (raw == null || raw.Length == 0)
            {
                return string.Empty;
            }

            if (ansi == null)
            {
                ansi = Encoding.Default;
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

            // ② ANSI 代码页装不下汉字时（单字节页 1252/437/850… 或 UTF-8 页 65001），
            //    先把严格 GBK 试掉。这一步必须在 ③ 之前，否则 ③ 会用一个「解得出字符
            //    但全是乱码」的结果提前 return（单字节页不产 U+FFFD，守卫拦不住）。
            if (StrictGbk != null && AnsiCannotCarryChinese(ansi))
            {
                try
                {
                    return StrictGbk.GetString(raw);
                }
                catch (DecoderFallbackException)
                {
                    // 确实不是 GBK（例如 cp1252 的 "café"），交给 ③
                }
            }

            // ③ 本机首选 ANSI 代码页（中文 Windows 即 936）
            //    坑：系统若开了「Beta: 使用 UTF-8 提供全球语言支持」，默认代码页就是 65001，
            //    此时 GetString **不会抛异常**，只会把 GBK 字节解成一串 U+FFFD（替换字符）。
            //    这种「静默乱码」比异常更糟 —— 它会提前 return，把下面真正的 GBK 兜底整段跳过，
            //    于是 winget 的中文输出在界面上变成一片「口口口」。所以必须检查替换字符：
            //    解出乱码就当作这一档失败，继续往下走。
            try
            {
                text = ansi.GetString(raw);
                if (!string.IsNullOrEmpty(text) && !ContainsReplacementChar(text))
                {
                    return text;
                }
            }
            catch (Exception)
            {
                // 忽略，继续下一档
            }

            // ④ 宽容 GBK（某些系统的默认代码页被改成 65001、且 ② 因故没跑时的兜底）
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

            // ⑤ 最后容错：UTF-8 + 替换字符（信息有损，但界面能继续跑）
            return LenientUtf8.GetString(raw, offset, raw.Length - offset);
        }

        /// <summary>
        /// 该 ANSI 代码页是否能表示汉字。单字节页（1252/437/850…）与 UTF-8 页（65001）
        /// 一律不能 —— 前者没有汉字码位，后者拿到的是「已确认不是合法 UTF-8」的字节。
        /// 932/936/949/950/54936 这类双字节页保持原有顺序，不改变既有行为。
        /// </summary>
        private static bool AnsiCannotCarryChinese(Encoding ansi)
        {
            return ansi.IsSingleByte || ansi.CodePage == 65001;
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
