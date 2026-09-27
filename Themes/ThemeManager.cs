using System;
using System.IO;
using System.Windows;
using System.Xml.Serialization;

namespace FC.Themes
{
    /// <summary>应用设置（主题/单位），XmlSerializer 存 %APPDATA%\FC\settings.xml</summary>
    [XmlRoot("Settings")]
    public class AppSettings
    {
        [XmlElement]
        public bool IsDarkTheme { get; set; }

        [XmlElement]
        public string UnitMode { get; set; }

        [XmlElement]
        public bool ShowSizeColumn { get; set; }

        [XmlElement]
        public bool ShowAllocatedColumn { get; set; }

        [XmlElement]
        public bool ShowFilesColumn { get; set; }

        [XmlElement]
        public bool ShowFoldersColumn { get; set; }

        [XmlElement]
        public bool ShowPercentColumn { get; set; }

        [XmlElement]
        public bool QuickReuse { get; set; }

        /// <summary>环境变量规则数据更新地址（用户在"帮助→更新设置"里填；空=用 App.config 的 RulesUpdateUrl）。</summary>
        [XmlElement(IsNullable = true)]
        public string RulesUpdateUrl { get; set; }

        /// <summary>同时保留的扫描根数量上限（多根并列，旧根折叠）。默认 4。</summary>
        [XmlElement]
        public int MaxRoots { get; set; }

        public AppSettings()
        {
            IsDarkTheme = false;
            UnitMode = "自动";
            ShowSizeColumn = true;
            ShowAllocatedColumn = true;
            ShowFilesColumn = true;
            ShowFoldersColumn = true;
            ShowPercentColumn = true;
            QuickReuse = false;
            RulesUpdateUrl = null;
            MaxRoots = 4;
        }
    }

    /// <summary>切换深浅主题：替换 App 资源里第一份颜色字典，并持久化设置。</summary>
    public static class ThemeManager
    {
        private const string SettingsDir = "FC";
        private static string _settingsFile;

        public static bool IsDark { get; private set; }

        public static AppSettings CurrentSettings { get; private set; }

        private static string SettingsFilePath
        {
            get
            {
                if (_settingsFile == null)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SettingsDir);
                    _settingsFile = Path.Combine(dir, "settings.xml");
                }
                return _settingsFile;
            }
        }

        /// <summary>启动时调用：读设置并应用主题。</summary>
        public static void LoadAndApply()
        {
            CurrentSettings = LoadSettings();
            Apply(CurrentSettings.IsDarkTheme);
            IsDark = CurrentSettings.IsDarkTheme;
        }

        public static void Toggle()
        {
            Apply(!IsDark);
            SaveSettings();
        }

        public static void Apply(bool dark)
        {
            IsDark = dark;
            if (CurrentSettings != null)
            {
                CurrentSettings.IsDarkTheme = dark;
            }

            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            // 第一份合并字典固定是颜色字典（App.xaml 里顺序保证）
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 0)
            {
                merged[0] = new ResourceDictionary
                {
                    Source = new Uri("Themes/Colors." + (dark ? "Dark" : "Light") + ".xaml", UriKind.Relative)
                };
            }
        }

        public static void SaveSettings()
        {
            try
            {
                if (CurrentSettings == null)
                {
                    return;
                }
                string dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var ser = new XmlSerializer(typeof(AppSettings));
                using (var xw = System.Xml.XmlWriter.Create(SettingsFilePath,
                    new System.Xml.XmlWriterSettings { Indent = true }))
                {
                    var ns = new XmlSerializerNamespaces();
                    ns.Add(string.Empty, string.Empty);
                    ser.Serialize(xw, CurrentSettings, ns);
                }
            }
            catch (Exception)
            {
                // 设置写失败不影响运行
            }
        }

        private static AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return new AppSettings();
                }
                var ser = new XmlSerializer(typeof(AppSettings));
                using (var fs = File.OpenRead(SettingsFilePath))
                {
                    return (AppSettings)ser.Deserialize(fs);
                }
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }
    }
}