using System;
using System.Collections.Generic;
using System.Configuration;

namespace FC.Services
{
    /// <summary>
    /// 排除目录名列表：命中名字的目录不枚举、计入"已排除"（行内标注）。
    /// 默认内置一批巨目录/无关目录；可在 App.config 的 SkippedFolderNames（逗号分隔）里覆盖。
    /// </summary>
    public static class SkipList
    {
        private static readonly HashSet<string> Names;

        static SkipList()
        {
            Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string cfg = ConfigurationManager.AppSettings["SkippedFolderNames"];
                if (!string.IsNullOrWhiteSpace(cfg))
                {
                    foreach (var s in cfg.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = s.Trim();
                        if (t.Length > 0)
                        {
                            Names.Add(t);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 配置读取失败用默认
            }
            if (Names.Count == 0)
            {
                // 默认值：巨/无关目录（可配置覆盖）
                foreach (var s in new[] { "WinSxS", "System Volume Information", "$Recycle.Bin", "node_modules", ".git", "Windows.old" })
                {
                    Names.Add(s);
                }
            }
        }

        public static bool IsSkipped(string folderName)
        {
            return !string.IsNullOrEmpty(folderName) && Names.Contains(folderName);
        }
    }
}