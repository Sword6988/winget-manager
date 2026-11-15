using System;
using System.Globalization;

namespace WingetManagerWpf.ViewModels
{
    /// <summary>
    /// 日志面板里的一行。<see cref="Tag"/> 决定上色，<see cref="Text"/> 是正文（不含时间戳）。
    /// <para>
    /// 为什么单独建类而不是直接往 RichTextBox 里塞字符串：日志面板要支持「收起后
    /// 仍在后台继续收」、「复制全部」、「另存为」，这些都需要能拿到结构化行数据。
    /// </para>
    /// </summary>
    public sealed class LogEntry
    {
        /// <summary>普通信息（文件日志级别 INFO）。</summary>
        public const string TagInfo = "Info";

        /// <summary>成功（文件日志级别仍写 INFO，界面绿色）。</summary>
        public const string TagSuccess = "Success";

        /// <summary>警告。</summary>
        public const string TagWarn = "Warn";

        /// <summary>错误。</summary>
        public const string TagError = "Error";

        /// <summary>导出/扫描「完成」的收尾行（紫色加粗）。</summary>
        public const string TagDone = "Done";

        /// <summary>跳过（例如缓存命中跳过 show）。</summary>
        public const string TagSkip = "Skip";

        public LogEntry(DateTime timestamp, string tag, string text)
        {
            Timestamp = timestamp;
            Tag = NormalizeTag(tag);
            Text = text ?? string.Empty;
        }

        /// <summary>发生时间（本地时间）。</summary>
        public DateTime Timestamp { get; private set; }

        /// <summary>归一化后的标签（见 Tag* 常量）。</summary>
        public string Tag { get; private set; }

        /// <summary>正文，可能为空串（例如纯分隔行）。</summary>
        public string Text { get; private set; }

        /// <summary>「HH:mm:ss」。</summary>
        public string TimeText
        {
            get { return Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// 「完成」类行加粗显示。Success 与 Done 都算：一个扫描/导出段落结束时
        /// 需要一个视觉落点，否则整屏日志像流水账。
        /// </summary>
        public bool IsEmphasis
        {
            get
            {
                return string.Equals(Tag, TagSuccess, StringComparison.Ordinal)
                       || string.Equals(Tag, TagDone, StringComparison.Ordinal)
                       || string.Equals(Tag, TagError, StringComparison.Ordinal);
            }
        }

        /// <summary>复制/另存时用的整行文本：「HH:mm:ss [级别] 正文」。</summary>
        public string DisplayLine
        {
            get
            {
                string level = LevelText;
                if (level.Length == 0)
                {
                    return TimeText + "  " + Text;
                }

                return TimeText + " [" + level + "] " + Text;
            }
        }

        /// <summary>级别缩写；Info 不写（原版面板也不给普通行加级别前缀，避免噪音）。</summary>
        public string LevelText
        {
            get
            {
                if (string.Equals(Tag, TagWarn, StringComparison.Ordinal))
                {
                    return "WARN";
                }

                if (string.Equals(Tag, TagError, StringComparison.Ordinal))
                {
                    return "ERROR";
                }

                return string.Empty;
            }
        }

        /// <summary>
        /// 把各种来源的标签统一：<see cref="Core.AppLogger"/> 用 Info/Success/Warn/Error，
        /// <see cref="Services.ExportProfiles"/> 的小写 info/done/fail/skip 也要认。
        /// </summary>
        public static string NormalizeTag(string tag)
        {
            string text = (tag ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return TagInfo;
            }

            if (text.Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                return TagInfo;
            }

            if (text.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                return TagSuccess;
            }

            if (text.Equals("warn", StringComparison.OrdinalIgnoreCase)
                || text.Equals("warning", StringComparison.OrdinalIgnoreCase))
            {
                return TagWarn;
            }

            if (text.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                return TagSkip;
            }

            if (text.Equals("error", StringComparison.OrdinalIgnoreCase)
                || text.Equals("fail", StringComparison.OrdinalIgnoreCase))
            {
                return TagError;
            }

            if (text.Equals("done", StringComparison.OrdinalIgnoreCase)
                || text.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return TagDone;
            }

            return text;
        }
    }
}
