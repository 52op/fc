using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace FC.Services
{
    /// <summary>
    /// 环境变量规则数据的远程更新。
    /// - 更新 URL：App.config 的 RulesUpdateUrl（默认空=不更新）。
    /// - 下载远程 envrules.xml → 覆盖 %APPDATA%\FC\envrules.xml → 重置 EnvVarRuleSet 缓存。
    /// - 校验远程内容为合法规则集，避免坏数据污染本地。
    /// 静默：启动时自动尝试一次，失败绝不影响启动。
    /// </summary>
    public static class EnvRulesUpdater
    {
        private const int TimeoutSec = 15;

        /// <summary>启动时静默尝试更新（Fire-and-forget，不阻塞启动）。</summary>
        public static void TrySilentUpdate()
        {
            string url = GetConfiguredUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                return; // 未配置 URL，不更新
            }
            Task.Run(async () =>
            {
                try
                {
                    await DownloadAsync(url, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 静默失败
                }
            });
        }

        /// <summary>手动更新（返回用户可读结果）。</summary>
        public static async Task<string> UpdateAsync()
        {
            string url = GetConfiguredUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                return "未配置规则更新地址（可在“帮助 → 更新设置”里填写）。";
            }
            try
            {
                bool ok = await DownloadAsync(url, CancellationToken.None);
                return ok ? "规则数据已更新。" : "更新失败：下载内容无效。";
            }
            catch (Exception ex)
            {
                return "更新失败：" + ex.Message;
            }
        }

        /// <summary>配置的更新 URL：优先取用户设置（%APPDATA%\FC\settings.xml），回落 App.config 的 RulesUpdateUrl。</summary>
        public static string GetConfiguredUrl()
        {
            try
            {
                var s = FC.Themes.ThemeManager.CurrentSettings;
                if (s != null && !string.IsNullOrWhiteSpace(s.RulesUpdateUrl))
                {
                    return s.RulesUpdateUrl;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                return System.Configuration.ConfigurationManager.AppSettings["RulesUpdateUrl"];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>下载并校验覆盖本地规则文件。返回是否成功。</summary>
        private static async Task<bool> DownloadAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "FC-DiskTool/1.0";
                ct.ThrowIfCancellationRequested();
                string xml = await client.DownloadStringTaskAsync(new Uri(url));
                if (string.IsNullOrWhiteSpace(xml))
                {
                    return false;
                }
                // 校验是合法规则集
                var coll = TryParse(xml);
                if (coll == null)
                {
                    return false;
                }
                string dir = Path.GetDirectoryName(EnvVarRuleSet.ExternalPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string tmp = EnvVarRuleSet.ExternalPath + ".tmp";
                File.WriteAllText(tmp, xml);
                File.Copy(tmp, EnvVarRuleSet.ExternalPath, true);
                File.Delete(tmp);
                EnvVarRuleSet.ResetCache();
                return true;
            }
        }

        private static EnvVarRuleCollection TryParse(string xml)
        {
            try
            {
                var ser = new XmlSerializer(typeof(EnvVarRuleCollection));
                using (var sr = new StringReader(xml))
                {
                    var c = ser.Deserialize(sr) as EnvVarRuleCollection;
                    // 至少一条规则才算有效
                    return (c != null && c.Items != null && c.Items.Count > 0) ? c : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}