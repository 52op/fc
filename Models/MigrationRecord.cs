using System;
using System.Xml.Serialization;

namespace FC.Models
{
    public enum MigrationStatus
    {
        /// <summary>迁移完成，源位置是 junction，数据在目标位置</summary>
        Active,

        /// <summary>已还原（数据已移回源位置，联接已删除）</summary>
        Restored,

        /// <summary>出现过错误</summary>
        Error
    }

    /// <summary>迁移方式：Junction=原位置留联接；EnvVar=通过环境变量重定位（源目录已删除）。</summary>
    public enum MigrationKind
    {
        Junction,
        EnvVar
    }

    /// <summary>
    /// 一条迁移记录。可被 XmlSerializer 序列化到 %APPDATA%\FC\migrations.xml。
    /// 属性全部要有公共 setter 才能序列化。
    /// </summary>
    [XmlRoot("MigrationRecord")]
    public class MigrationRecord
    {
        [XmlElement]
        public string Id { get; set; }

        /// <summary>迁移前的位置（还原时数据的最终去处）</summary>
        [XmlElement]
        public string SourcePath { get; set; }

        /// <summary>数据实际存放位置（还原时数据的来源）</summary>
        [XmlElement]
        public string DestPath { get; set; }

        [XmlElement]
        public DateTime MigratedAt { get; set; }

        [XmlElement(IsNullable = true)]
        public DateTime? RestoredAt { get; set; }

        [XmlElement]
        public long TotalBytes { get; set; }

        [XmlElement]
        public long FileCount { get; set; }

        [XmlElement]
        public MigrationStatus Status { get; set; }

        [XmlElement]
        public string ErrorMessage { get; set; }

        // ==================== 环境变量迁移扩展（Junction 迁移时保持默认） ====================

        /// <summary>迁移方式。旧记录反序列化默认 Junction。</summary>
        [XmlElement]
        public MigrationKind MigrationKind { get; set; }

        /// <summary>环境变量名（仅 EnvVar 方式）。</summary>
        [XmlElement(IsNullable = true)]
        public string EnvVarName { get; set; }

        /// <summary>环境变量作用域："User" / "Machine"（仅 EnvVar 方式）。</summary>
        [XmlElement(IsNullable = true)]
        public string EnvVarScope { get; set; }

        /// <summary>迁移前的环境变量旧值（还原时恢复；空=原不存在，还原时删除）。</summary>
        [XmlElement(IsNullable = true)]
        public string EnvVarOldValue { get; set; }

        /// <summary>迁移后写入的环境变量新值（记录备查）。</summary>
        [XmlElement(IsNullable = true)]
        public string EnvVarNewValue { get; set; }

        /// <summary>迁移时是否在源位置保留了 junction 兜底。</summary>
        [XmlElement]
        public bool JunctionFallback { get; set; }

        /// <summary>软件显示名（仅 EnvVar 方式，来自规则表）。</summary>
        [XmlElement(IsNullable = true)]
        public string SoftwareName { get; set; }

        public MigrationRecord()
        {
            Id = Guid.NewGuid().ToString("N");
            MigratedAt = DateTime.Now;
            Status = MigrationStatus.Active;
        }

        /// <summary>仅供 UI 显示</summary>
        [XmlIgnore]
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case MigrationStatus.Active:
                        return "已迁移";
                    case MigrationStatus.Restored:
                        return "已还原";
                    default:
                        return "出错";
                }
            }
        }
    }

    /// <summary>
    /// XmlSerializer 对 List&lt;T&gt; 顶层序列化需要一层包装类。
    /// </summary>
    [XmlRoot("MigrationRecords")]
    public class MigrationRecordCollection
    {
        [XmlElement("Record")]
        public System.Collections.Generic.List<MigrationRecord> Items { get; set; }

        public MigrationRecordCollection()
        {
            Items = new System.Collections.Generic.List<MigrationRecord>();
        }
    }
}
