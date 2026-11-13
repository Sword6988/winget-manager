using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WingetManager.Models;

namespace WingetManager.Json;

/// <summary>
/// System.Text.Json <b>源生成</b>上下文：全项目唯一的 JSON 序列化入口。
///
/// AOT 硬约束（ARCHITECTURE §1.4）：任何 DTO 必须在这里用 <c>[JsonSerializable]</c> 注册，
/// 读写必须传 context（<c>AppJsonContext.Default.Xxx</c> / <c>AppJsonContext.Compat.Xxx</c>）；
/// 禁止无 context 的 <c>JsonSerializer.Serialize(object)</c> 重载、禁止 <c>dynamic</c> / 反射序列化。
///
/// 写盘格式与 Python 版对齐：两空格缩进（<c>WriteIndented</c>）+ 不转义非 ASCII 与 <c>+</c>
/// （<c>Compat</c> 实例，等价 <c>json.dumps(ensure_ascii=False)</c>）+ LF 换行（由
/// <c>AtomicFile.WriteJson</c> 归一化）。
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization | JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNameCaseInsensitive = false,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(MetadataCacheFile))]
[JsonSerializable(typeof(MetadataCacheEntry))]
[JsonSerializable(typeof(CatalogCacheFile))]
[JsonSerializable(typeof(WingetImportManifest))]
[JsonSerializable(typeof(WingetImportSource))]
[JsonSerializable(typeof(WingetImportPackage))]
[JsonSerializable(typeof(WingetSourceDetails))]
[JsonSerializable(typeof(CatalogSnapshot))]
[JsonSerializable(typeof(MachineInfo))]
[JsonSerializable(typeof(CatalogCounts))]
[JsonSerializable(typeof(Package))]
[JsonSerializable(typeof(List<Package>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, MetadataCacheEntry>))]
public partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 写盘专用实例：与 <see cref="Default"/> 的差别只有 encoder——
    /// 用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> 让 <c>+</c>（时区偏移）
    /// 和中文按原样输出，等价 Python <c>json.dumps(ensure_ascii=False)</c> 的字节结果。
    /// </summary>
    public static AppJsonContext Compat { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

/// <summary>
/// JSON 文本输出：全项目唯一的「对象 → Python 兼容 JSON 文本」转换点。
///
/// 与 <c>JsonSerializer.Serialize</c> 的差别只有换行：.NET 的缩进 writer 在 Windows 上
/// 用 CRLF，Python <c>json.dumps</c> 用 LF；不归一化两个版本写出的文件字节就不一致。
/// </summary>
public static class JsonOutput
{
    /// <summary>
    /// 序列化成与 Python 版一致的 JSON 文本（两空格缩进、LF 换行、非 ASCII 与 <c>+</c> 不转义）。
    /// </summary>
    /// <typeparam name="T">DTO 类型。</typeparam>
    /// <param name="payload">要序列化的对象。</param>
    /// <param name="typeInfo">源生成的类型元数据（必须来自 <see cref="AppJsonContext"/>）。</param>
    /// <returns>JSON 文本（不带结尾换行）。</returns>
    public static string Serialize<T>(T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        if (json.Contains('\r'))
        {
            // JSON 文本里的字面 CR 只可能来自缩进换行（字符串值里的回车是 \r 转义）
            json = json.Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace('\r', '\n');
        }

        return json;
    }
}
