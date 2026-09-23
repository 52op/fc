using System;
using System.Collections.Generic;
using System.IO;

namespace FC.Services
{
    /// <summary>
    /// 特殊文件识别：给系统/隐藏/超大占用类文件打说明标签（pagefile/hiberfil/swapfile 等）。
    /// 判断"隐藏或系统"用文件属性——本工具默认显示这些文件并加标记说明（大占位文件正是用户关心的）。
    /// </summary>
    public static class SpecialFiles
    {
        private static readonly Dictionary<string, string> Notes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "pagefile.sys", "虚拟内存页面文件（系统分页，可非常大）" },
                { "hiberfil.sys", "休眠文件（休眠时保存内存镜像，与内存同量级）" },
                { "swapfile.sys", "系统交换文件（部分 UWP/新式应用用）" },
                { "DumpStack.log.tmp", "内存转储日志临时文件" },
                { "sleepstudy.bin", "睡眠/唤醒功耗诊断数据" },
                { "HybridSleep.ini", "混合休眠配置" },
                { "MifareStan.drv", "系统驱动缓存文件" },
                { "iconcache.db", "系统图标缓存（可重建）" },
                { "thumbcache_*.db", "缩略图缓存（可重建）" },
                { "thumbs.db", "缩略图缓存（可重建）" },
                { "desktop.ini", "文件夹显示配置" },
                { "ntuser.dat", "用户配置（注册表蜂巢）" },
                { "ntuser.dat.log1", "用户配置日志" },
                { "ntuser.dat.log2", "用户配置日志" },
                { "*.log", "日志文件" }
            };

        /// <summary>文件名（含通配）→ 说明。无命中返回 null。</summary>
        public static string GetNote(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }
            if (Notes.TryGetValue(fileName, out string note))
            {
                return note;
            }
            // 通配条目（如 *.log / iconcache 前缓等）
            try
            {
                var wildcard = System.Text.RegularExpressions.Regex.Match(fileName, @"^\w+_\d+\.dat$");
                if (wildcard.Success && fileName.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase))
                {
                    return "缩略图缓存（可重建）";
                }
                if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && Notes.TryGetValue("*.log", out string l))
                {
                    return "日志文件";
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>路径是否为隐藏或系统文件。判断失败返回 false。</summary>
        public static bool IsHiddenOrSystem(string fullPath)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(fullPath);
                return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}