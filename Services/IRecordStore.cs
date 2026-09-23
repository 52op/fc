using System.Collections.Generic;
using FC.Models;

namespace FC.Services
{
    public interface IRecordStore
    {
        /// <summary>读取全部记录。文件不存在返回空列表；解析失败也返回空列表。</summary>
        List<MigrationRecord> Load();

        /// <summary>整体覆盖保存。</summary>
        void SaveAll(List<MigrationRecord> records);

        /// <summary>按 Id 新增或更新一条。</summary>
        void Upsert(MigrationRecord record);
    }
}
