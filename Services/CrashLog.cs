using System;
using System.IO;

namespace FC.Services
{
    /// <summary>全局崩溃/异常落盘日志（%APPDATA%\FC\error.log），便于远端机器闪退后定位。</summary>
    public static class CrashLog
    {
        private static readonly object Sync = new object();
        private static string _file;

        private static string FilePath
        {
            get
            {
                if (_file == null)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FC");
                    _file = Path.Combine(dir, "error.log");
                }
                return _file;
            }
        }

        /// <summary>日志文件完整路径（供提示用户查看）。</summary>
        public static string LogPath
        {
            get { return FilePath; }
        }

        /// <summary>追加一行。写失败静默（日志本身不能让程序崩）。</summary>
        public static void Write(string section, Exception ex)
        {
            try
            {
                lock (Sync)
                {
                    string dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    string line = string.Format(
                        "[{0:yyyy-MM-dd HH:mm:ss}] {1}：{2}\n    {3}\n",
                        DateTime.Now,
                        section,
                        ex == null ? "(null)" : ex.ToString(),
                        ex != null && ex.InnerException != null ? "    inner: " + ex.InnerException : "");
                    File.AppendAllText(FilePath, line);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}