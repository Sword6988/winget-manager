namespace WingetManagerWpf.Models
{
    /// <summary>
    /// <c>winget list</c> 解析出的一行原始数据（未经任何判定）。
    /// 保持「字段全为字符串」是为了不丢信息：解析阶段只负责切列，
    /// 是否可重装、是不是运行库全部在 PackageCatalog 里决定。
    /// </summary>
    public sealed class RawPackage
    {
        public RawPackage()
        {
            Name = string.Empty;
            PackageId = string.Empty;
            Version = string.Empty;
            Available = string.Empty;
            Source = string.Empty;
        }

        public RawPackage(string name, string packageId, string version, string available, string source)
        {
            Name = name ?? string.Empty;
            PackageId = packageId ?? string.Empty;
            Version = version ?? string.Empty;
            Available = available ?? string.Empty;
            Source = source ?? string.Empty;
        }

        /// <summary>「名称」列。</summary>
        public string Name { get; set; }

        /// <summary>「ID」列（包标识符）；为空的行在解析阶段就会被丢掉。</summary>
        public string PackageId { get; set; }

        /// <summary>「版本」列，可能为空串。</summary>
        public string Version { get; set; }

        /// <summary>「可用」列（有新版本时 winget 才填）。</summary>
        public string Available { get; set; }

        /// <summary>「源」列：winget / msstore / 空串（空串表示 winget 装不回来）。</summary>
        public string Source { get; set; }
    }
}
