using System;
using System.IO;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 数据文件路径与「数据目录是否可写」的探测。
    /// <para>
    /// v3.6 起默认走便携布局：单文件 exe 旁边建 <c>Data\</c>，配置 / 勾选 / 日志都跟着 exe 走，
    /// 换个盘符、拷走文件夹、删目录即卸载，不需要写注册表也不需要用户目录。
    /// 只有当 exe 所在目录不可写（装在 Program Files、只读介质、受控目录）时才回落到
    /// <c>%LOCALAPPDATA%\WingetManager</c>，保证受限账户也能跑起来。
    /// </para>
    /// <para>
    /// 强制开关：<c>WPM_PORTABLE=0</c> 走用户目录，<c>WPM_PORTABLE=1</c> 强制便携，
    /// 或放一个 <c>WingetManager.portable</c> 标记文件（供不想依赖写权限探测的场景）。
    /// </para>
    /// </summary>
    public static class AppPaths
    {
        /// <summary>便携模式标记文件名：与 exe 同目录放一个同名空文件即可强制开启。</summary>
        public const string PortableMarkerFileName = "WingetManager.portable";

        private static readonly object SyncRoot = new object();
        private static readonly string AppDataRoot = ResolveLocalAppData();

        /// <summary>便携模式根目录（exe 所在目录）；null 表示非便携模式。</summary>
        private static readonly string PortableRoot = ResolvePortableRoot();

        private static bool? _writable;

        /// <summary>本机 LOCALAPPDATA 根（带兜底，绝不返回空串）。</summary>
        public static string LocalAppDataRoot
        {
            get { return AppDataRoot; }
        }

        /// <summary>
        /// 是否便携（绿色）模式：数据全部落在 exe 旁边，卸载 = 删文件夹。
        /// <para>
        /// **默认开启**（v3.6 起）：只要 exe 所在目录能建出可写的 <c>Data\</c>，就走便携布局。
        /// 装在 Program Files、只读介质或受控目录时才自动回落到 <c>%LOCALAPPDATA%\WingetManager</c>，
        /// 保证受限账户也能跑。
        /// </para>
        /// <para>
        /// 显式干预：环境变量 <c>WPM_PORTABLE=0</c>（或 false/no）强制用用户目录；
        /// <c>WPM_PORTABLE=1</c> 或 exe 同目录放 <c>WingetManager.portable</c> 空文件则强制便携。
        /// 首次进入便携模式时，<see cref="MigrateLegacyDataIfNeeded"/> 会把旧用户目录里的
        /// 勾选 / 配置 / 缓存**复制**过来，所以与原版 Python 版的数据是互通的。
        /// </para>
        /// </summary>
        public static bool IsPortable
        {
            get { return PortableRoot != null; }
        }

        /// <summary>
        /// 单实例作用域后缀：便携模式下按数据目录区分，
        /// 否则两个不同目录的绿色副本会互相抢窗口（后启动的会把先启动的顶到前台）。
        /// </summary>
        public static string InstanceScopeSuffix
        {
            get
            {
                if (PortableRoot == null)
                {
                    return string.Empty;
                }

                return "." + StableHash(DataDir);
            }
        }

        /// <summary>数据目录：普通模式 %LOCALAPPDATA%\WingetManager；便携模式 ＜exe 目录＞\Data。</summary>
        public static string DataDir
        {
            get
            {
                return PortableRoot != null
                    ? Path.Combine(PortableRoot, "Data")
                    : Path.Combine(AppDataRoot, "WingetManager");
            }
        }

        /// <summary>旧版固定使用的数据目录（%LOCALAPPDATA%\WingetManager），供迁移与提示文案使用。</summary>
        public static string LegacyDataDir
        {
            get { return Path.Combine(AppDataRoot, "WingetManager"); }
        }

        public static string LogDir
        {
            get { return Path.Combine(DataDir, "logs"); }
        }

        /// <summary>当前日志文件。</summary>
        public static string LogFile
        {
            get { return Path.Combine(LogDir, "app.log"); }
        }

        /// <summary>轮转后的旧日志（512KB×2 轮转）。</summary>
        public static string LogArchiveFile
        {
            get { return Path.Combine(LogDir, "app.log.1"); }
        }

        public static string SelectionFile
        {
            get { return Path.Combine(DataDir, "selection.json"); }
        }

        public static string MetadataCacheFile
        {
            get { return Path.Combine(DataDir, "metadata-cache.json"); }
        }

        /// <summary>
        /// settings.json（已知差异：原版用 QSettings 写注册表，本版落文件，
        /// 好处是可随数据目录一起备份）。
        /// </summary>
        public static string SettingsFile
        {
            get { return Path.Combine(DataDir, "settings.json"); }
        }

        /// <summary>数据目录是否可写（首次探测结果缓存，避免每次写文件都试一遍）。</summary>
        public static bool DataDirWritable
        {
            get
            {
                lock (SyncRoot)
                {
                    if (!_writable.HasValue)
                    {
                        _writable = IsDataDirWritable();
                    }

                    return _writable.Value;
                }
            }
        }

        /// <summary>建目录（失败静默：调用方靠 DataDirWritable 判断降级）。</summary>
        public static void EnsureDataDir()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(LogDir);
            }
            catch (Exception)
            {
                // 目录建不出来不是致命错误：所有存储都会退回内存态
            }
        }

        /// <summary>
        /// 真正探测一次：「建目录 → 写临时文件 → 删临时文件」。
        /// 只看目录是否存在不够，受限账户下目录可能存在但不可写。
        /// </summary>
        public static bool IsDataDirWritable()
        {
            return CanWriteTo(DataDir);
        }

        /// <summary>
        /// 目录可写探测（同样的三步法，供便携模式判定与数据目录探测共用）。
        /// 探测文件带 GUID 且用 CreateNew，避免与并发实例互相覆盖。
        /// </summary>
        private static bool CanWriteTo(string dir)
        {
            string probe = null;
            try
            {
                Directory.CreateDirectory(dir);
                probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0x20);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    try
                    {
                        if (File.Exists(probe))
                        {
                            File.Delete(probe);
                        }
                    }
                    catch (Exception)
                    {
                        // 探测文件删不掉不影响结论
                    }
                }
            }
        }

        /// <summary>
        /// 把旧版（固定写 %LOCALAPPDATA%）留下的数据搬到便携目录。
        /// <para>
        /// 只在「便携模式 + 便携目录里还没有自己的数据 + 用户目录里确实有旧数据」三条同时成立时才动手，
        /// 且是**复制**不是移动——用户回退到旧版或强制 WPM_PORTABLE=0 时数据仍在原处。
        /// 日志不搬：那是过程性文件，跟着旧目录留着即可。
        /// </para>
        /// </summary>
        /// <returns>实际复制的文件数（0 表示什么都没做）。</returns>
        public static int MigrateLegacyDataIfNeeded()
        {
            if (PortableRoot == null)
            {
                return 0;
            }

            try
            {
                string legacy = LegacyDataDir;
                if (string.Equals(
                        legacy.TrimEnd(Path.DirectorySeparatorChar),
                        DataDir.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                // 便携目录已经有用户数据：以它为准，绝不覆盖
                if (File.Exists(SelectionFile) || File.Exists(SettingsFile))
                {
                    return 0;
                }

                if (!Directory.Exists(legacy))
                {
                    return 0;
                }

                Directory.CreateDirectory(DataDir);

                int copied = 0;
                string[] names = { "selection.json", "settings.json", "metadata-cache.json" };
                foreach (string name in names)
                {
                    string src = Path.Combine(legacy, name);
                    if (!File.Exists(src))
                    {
                        continue;
                    }

                    try
                    {
                        File.Copy(src, Path.Combine(DataDir, name), true);
                        copied++;
                    }
                    catch (Exception)
                    {
                        // 单个文件失败不影响其余；实在不行就从头开始，没有数据损坏风险
                    }
                }

                return copied;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 导出默认路径：上次用过的位置 → D:\apps.json（D 盘存在时）→ 我的文档 → 用户目录。
        /// 早先版本硬编码 D 盘，在没有 D 盘的机器上会直接导出失败，所以必须逐级回退。
        /// </summary>
        public static string DefaultExportPath(string lastExportPath)
        {
            if (!string.IsNullOrEmpty(lastExportPath))
            {
                string trimmed = lastExportPath.Trim();
                if (trimmed.Length > 0)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(trimmed);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            return trimmed;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // 上次的路径本身非法（含非法字符等）：忽略它，继续回退
                    }
                }
            }

            const string DDriveDefault = @"D:\apps.json";
            try
            {
                if (Directory.Exists(@"D:\"))
                {
                    return DDriveDefault;
                }
            }
            catch (Exception)
            {
                // 极端情况下盘符探测也可能抛异常，继续回退
            }

            string docs = SafeSpecialFolder(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
            {
                return Path.Combine(docs, "apps.json");
            }

            string profile = SafeSpecialFolder(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
            {
                profile = Path.GetTempPath();
            }

            return Path.Combine(profile, "apps.json");
        }

        /// <summary>
        /// 判定便携模式。三级顺序：
        /// <list type="number">
        /// <item>环境变量 <c>WPM_PORTABLE</c>：显式开关，0/false/no 强制关闭，其余强制开启。</item>
        /// <item>exe 同目录的 <c>WingetManager.portable</c> 标记文件：强制开启。</item>
        /// <item>默认：exe 目录下能建 <c>Data\</c> 并写入，就按便携处理。装在只读位置时自然回落。</item>
        /// </list>
        /// 任何探测异常都当作「非便携」——写用户目录比写只读目录安全。
        /// </summary>
        private static string ResolvePortableRoot()
        {
            try
            {
                string exeDir = BaseDirectory;

                string flag = Environment.GetEnvironmentVariable("WPM_PORTABLE");
                if (!string.IsNullOrEmpty(flag))
                {
                    string v = flag.Trim();
                    if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                        || v.Equals("no", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    return exeDir;
                }

                if (File.Exists(Path.Combine(exeDir, PortableMarkerFileName)))
                {
                    return exeDir;
                }

                // 默认便携：只有 exe 目录真的写不进去，才退到用户目录
                if (CanWriteTo(Path.Combine(exeDir, "Data")))
                {
                    return exeDir;
                }
            }
            catch (Exception)
            {
                // 探测失败一律退回普通模式
            }

            return null;
        }

        /// <summary>exe 所在目录（带尾部分隔符；AppDomain 失败时退回当前工作目录）。</summary>
        private static string BaseDirectory
        {
            get
            {
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(dir))
                    {
                        return dir;
                    }
                }
                catch (Exception)
                {
                    // 极端沙箱环境下可能失败，继续兜底
                }

                return Environment.CurrentDirectory;
            }
        }

        /// <summary>
        /// FNV-1a 32 位：只用来生成稳定的短后缀。
        /// 不能用 string.GetHashCode()——它的算法在不同 CLR 版本间不保证一致，
        /// 换台机器算出的互斥量名就变了，单实例会失效。
        /// </summary>
        private static string StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }

                return hash.ToString("x8");
            }
        }

        private static string ResolveLocalAppData()
        {
            string env = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            {
                return env;
            }

            string folder = SafeSpecialFolder(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(folder))
            {
                return folder;
            }

            // 最后兜底：临时目录（虽然会丢失「跨重启」语义，但至少不会崩）
            return Path.GetTempPath();
        }

        private static string SafeSpecialFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
