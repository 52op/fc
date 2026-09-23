namespace FC.Models
{
    /// <summary>按扩展名聚合的文件占用统计（扫描阶段并发累计，结果取 Top N）</summary>
    public struct TypeStat
    {
        /// <summary>小写扩展名（含点，如 ".mp4"）。无扩展名的文件为 null，显示层转 "(无扩展名)"。</summary>
        public string Extension;

        /// <summary>该类型累计字节数</summary>
        public long Bytes;

        /// <summary>该类型文件数量</summary>
        public long Count;
    }
}
