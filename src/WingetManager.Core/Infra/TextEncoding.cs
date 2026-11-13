using System;
using System.Text;
using System.Threading;

namespace WingetManager.Infra;

/// <summary>
/// 文本编码集中管理：GBK/cp936 需要注册 <see cref="CodePagesEncodingProvider"/>，
/// .NET 默认不带这张码表。
///
/// AOT 安全性：<c>Encoding.RegisterProvider</c> 是纯托管注册表操作，不依赖反射；
/// PRD Q4 已裁定用这条路径复刻 Python 版「GBK 写盘 + 解码回落」的行为。
/// </summary>
public static class TextEncoding
{
    /// <summary>GBK / cp936 的代码页号（cmd 默认代码页）。</summary>
    public const int GbkCodePage = 936;

    private static readonly Lazy<Encoding> GbkLazy = new(() =>
    {
        EnsureProvider();
        return Encoding.GetEncoding(GbkCodePage);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Encoding> GbkStrictLazy = new(() =>
    {
        EnsureProvider();
        // 默认 fallback 是「静默替换成 ?」，这里要复刻 Python 的 <c>.encode("gbk")</c>：
        // 编不进去必须抛 EncoderFallbackException，否则用户会拿到一份带 ? 的恢复脚本。
        return Encoding.GetEncoding(GbkCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static int _registered;

    /// <summary>UTF-8（无 BOM）：JSON / 日志 / 普通文本落盘用。</summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8（带 BOM）：<c>无法自动重装清单.txt</c> 用（与原版 utf-8-sig 对齐）。</summary>
    public static Encoding Utf8Bom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>严格 UTF-8（遇非法字节抛异常），用于解码链第一跳。</summary>
    public static Encoding StrictUtf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>GBK（cp936）：<c>恢复-apps.bat</c> 写盘用。</summary>
    public static Encoding Gbk => GbkLazy.Value;

    /// <summary>
    /// 严格 GBK（编不进去抛 <see cref="EncoderFallbackException"/>）。
    /// 用来在写盘前校验文件名等用户可控文本（对应 Python 的 <c>str.encode("gbk")</c>）。
    /// </summary>
    public static Encoding GbkStrict => GbkStrictLazy.Value;

    /// <summary>注册代码页提供程序（幂等，可重复调用）。</summary>
    public static void EnsureProvider()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    /// <summary>
    /// 按 Python 版的顺序解码子进程输出：
    /// utf-8-sig → utf-8 → cp936 → gbk → utf-8（容错替换）。
    ///
    /// 顺序很重要：<c>utf-8-sig</c> 必须先试，否则 BOM 会变成 U+FEFF 混进第一行，
    /// 后面按行首匹配包 ID / 版本号的地方都会莫名其妙地失败。
    /// </summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>解码后的文本。</returns>
    public static string DecodeChain(byte[] raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return string.Empty;
        }

        // 1) utf-8-sig：严格 UTF-8 解出来再去掉开头 BOM
        if (TryDecode(raw, StrictUtf8, out var trimmed))
        {
            return trimmed.Length > 0 && trimmed[0] == '\uFEFF' ? trimmed[1..] : trimmed;
        }

        // 2) utf-8（严格）与 1) 等价，跳过重复尝试；直接进本地编码
        foreach (var encoding in new[] { TryGetEncoding(GbkCodePage), TryGetEncoding("gbk") })
        {
            if (encoding is null)
            {
                continue;
            }

            if (TryDecode(raw, encoding, out var text))
            {
                return text;
            }
        }

        // 3) 容错兜底：非法字节替换成 U+FFFD，绝不让界面因为一个乱码字节崩掉
        return Encoding.UTF8.GetString(raw);
    }

    private static Encoding? TryGetEncoding(int codePage)
    {
        try
        {
            EnsureProvider();
            return Encoding.GetEncoding(codePage);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            EnsureProvider();
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryDecode(byte[] raw, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(raw);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
        catch (ArgumentException)
        {
            text = string.Empty;
            return false;
        }
    }
}
