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
