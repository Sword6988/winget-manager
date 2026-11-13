namespace WingetManager.Models;

/// <summary>
/// <c>winget list</c> 解析出的一行原始记录（对应 <c>winget_table.RawPackage</c>）。
/// 只承载表格里真实存在的字段，任何业务判定都在 <c>CatalogService</c> 里做。
/// </summary>
/// <param name="Name">名称列（可能为空，例如某些 MSIX 条目）。</param>
/// <param name="PackageId">ID 列（唯一标识，必填；空的行走不到这里）。</param>
/// <param name="Version">已安装版本。</param>
/// <param name="Available">可用版本（「可用」列，可能为空）。</param>
/// <param name="Source">源列：winget / msstore / 空（注册表安装、MSIX 商店桥等）。</param>
public sealed record RawPackage(
    string Name,
    string PackageId,
    string Version = "",
    string Available = "",
    string Source = "");
