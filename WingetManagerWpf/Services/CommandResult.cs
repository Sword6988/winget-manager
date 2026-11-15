namespace WingetManagerWpf.Services
{
    /// <summary>
    /// 一条 winget 命令的执行结果（输出已解码为文本）。
    /// </summary>
    public sealed class CommandResult
    {
        public CommandResult()
        {
            Args = new string[0];
            StdOut = string.Empty;
            StdErr = string.Empty;
        }

        public CommandResult(string[] args, int exitCode, string standardOutput, string standardError, int durationMs)
        {
            Args = args ?? new string[0];
            ExitCode = exitCode;
            StdOut = standardOutput ?? string.Empty;
            StdErr = standardError ?? string.Empty;
            DurationMs = durationMs;
        }

        /// <summary>实际执行的完整参数（含 exe 之后的全部参数，便于日志复现）。</summary>
        public string[] Args { get; set; }

        /// <summary>
        /// 退出码。注意 winget 的互斥冲突码 0x8A150001 = 2316632065 **超出 int 正数范围**，
        /// 这里会表现为负数，判定必须用 <c>unchecked((int)0x8A150001)</c> 比较（见 <see cref="IsMutexConflict"/>）。
        /// </summary>
        public int ExitCode { get; set; }

        public string StdOut { get; set; }

        public string StdErr { get; set; }

        /// <summary>耗时（毫秒，含互斥重试的退避时间）。</summary>
        public int DurationMs { get; set; }

        public bool Ok
        {
            get { return ExitCode == 0; }
        }

        /// <summary>stdout + stderr（stderr 为空时不加多余换行，与原版 combined 一致）。</summary>
        public string Combined
        {
            get
            {
                string stdout = StdOut ?? string.Empty;
                string stderr = StdErr ?? string.Empty;
                return stderr.Length == 0 ? stdout : stdout + "\n" + stderr;
            }
        }

        /// <summary>
        /// 这次失败是不是「winget 被别的实例占用」：
        /// ① 退出码正是互斥码；② 或者非 0 退出且**一个字都没输出**（个别版本的表现）。
        /// 两者对调用方的含义一样：等一会儿再来。重试有次数上限，真出错只是多花 0.8s。
        /// </summary>
        public bool IsMutexConflict
        {
            get
            {
                if (ExitCode == unchecked((int)0x8A150001))
                {
                    return true;
                }

                return ExitCode != 0 && string.IsNullOrEmpty((Combined ?? string.Empty).Trim());
            }
        }
    }
}
