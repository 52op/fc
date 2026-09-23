namespace FC.Models
{
    /// <summary>一次迁移/还原操作的最终结果</summary>
    public class MigrationResult
    {
        public bool Success { get; set; }

        /// <summary>robocopy 的退出码</summary>
        public int RobocopyExitCode { get; set; }

        public bool Cancelled { get; set; }

        public long Files { get; set; }

        public long Bytes { get; set; }

        /// <summary>占用源/目标目录的进程（检测到时由调用方提示用户结束并重试）</summary>
        public System.Collections.Generic.List<LockerInfo> Lockers { get; set; }

        public bool LockersFound
        {
            get { return Lockers != null && Lockers.Count > 0; }
        }

        /// <summary>面向用户的一句话总结（成功或失败原因）</summary>
        public string Message { get; set; }

        public MigrationResult()
        {
            Lockers = new System.Collections.Generic.List<LockerInfo>();
        }
    }
}
