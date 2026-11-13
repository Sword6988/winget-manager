using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WingetManager.Models;

/// <summary>
/// <c>apps.json</c> 导出 DTO：winget 官方 import schema 2.0
/// （对应 <c>profiles.build_winget_manifest</c>）。
///
/// 属性名/顺序与 Python 版 dict 逐字段对齐：<c>$schema</c> / <c>CreationDate</c> /
/// <c>Sources</c> / <c>WinGetVersion</c>。
/// </summary>
public sealed class WingetImportManifest
{
    /// <summary>winget import 的固定 schema 地址。</summary>
    public const string SchemaUrl = "https://aka.ms/winget-packages.schema.2.0.json";

    /// <summary>winget 源的 SourceDetails。</summary>
    public const string WingetSourceArgument = "https://cdn.winget.microsoft.com/cache";
    public const string WingetSourceIdentifier = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    public const string WingetSourceName = "winget";
    public const string WingetSourceType = "Microsoft.PreIndexed.Package";

    /// <summary>msstore 源的 SourceDetails。</summary>
    public const string MsStoreSourceArgument = "https://storeedgefd.dsx.mp.microsoft.com/v9.0";
    public const string MsStoreSourceIdentifier = "StoreEdgeFD";
    public const string MsStoreSourceName = "msstore";
    public const string MsStoreSourceType = "Microsoft.Rest";

    /// <summary>schema 地址。</summary>
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = SchemaUrl;

    /// <summary>生成时间（ISO8601，毫秒精度，带本地时区）。</summary>
    [JsonPropertyName("CreationDate")]
    public string CreationDate { get; set; } = string.Empty;

    /// <summary>按源分组后的包清单（顺序 = 源名排序）。</summary>
    [JsonPropertyName("Sources")]
    public List<WingetImportSource> Sources { get; set; } = new();

    /// <summary>归一化后的 winget 版本（必须数字开头，解析不出回退 1.0.0）。</summary>
    [JsonPropertyName("WinGetVersion")]
    public string WingetVersion { get; set; } = "1.0.0";
}

/// <summary>一个源下的包清单。</summary>
public sealed class WingetImportSource
{
    /// <summary>该源下的包（只带 PackageIdentifier）。</summary>
    [JsonPropertyName("Packages")]
    public List<WingetImportPackage> Packages { get; set; } = new();

    /// <summary>源描述。</summary>
    [JsonPropertyName("SourceDetails")]
    public WingetSourceDetails SourceDetails { get; set; } = new();
}

/// <summary>import 清单里的单个包。</summary>
public sealed class WingetImportPackage
{
    /// <summary>包 ID。</summary>
    [JsonPropertyName("PackageIdentifier")]
    public string PackageIdentifier { get; set; } = string.Empty;
}

/// <summary>源描述（字段顺序与 Python 版 dict 一致：Argument / Identifier / Name / Type）。</summary>
public sealed class WingetSourceDetails
{
    /// <summary>源地址。</summary>
    [JsonPropertyName("Argument")]
    public string Argument { get; set; } = string.Empty;

    /// <summary>源标识。</summary>
    [JsonPropertyName("Identifier")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>源名。</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>源类型。</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;
}
