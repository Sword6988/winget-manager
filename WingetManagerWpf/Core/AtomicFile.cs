using System;
using System.IO;
using System.Text;
using WingetManagerWpf.Core.Json;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 原子写盘：先写同目录临时文件 → 刷到磁盘（Flush(true)）→ 覆盖目标。
    /// 为什么不能直接 File.WriteAllText：它先截断目标再写，写到一半崩溃/磁盘满，
    /// 用户手里就只剩半个 JSON（勾选全丢、清单打不开）。同目录临时文件 + Replace
    /// 是 NTFS 上「要么旧、要么新」的唯一保证；临时文件必须与目标同盘同目录，
    /// 否则 Replace 会退化成跨盘拷贝，原子性就没了。
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>UTF-8 无 BOM（JSON / 日志 / txt 用）。</summary>
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>UTF-8 带 BOM（utf-8-sig）。核心产物不再用它（清单已改 HTML 无 BOM），保留给需要 BOM 的场景。</summary>
        public static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        /// <summary>
        /// 原子写文本。**不做换行转换**：StreamWriter 的默认行为会原样保留
        /// <c>\r\n</c>（我们没有用 WriteLine，也没有用会改写换行的重载），
        /// .bat 依赖的 CRLF 因此能保住。
        /// </summary>
        public static void WriteTextAtomic(string path, string text, Encoding encoding)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new BadRequestException("写入路径为空。");
            }

            Encoding enc = encoding ?? Utf8NoBom;
            string fullPath = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir))
            {
                throw new BadRequestException("写入路径没有所在目录：" + path);
            }

            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                try
                {
                    var writer = new StreamWriter(stream, enc);
                    try
                    {
                        writer.Write(text ?? string.Empty);
                        writer.Flush();
                        // 关键：把内核缓冲也刷到盘上，否则断电后临时文件可能是空的
                        stream.Flush(true);
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                }
                finally
                {
                    stream.Dispose();
                }

                Replace(tmp, fullPath);
            }
            catch (Exception)
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>原子写二进制（GBK 编码的 bat 走这条路）。</summary>
        public static void WriteAllBytesAtomic(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new BadRequestException("写入路径为空。");
            }

            byte[] payload = bytes ?? new byte[0];
            string fullPath = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir))
            {
                throw new BadRequestException("写入路径没有所在目录：" + path);
            }

            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                try
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                finally
                {
                    stream.Dispose();
                }

                Replace(tmp, fullPath);
            }
            catch (Exception)
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>
        /// 原子写 JSON：UTF-8 **无 BOM** + 2 空格缩进 + 尾随换行（与原版
        /// <c>json.dumps(indent=2) + "\n"</c> 逐字节等价）。
        /// </summary>
        public static void WriteJsonAtomic(string path, JsonValue payload)
        {
            string text = JsonWriter.Serialize(payload ?? JsonValue.Null) + "\n";

            // 显式用 UTF8Encoding(false)：Encoding.UTF8 带 BOM，会毁掉与原版的互通
            WriteTextAtomic(path, text, Utf8NoBom);
        }

        private static void Replace(string tmp, string target)
        {
            if (!File.Exists(target))
            {
                File.Move(tmp, target);
                return;
            }

            try
            {
                // Replace 保留目标的 ACL，并保证「读者要么看到旧文件、要么看到新文件」
                File.Replace(tmp, target, null);
            }
            catch (IOException)
            {
                // 目标被占用 / 跨卷等情况：退化为「覆盖拷贝 + 删临时文件」，
                // 牺牲一点点原子性也不能让导出直接失败
                File.Copy(tmp, target, true);
                TryDelete(tmp);
            }
            catch (UnauthorizedAccessException)
            {
                File.Copy(tmp, target, true);
                TryDelete(tmp);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(tmp, target, true);
                TryDelete(tmp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响正确性（下次同目录会留下一个 .tmp 垃圾但能继续跑）
            }
        }
    }
}
