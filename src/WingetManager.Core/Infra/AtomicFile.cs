using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WingetManager.Infra;

/// <summary>
/// 原子写盘（对应 <c>core/fsio.py</c>）：先写同目录临时文件，fsync 后替换目标。
///
/// 为什么要这么麻烦：直接截断目标文件再写，写到一半崩溃/断电/磁盘满，用户手里就只剩
/// 半个 JSON。同目录临时文件 + 原子替换是 NTFS 上「要么旧、要么新」的唯一保证；
/// 临时文件必须和目标同目录，否则替换退化成跨卷拷贝，原子性就没了。
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 把文本原子写入 <paramref name="path"/>（父目录不存在时先建）。
    /// 文本按原样写入，不做换行转换（.bat 依赖 CRLF）。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="text">要写入的文本（调用方保证换行符符合要求）。</param>
    /// <param name="encoding">编码；默认 UTF-8（无 BOM）。GBK 写盘传 <c>Encoding.GetEncoding(936)</c>。</param>
    /// <exception cref="IOException">磁盘满/无权限等；失败时目标文件保持旧内容，临时文件被清理。</exception>
    public static void WriteText(string path, string text, Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var enc = encoding ?? Utf8NoBom;
        var bytes = enc.GetBytes(text);
        WriteBytes(fullPath, bytes);
    }

    /// <summary>
    /// 原子写入 JSON（UTF-8 无 BOM、两空格缩进、结尾换行），
    /// 序列化必须走 System.Text.Json 源生成的 <see cref="JsonTypeInfo{T}"/>（AOT 要求）。
    /// </summary>
    /// <typeparam name="T">DTO 类型（必须在 AppJsonContext 中注册）。</typeparam>
    /// <param name="path">目标文件路径。</param>
    /// <param name="payload">要序列化的对象。</param>
    /// <param name="typeInfo">源生成的类型元数据，例如 <c>AppJsonContext.Default.WingetImportManifest</c>。</param>
    public static void WriteJson<T>(string path, T payload, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        // WriteIndented 在 AppJsonContext 上统一声明为 true（源生成不支持运行时 options 注入）
        var json = JsonSerializer.Serialize(payload, typeInfo);
        WriteText(path, json + "\n");
    }

    /// <summary>原子写原始字节：临时文件 + fsync + 替换。</summary>
    private static void WriteBytes(string fullPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var tempName = Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var tempPath = Path.Combine(directory, tempName);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                              FileShare.None, bufferSize: 4096))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                // 同卷替换：要么保留旧文件，要么换成新文件，不存在「半个文件」的中间态
                File.Replace(tempPath, fullPath, destinationBackupFileName: null,
                             ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不该掩盖原始异常
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
