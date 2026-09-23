using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 用 .NET Framework 自带的 XmlSerializer 把迁移记录存到 %APPDATA%\FC\migrations.xml。
    /// 写盘用临时文件 + 覆盖，避免写一半损坏。
    /// </summary>
    public class XmlRecordStore : IRecordStore
    {
        private readonly string _filePath;

        public XmlRecordStore(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>默认存储位置</summary>
        public static string DefaultPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FC");
            return Path.Combine(dir, "migrations.xml");
        }

        public List<MigrationRecord> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new List<MigrationRecord>();
                }

                using (var fs = File.OpenRead(_filePath))
                {
                    var ser = new XmlSerializer(typeof(MigrationRecordCollection));
                    var coll = ser.Deserialize(fs) as MigrationRecordCollection;
                    if (coll == null || coll.Items == null)
                    {
                        return new List<MigrationRecord>();
                    }
                    foreach (var r in coll.Items)
                    {
                        if (string.IsNullOrEmpty(r.Id))
                        {
                            r.Id = Guid.NewGuid().ToString("N");
                        }
                    }
                    return coll.Items;
                }
            }
            catch (Exception)
            {
                // 文件损坏/被占用时不阻塞主流程
                return new List<MigrationRecord>();
            }
        }

        public void SaveAll(List<MigrationRecord> records)
        {
            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = _filePath + ".tmp";
            var coll = new MigrationRecordCollection
            {
                Items = records ?? new List<MigrationRecord>()
            };

            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false)
            };

            var ns = new XmlSerializerNamespaces();
            ns.Add(string.Empty, string.Empty);

            using (var xw = XmlWriter.Create(tmp, settings))
            {
                var ser = new XmlSerializer(typeof(MigrationRecordCollection));
                ser.Serialize(xw, coll, ns);
                xw.Flush();
            }

            File.Copy(tmp, _filePath, true);
            File.Delete(tmp);
        }

        public void Upsert(MigrationRecord record)
        {
            var list = Load();
            var existing = list.FirstOrDefault(r => string.Equals(r.Id, record.Id, StringComparison.Ordinal));
            if (existing != null)
            {
                int idx = list.IndexOf(existing);
                list[idx] = record;
            }
            else
            {
                list.Add(record);
            }
            SaveAll(list);
        }
    }
}
