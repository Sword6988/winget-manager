using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using WingetManager.Models;

namespace WingetManager.Json;

/// <summary>
/// System.Text.Json <b>源生成</b>上下文：全项目唯一的 JSON 序列化入口。
///
/// AOT 硬约束（ARCHITECTURE §1.4）：任何 DTO 必须在这里用 <c>[JsonSerializable]</c> 注册，
/// 读写必须传 context（<c>AppJsonContext.Default.Xxx</c>）；禁止无 context 的
/// <c>JsonSerializer.Serialize(object)</c> 重载、禁止 <c>dynamic</c> / 反射序列化。
///
/// 写盘格式与 Python 版对齐：两空格缩进（<c>WriteIndented</c>）。落盘的三个文件
/// （apps.json / selection.json / metadata-cache.json）字段值全是 ASCII（包 ID、时间戳），
/// 因此不需要额外设置 encoder 就能与 Python 版字节一致。
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
}
