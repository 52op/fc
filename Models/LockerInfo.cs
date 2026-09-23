namespace FC.Models
{
    /// <summary>占用目录/文件的进程信息（Restart Manager 检测）</summary>
    public class LockerInfo
    {
        public int ProcessId { get; set; }

        public string ProcessName { get; set; }

        public override string ToString()
        {
            string name = string.IsNullOrEmpty(ProcessName) ? "(未知)" : ProcessName;
            return name + " (PID " + ProcessId + ")";
        }
    }
}