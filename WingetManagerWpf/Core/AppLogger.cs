using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 文件日志：<c>%LOCALAPPDATA%\WingetManager\logs\app.log</c>，512KB×2 轮转。
    /// 格式 <c>yyyy-MM-dd HH:mm:ss [INFO|WARNING|ERROR] message</c>（与原版 python
    /// logging 一致，便于两侧日志对照排查）。
    /// <para>
    /// 数据目录不可写时**降级为内存日志而不是抛异常**：日志写不出来绝不能导致
    /// 程序起不来或功能不可用（易错点 19）。
    /// </para>
    /// <para>
    /// <see cref="LogEmitted"/> 事件供 UI 日志面板复用同一份内容 —— 界面日志与文件日志
    /// 同源，避免出现「界面有这行、文件里没有」的排查困境。
    /// 注意：事件可能在后台线程触发，UI 侧必须自己 Dispatcher 回主线程。
    /// </para>
    /// </summary>
    public sealed class AppLogger : IDisposable
    {
        private const long MaxBytes = 512 * 1024;
        private const int MemoryCap = 2000;

        private readonly object _sync = new object();
        private readonly List<string> _memory = new List<string>();
        private readonly string _filePath;
        private long _currentLength = -1;
        private bool _fileDisabled;
        private bool _disposed;

        private AppLogger(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>事件参数：(时间, 级别, 文本)。级别为 Info/Success/Warn/Error。</summary>
        public event Action<DateTime, string, string> LogEmitted;

        /// <summary>实际日志文件路径；不可写时为 null（此时只有内存日志）。</summary>
        public string FilePath
        {
            get
            {
                lock (_sync)
                {
                    return _fileDisabled ? null : _filePath;
                }
            }
        }

        /// <summary>创建全局日志实例（唯一入口，UI 与后台共用）。</summary>
        public static AppLogger Create()
        {
            AppPaths.EnsureDataDir();
            string path = AppPaths.DataDirWritable ? AppPaths.LogFile : null;
            return new AppLogger(path);
        }

        /// <summary>创建一个「只进内存」的实例，供自测与数据目录不可写时使用。</summary>
        public static AppLogger CreateInMemory()
        {
            return new AppLogger(null);
        }

        public void Info(string message)
        {
            Write("INFO", "Info", message, null);
        }

        public void Success(string message)
        {
            // 文件里没有 SUCCESS 级别（python 也没有），UI 侧靠 tag 上色
            Write("INFO", "Success", message, null);
        }

        public void Warn(string message)
        {
            Write("WARNING", "Warn", message, null);
        }

        public void Error(string message, Exception ex)
        {
            Write("ERROR", "Error", message, ex);
        }

        public void Error(string message)
        {
            Write("ERROR", "Error", message, null);
        }

        /// <summary>异常专用入口（与原版 <c>logger.exception</c> 等价：带堆栈）。</summary>
        public void Exception(string message, Exception ex)
        {
            Write("ERROR", "Error", message, ex);
        }

        /// <summary>取日志尾部若干行（诊断信息用）。优先读文件，读不到用内存缓存。</summary>
        public IList<string> Tail(int maxLines)
        {
            if (maxLines <= 0)
            {
                return new string[0];
            }

            var result = new List<string>();
            lock (_sync)
            {
                if (!_fileDisabled && _filePath != null)
                {
                    try
                    {
                        if (File.Exists(_filePath))
                        {
                            string[] lines = File.ReadAllLines(_filePath, Encoding.UTF8);
                            int start = Math.Max(0, lines.Length - maxLines);
                            for (int i = start; i < lines.Length; i++)
                            {
                                result.Add(lines[i]);
                            }

                            return result;
                        }
                    }
                    catch (Exception)
                    {
                        // 读日志失败就退回内存缓存，不要让「关于框」跟着崩
                    }
                }

                int memStart = Math.Max(0, _memory.Count - maxLines);
                for (int i = memStart; i < _memory.Count; i++)
                {
                    result.Add(_memory[i]);
                }
            }

            return result;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
            }
        }

        private void Write(string level, string tag, string message, Exception ex)
        {
            string text = message ?? string.Empty;
            DateTime now = DateTime.Now;
            var sb = new StringBuilder(160);
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(" [").Append(level).Append("] ").Append(text);
            if (ex != null)
            {
                sb.Append(Environment.NewLine).Append(ex);
            }

            string line = sb.ToString();

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                AppendToFile(line);
            }

            Action<DateTime, string, string> handler = LogEmitted;
            if (handler != null)
            {
                try
                {
                    handler(now, tag, text);
                }
                catch (Exception)
                {
                    // 订阅方（UI）抛异常不能反过来打死打日志的调用点
                }
            }
        }

        private void AppendToFile(string line)
        {
            if (_fileDisabled || _filePath == null)
            {
                RememberInMemory(line);
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                byte[] payload = Encoding.UTF8.GetBytes(line + "\r\n");
                if (_currentLength < 0)
                {
                    _currentLength = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
                }

                if (_currentLength + payload.Length > MaxBytes)
                {
                    Rotate();
                    _currentLength = 0;
                }

                // 追加写 + 允许其他读者：多个读日志的工具（记事本）不会把写入挡住
                var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                try
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                finally
                {
                    stream.Dispose();
                }

                _currentLength += payload.Length;
            }
            catch (Exception)
            {
                // 磁盘满 / 无权限 / 文件被独占：静默降级为内存日志（只做一次）
                _fileDisabled = true;
                RememberInMemory(line);
            }
        }

        private void Rotate()
        {
            try
            {
                string archive = _filePath + ".1";
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }

                if (File.Exists(_filePath))
                {
                    File.Move(_filePath, archive);
                }
            }
            catch (Exception)
            {
                // 轮转失败（文件被占用）→ 继续往原文件追加，宁可文件变大也别丢日志
            }
        }

        private void RememberInMemory(string line)
        {
            _memory.Add(line.Replace("\r\n", " ⏎ "));
            while (_memory.Count > MemoryCap)
            {
                _memory.RemoveAt(0);
            }
        }
    }
}
