using WingetManagerWpf.Core.Json;

namespace WingetManagerWpf.Models
{
    /// <summary>包 ID 的形态（决定右键「复制安装命令」怎么拼、图标怎么画）。</summary>
    public enum IdKind
    {
        /// <summary>普通 winget 包 ID（如 <c>Google.Chrome</c>）。</summary>
        Winget = 0,

        /// <summary>MSIX / 商店桥包（<c>msix\...</c>）。</summary>
        Msix = 1,

        /// <summary>注册表安装项（<c>ARP\...</c> 或含反斜杠）。</summary>
        Registry = 2
    }

    /// <summary>
    /// 规范化后的包。分组只看 <see cref="Reinstallable"/> + <see cref="IsDependency"/>：
    /// 「源」列有 winget/msstore 就能重装；运行库只是一个展示属性。
    /// </summary>
    public sealed class Package
    {
        public Package()
        {
            PackageIdentifier = string.Empty;
            Name = string.Empty;
            Version = string.Empty;
            AvailableVersion = string.Empty;
            Source = string.Empty;
            // 属性名与枚举类型名同为 IdKind：这里用完全限定名，避免「同名解析」歧义
            IdKind = WingetManagerWpf.Models.IdKind.Winget;
        }

        /// <summary>包标识符（保留原始大小写，导出清单必须原样写）。</summary>
        public string PackageIdentifier { get; set; }

        /// <summary>显示名称；为空时回落成包 ID。</summary>
        public string Name { get; set; }

        public string Version { get; set; }

        /// <summary>「可用」版本（有新版本时非空）。</summary>
        public string AvailableVersion { get; set; }

        /// <summary>源：winget / msstore / ""。</summary>
        public string Source { get; set; }

        public IdKind IdKind { get; set; }

        /// <summary>能不能被 winget 重装（源列 ∈ {winget, msstore}）。</summary>
        public bool Reinstallable { get; set; }

        /// <summary>是不是运行库/系统组件（关键词初判 + 元数据核查补漏）。</summary>
        public bool IsDependency { get; set; }

        /// <summary>去重/勾选用的键：包 ID 小写（与原版一致）。</summary>
        public string Key
        {
            get { return (PackageIdentifier ?? string.Empty).ToLowerInvariant(); }
        }

        /// <summary>
        /// 序列化成 JSON（诊断信息 / 自测断言用）。字段名与原版 <c>dataclasses.asdict</c> 一致，
        /// 键序也保持一致，便于把两边快照直接 diff。
        /// </summary>
        public JsonValue ToJson()
        {
            JsonValue value = JsonValue.NewObject();
            value.Set("packageIdentifier", JsonValue.From(PackageIdentifier));
            value.Set("name", JsonValue.From(Name));
            value.Set("version", JsonValue.From(Version));
            value.Set("availableVersion", JsonValue.From(AvailableVersion));
            value.Set("source", JsonValue.From(Source));
            value.Set("idKind", JsonValue.From(IdKindText(IdKind)));
            value.Set("reinstallable", JsonValue.From(Reinstallable));
            value.Set("isDependency", JsonValue.From(IsDependency));
            return value;
        }

        public static string IdKindText(IdKind kind)
        {
            switch (kind)
            {
                case WingetManagerWpf.Models.IdKind.Msix:
                    return "msix";
                case WingetManagerWpf.Models.IdKind.Registry:
                    return "registry";
                default:
                    return "winget";
            }
        }
    }
}
