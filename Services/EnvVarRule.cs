using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;

namespace FC.Services
{
    /// <summary>规则可信度：Official=官方文档明确支持；Community=官方源码存在但未文档化。</summary>
    public enum RuleConfidence
    {
        Official,
        Community
    }

    /// <summary>环境变量值的写法：Direct=值直接是目录；OptsFlag=值形如 -Dxxx=目录（如 MAVEN_OPTS）。</summary>
    public enum EnvValueMode
    {
        Direct,
        OptsFlag
    }

    /// <summary>目录迁移方式（决定右键菜单提供什么）：</summary>
    public enum RedirectKind
    {
        /// <summary>支持环境变量重定位（env 优先 + junction 兜底）</summary>
        EnvVar,

        /// <summary>不支持环境变量，只能 junction 迁移</summary>
        JunctionOnly,

        /// <summary>不建议整体迁移（通常只是缓存/日志/残留），仅作归属标注</summary>
        Cleanable
    }

    /// <summary>
    /// 一条"软件数据目录识别规则"。两个用途：
    /// 1) 扫描时按 PathPattern 匹配，命中则在树行小字标注软件归属 + 环境变量提示（EnvTagText）。
    /// 2) 右键菜单据 RedirectKind 决定显示"通过环境变量迁移"（EnvVar）还是仅 junction（JunctionOnly）。
    /// 数据来源：内置（本类静态表）+ %APPDATA%\FC\envrules.xml 覆盖合并（同 Id 覆盖内置）。
    /// </summary>
    [XmlRoot("Rule")]
    public class EnvVarRule
    {
        /// <summary>唯一键，如 "vscode-extensions"。覆盖合并用。</summary>
        [XmlElement]
        public string Id { get; set; }

        /// <summary>软件显示名，如 "VS Code 扩展"</summary>
        [XmlElement]
        public string SoftwareName { get; set; }

        /// <summary>环境变量名（RedirectKind=EnvVar 时用）。</summary>
        [XmlElement(IsNullable = true)]
        public string EnvVarName { get; set; }

        /// <summary>
        /// 环境变量指向的路径，相对于迁移根目录的子路径（空=变量直接指迁移根目录）。
        /// 例：迁移根 %USERPROFILE%\.nuget，NUGET_PACKAGES 实际指 .nuget\packages，
        ///     则 EnvVarSubPath="packages"，迁移后变量值 = 新根\packages。
        /// 迁移单元始终是"软件根目录"（PathPattern 匹配的目录），不是子目录。
        /// </summary>
        [XmlElement(IsNullable = true)]
        public string EnvVarSubPath { get; set; }

        /// <summary>
        /// 目录路径模式，支持 %USERPROFILE%/%LOCALAPPDATA%/%APPDATA%/%PROGRAMDATA%/%USERPROFILE_DOC% 等占位。
        /// 匹配方式是"目标路径以该模式结尾"（大小写不敏感），允许规则匹配子层。
        /// 例："%USERPROFILE%\.vscode\extensions"
        /// </summary>
        [XmlElement]
        public string PathPattern { get; set; }

        /// <summary>迁移方式。</summary>
        [XmlElement]
        public RedirectKind Kind { get; set; }

        /// <summary>可信度。</summary>
        [XmlElement]
        public RuleConfidence Confidence { get; set; }

        /// <summary>环境变量值写法。</summary>
        [XmlElement]
        public EnvValueMode ValueMode { get; set; }

        /// <summary>需先结束的进程名（不含 .exe），逗号分隔。空=自动检测占用。</summary>
        [XmlElement(IsNullable = true)]
        public string KillHints { get; set; }

        /// <summary>依据链接（标注 tooltip 显示）。</summary>
        [XmlElement(IsNullable = true)]
        public string SourceUrl { get; set; }

        /// <summary>目录标签类型：Cache/Update/Data/Config/Log，仅标注用。</summary>
        [XmlElement(IsNullable = true)]
        public string Tag { get; set; }

        /// <summary>是否建议迁移到其他盘（仅标注提示）。</summary>
        [XmlElement]
        public bool RecommendMove { get; set; }

        /// <summary>禁止为本次迁移生成"重启后删除"脚本（默认 false = 允许）。</summary>
        [XmlElement]
        public bool DisableRestartDeleteScript { get; set; }

        public EnvVarRule()
        {
        }

        public EnvVarRule(string id, string softwareName, string envVarName, string pathPattern,
            RedirectKind kind, RuleConfidence confidence, EnvValueMode valueMode,
            string killHints, string sourceUrl, string tag, bool recommendMove)
        {
            Id = id;
            SoftwareName = softwareName;
            EnvVarName = envVarName;
            PathPattern = pathPattern;
            Kind = kind;
            Confidence = confidence;
            ValueMode = valueMode;
            KillHints = killHints;
            SourceUrl = sourceUrl;
            Tag = tag;
            RecommendMove = recommendMove;
        }

        /// <summary>
        /// 是否匹配给定目录路径。
        /// 1) 先用占位展开后的绝对路径做后缀匹配（%USERPROFILE% 等展开正确时最精确）；
        /// 2) 若失败，改用"去掉根占位后的相对尾部"匹配（如 \.vscode\extensions），
        ///    这样无论用户配置文件实际在哪、进程环境变量是否被改，只要目录结构一致就能命中。
        /// </summary>
        public bool Matches(string fullPath)
        {
            if (string.IsNullOrEmpty(PathPattern) || string.IsNullOrEmpty(fullPath))
            {
                return false;
            }

            // 1) 绝对路径后缀匹配
            string expanded = EnvVarRuleSet.ExpandPath(PathPattern);
            if (!string.IsNullOrEmpty(expanded) && PathUtil.EndsWithIgnoreCase(fullPath, expanded))
            {
                return true;
            }

            // 2) 相对尾部匹配：去掉开头的根占位（%XXX% 或盘符），只留 \a\b 形式
            string tail = EnvVarRuleSet.ToRelativeTail(PathPattern);
            if (!string.IsNullOrEmpty(tail) && PathUtil.EndsWithIgnoreCase(fullPath, tail))
            {
                return true;
            }
            return false;
        }

        /// <summary>迁移后应结束的进程名列表（不含 .exe，可能为空）。</summary>
        public IEnumerable<string> GetKillHints()
        {
            if (string.IsNullOrWhiteSpace(KillHints))
            {
                yield break;
            }
            foreach (var s in KillHints.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0)
                {
                    yield return t;
                }
            }
        }
    }

    /// <summary>集合级工具：占位展开、前缀日志等。</summary>
    public static class PathUtil
    {
        /// <summary>忽略大小写地判断 path 是否以 suffix 结尾（统一目录分隔符）。</summary>
        public static bool EndsWithIgnoreCase(string path, string suffix)
        {
            string p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string s = suffix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.EndsWith(s, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>去掉尾部分隔符（保留盘根 "C:\" 语义：长度为 3 且形如 X:\ 时保留）。</summary>
        public static string TrimSlash(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            string p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (p.Length == 2 && p[1] == ':')
            {
                return p + Path.DirectorySeparatorChar;
            }
            return p;
        }

        /// <summary>ancestor 是否是 child 的祖先或与 child 相同（前缀 + 分隔符边界）。用于多根覆盖判断。</summary>
        public static bool IsSameOrAncestorPath(string ancestor, string child)
        {
            string a = TrimSlash(ancestor);
            string c = TrimSlash(child);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(c))
            {
                return false;
            }
            if (string.Equals(a, c, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (c.Length < a.Length
                || !c.StartsWith(a, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            // 盘根（如 "C:\"）作为前缀即祖先
            if (a.Length >= 3 && a[1] == ':'
                && (a.EndsWith(Path.DirectorySeparatorChar.ToString()) || a.EndsWith(Path.AltDirectorySeparatorChar.ToString())))
            {
                return true;
            }
            // 普通目录：a 之后的下个字符必须是分隔符
            char next = c[a.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }
    }

    /// <summary>
    /// 规则集合：内置静态表 + 外部 envrules.xml 覆盖合并。
    /// 用法：EnvVarRuleSet.Load() 返回合并后的规则列表（懒加载 + 缓存）。
    /// </summary>
    public static class EnvVarRuleSet
    {
        private static List<EnvVarRule> _merged;
        private static readonly object Gate = new object();

        /// <summary>外部覆盖文件默认位置。</summary>
        public static string ExternalPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FC", "envrules.xml");
            }
        }

        /// <summary>加载合并规则（内置 + 外部覆盖）。线程安全、懒加载。</summary>
        public static List<EnvVarRule> Load()
        {
            lock (Gate)
            {
                if (_merged != null)
                {
                    return _merged;
                }
                var list = new List<EnvVarRule>(BuildBuiltin());
                MergeExternal(list);
                _merged = list;
                return _merged;
            }
        }

        /// <summary>清缓存（供测试/外部更新后调用）。</summary>
        public static void ResetCache()
        {
            lock (Gate)
            {
                _merged = null;
            }
        }

        /// <summary>查找第一个匹配给定路径的规则（无则 null）。</summary>
        public static EnvVarRule Match(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return null;
            }
            var rules = Load();
            foreach (var r in rules)
            {
                if (r.Matches(fullPath))
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>展开路径模式里的已知 %变量%。未识别/不存在的变量保持原样（匹配将失败）。</summary>
        public static string ExpandPath(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return pattern;
            }
            string s = pattern;
            s = s.Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            s = s.Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            s = s.Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            s = s.Replace("%PROGRAMDATA%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            s = s.Replace("%USERPROFILE_DOC%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            s = s.Replace("%WINDIR%", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            s = s.Replace("%TEMP%", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return s;
        }

        /// <summary>
        /// 去掉路径模式开头的根占位（%XXX% 或盘符，如 C:\），只保留相对尾部（如 \.vscode\extensions）。
        /// 用于"路径结构匹配"，即使进程环境变量/用户目录与实际不符也能命中。
        /// </summary>
        public static string ToRelativeTail(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return pattern;
            }
            string s = pattern.Trim();
            // 去掉已知占位前缀
            foreach (var token in new[] { "%USERPROFILE%", "%LOCALAPPDATA%", "%APPDATA%", "%PROGRAMDATA%", "%USERPROFILE_DOC%", "%WINDIR%", "%TEMP%" })
            {
                if (s.IndexOf(token, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    s = s.Substring(token.Length);
                    break;
                }
            }
            // 去掉盘符前缀（如 C:\）
            if (s.Length >= 3 && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
            {
                s = s.Substring(2);
            }
            // 规范化分隔符并保证以 \ 开头
            s = s.Replace('/', Path.DirectorySeparatorChar);
            s = s.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.DirectorySeparatorChar + s;
        }

        /// <summary>外部 envrules.xml 存在则按 Id 合并覆盖内置规则。</summary>
        private static void MergeExternal(List<EnvVarRule> list)
        {
            try
            {
                if (!File.Exists(ExternalPath))
                {
                    return;
                }
                var ser = new XmlSerializer(typeof(EnvVarRuleCollection));
                EnvVarRuleCollection coll;
                using (var fs = File.OpenRead(ExternalPath))
                {
                    coll = ser.Deserialize(fs) as EnvVarRuleCollection;
                }
                if (coll == null || coll.Items == null)
                {
                    return;
                }
                foreach (var ext in coll.Items)
                {
                    if (string.IsNullOrEmpty(ext.Id))
                    {
                        continue;
                    }
                    int idx = list.FindIndex(r => string.Equals(r.Id, ext.Id, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        list[idx] = ext; // 覆盖内置
                    }
                    else
                    {
                        list.Add(ext); // 新增
                    }
                }
            }
            catch (Exception)
            {
                // 外部文件损坏/被占用：忽略，用内置
            }
        }

        /// <summary>内置规则集（核心数据，基于联网调研 + 官方文档/源码）。</summary>
        private static List<EnvVarRule> BuildBuiltin()
        {
            var L = new List<EnvVarRule>();

            // ============ A. 官方/源码确认支持环境变量重定位 ============
            L.Add(new EnvVarRule("vscode-extensions", "VS Code 扩展",
                "VSCODE_EXTENSIONS", "%USERPROFILE%\\.vscode\\extensions",
                RedirectKind.EnvVar, RuleConfidence.Community, EnvValueMode.Direct,
                "Code,Code - Insiders",
                "https://github.com/microsoft/vscode/blob/main/src/vs/platform/environment/common/environmentService.ts",
                "Data", true));

            L.Add(new EnvVarRule("vscode-insiders-extensions", "VS Code Insiders 扩展",
                "VSCODE_EXTENSIONS", "%USERPROFILE%\\.vscode-insiders\\extensions",
                RedirectKind.EnvVar, RuleConfidence.Community, EnvValueMode.Direct,
                "Code - Insiders",
                "https://github.com/microsoft/vscode/blob/main/src/vs/platform/environment/common/environmentService.ts",
                "Data", true));

            L.Add(new EnvVarRule("vscode-portable", "VS Code 便携模式数据",
                "VSCODE_PORTABLE", "%USERPROFILE%\\.vscode-portable",
                RedirectKind.EnvVar, RuleConfidence.Community, EnvValueMode.Direct,
                "Code,Code - Insiders",
                "https://code.visualstudio.com/docs/setup/portable",
                "Data", true));

            L.Add(new EnvVarRule("npm-cache", "npm 包缓存",
                "npm_config_cache", "%APPDATA%\\npm-cache",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "node,nodejs,npm",
                "https://learn.microsoft.com/en-us/windows/dev-drive/",
                "Cache", true));

            L.Add(new EnvVarRule("pip-cache", "pip 包缓存",
                "PIP_CACHE_DIR", "%LOCALAPPDATA%\\pip\\Cache",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "python,pythonw",
                "https://pip.pypa.io/en/stable/topics/caching/",
                "Cache", true));

            L.Add(new EnvVarRule("vcpkg-binary", "vcpkg 二进制包缓存",
                "VCPKG_DEFAULT_BINARY_CACHE", "%LOCALAPPDATA%\\vcpkg\\archives",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "vcpkg",
                "https://learn.microsoft.com/en-us/vcpkg/users/config-environment",
                "Cache", true));

            L.Add(new EnvVarRule("vcpkg-root", "vcpkg 根目录",
                "VCPKG_ROOT", "%LOCALAPPDATA%\\vcpkg",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "vcpkg",
                "https://learn.microsoft.com/en-us/vcpkg/users/config-environment",
                "Cache", true));

            L.Add(new EnvVarRule("cargo-home", "Rust Cargo 缓存",
                "CARGO_HOME", "%USERPROFILE%\\.cargo",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "cargo,rustup",
                "https://doc.rust-lang.org/cargo/guide/cargo-home.html",
                "Cache", true));

            L.Add(new EnvVarRule("rustup-home", "Rustup 工具链",
                "RUSTUP_HOME", "%USERPROFILE%\\.rustup",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "rustup,cargo",
                "https://rust-lang.github.io/rustup/environment-variables.html",
                "Cache", true));

            L.Add(new EnvVarRule("gradle-home", "Gradle 用户目录",
                "GRADLE_USER_HOME", "%USERPROFILE%\\.gradle",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "gradle",
                "https://docs.gradle.org/current/userguide/build_environment.html",
                "Cache", true));

            L.Add(new EnvVarRule("nuget-packages", "NuGet 全局包",
                "NUGET_PACKAGES", "%USERPROFILE%\\.nuget\\packages",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "dotnet,msbuild",
                "https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders",
                "Cache", true));

            L.Add(new EnvVarRule("nuget-httpcache", "NuGet HTTP 缓存",
                "NUGET_HTTP_CACHE_PATH", "%LOCALAPPDATA%\\NuGet\\v3-cache",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "dotnet,msbuild",
                "https://learn.microsoft.com/en-us/nuget/reference/cli-reference/cli-ref-environment-variables",
                "Cache", true));

            L.Add(new EnvVarRule("go-gopath", "Go 工作区",
                "GOPATH", "%USERPROFILE%\\go",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "go",
                "https://go.dev/wiki/GOPATH",
                "Cache", true));

            L.Add(new EnvVarRule("go-modcache", "Go 模块缓存",
                "GOMODCACHE", "%USERPROFILE%\\go\\pkg\\mod",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "go",
                "https://go.dev/ref/mod#module-cache",
                "Cache", true));

            L.Add(new EnvVarRule("go-cache", "Go 构建缓存",
                "GOCACHE", "%LOCALAPPDATA%\\go-build",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "go",
                "https://go.dev/cmd/go/",
                "Cache", true));

            L.Add(new EnvVarRule("dotnet-cli", ".NET CLI 用户数据",
                "DOTNET_CLI_HOME", "%USERPROFILE%\\.dotnet",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "dotnet",
                "https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables",
                "Cache", true));

            L.Add(new EnvVarRule("android-userhome", "Android 用户数据",
                "ANDROID_USER_HOME", "%USERPROFILE%\\.android",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "adb,emulator",
                "https://developer.android.com/tools/variables",
                "Data", true));

            L.Add(new EnvVarRule("android-avd", "Android AVD 磁盘镜像",
                "ANDROID_AVD_HOME", "%USERPROFILE%\\.android\\avd",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "emulator,qemu-system-x86_64",
                "https://developer.android.com/tools/variables",
                "Data", true));

            L.Add(new EnvVarRule("clojure-config", "Clojure CLI 配置",
                "CLJ_CONFIG", "%USERPROFILE%\\.clojure",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "clojure,clj",
                "https://clojure.org/reference/clojure_cli",
                "Config", true));

            L.Add(new EnvVarRule("coursier-cache", "coursier 依赖缓存",
                "COURSIER_CACHE", "%LOCALAPPDATA%\\Coursier\\Cache",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "cs,sbt,scala",
                "https://get-coursier.io/docs/cache.html",
                "Cache", true));

            L.Add(new EnvVarRule("konan-data", "Kotlin/Native 数据目录",
                "KONAN_DATA_DIR", "%USERPROFILE%\\.konan",
                RedirectKind.EnvVar, RuleConfidence.Community, EnvValueMode.Direct,
                "kotlinc,gradle",
                "https://android.googlesource.com/platform/external/jetbrains/kotlin/",
                "Cache", true));

            L.Add(new EnvVarRule("maven-repo", "Maven 本地仓库",
                "MAVEN_OPTS", "%USERPROFILE%\\.m2\\repository",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.OptsFlag,
                "mvn,mvnw",
                "https://maven.apache.org/settings.html",
                "Cache", true));

            L.Add(new EnvVarRule("temp-user", "用户临时目录",
                "TEMP", "%LOCALAPPDATA%\\Temp",
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct,
                "",
                "https://learn.microsoft.com/en-us/answers/questions/2421592/change-location-of-temp-files-folder-to-another-dr",
                "Cache", true));

            // ============ B. 不支持环境变量，仅 junction 迁移 ============
            L.Add(new EnvVarRule("chrome-userdata", "Google Chrome 用户数据",
                null, "%LOCALAPPDATA%\\Google\\Chrome\\User Data",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "chrome,msedge",
                "https://chromium.googlesource.com/chromium/src/+/HEAD/docs/user_data_dir.md",
                "Data", true));

            L.Add(new EnvVarRule("edge-userdata", "Microsoft Edge 用户数据",
                null, "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "msedge",
                "https://learn.microsoft.com/en-us/deployedge/edge-learnmore-create-user-directory-vars",
                "Data", true));

            L.Add(new EnvVarRule("firefox-profiles", "Firefox 配置",
                null, "%APPDATA%\\Mozilla\\Firefox\\Profiles",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "firefox",
                "https://kb.mozillazine.org/Profile_folder_-_Firefox",
                "Data", true));

            L.Add(new EnvVarRule("wechat-files", "微信聊天记录",
                null, "%USERPROFILE_DOC%\\WeChat Files",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "wechat,WeChat",
                "https://www.cnblogs.com/pcdoctor/p/20306673",
                "Data", true));

            L.Add(new EnvVarRule("wechat-xwechat", "微信 4.x 数据目录",
                null, "%USERPROFILE_DOC%\\xwechat_files",
                RedirectKind.JunctionOnly, RuleConfidence.Community, EnvValueMode.Direct,
                "wechat,WeChat",
                "https://www.cnblogs.com/pcdoctor/p/20306673",
                "Data", true));

            L.Add(new EnvVarRule("wechat-roaming", "微信缓存/日志",
                null, "%APPDATA%\\Tencent\\WeChat",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "wechat,WeChat",
                "https://cleaneronecn.trendmicro.com/blog/how-to-delete-wechat-cache/",
                "Log", true));

            L.Add(new EnvVarRule("qq-files", "QQ 聊天记录",
                null, "%USERPROFILE_DOC%\\Tencent Files",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "QQ",
                "https://www.160.com/article/8130.html",
                "Data", true));

            L.Add(new EnvVarRule("wxwork-cache", "企业微信数据",
                null, "%USERPROFILE_DOC%\\WXWork",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "WXWork,WeCom",
                "https://open.work.weixin.qq.com/help2/pc/13352",
                "Data", true));

            L.Add(new EnvVarRule("dingtalk-cache", "钉钉缓存",
                null, "%LOCALAPPDATA%\\DingTalk",
                RedirectKind.JunctionOnly, RuleConfidence.Community, EnvValueMode.Direct,
                "DingTalk",
                "https://www.zhihu.com/question/379375357",
                "Cache", true));

            L.Add(new EnvVarRule("netease-cache", "网易云音乐缓存",
                null, "%LOCALAPPDATA%\\Netease\\CloudMusic\\Cache",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "cloudmusic",
                "https://www.zhihu.com/question/40678992",
                "Cache", true));

            L.Add(new EnvVarRule("baidu-netdisk", "百度网盘缓存",
                null, "%LOCALAPPDATA%\\Baidu\\BaiduNetdisk",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "BaiduNetdisk",
                "https://blog.csdn.net/geekvc/article/details/51437110",
                "Cache", true));

            L.Add(new EnvVarRule("jetbrains-system", "JetBrains IDE 系统目录",
                null, "%LOCALAPPDATA%\\JetBrains",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "idea,pycharm,webstorm,clion,rider,phpstorm,goland,datagrip,studio64",
                "https://www.jetbrains.com/help/idea/directories-used-by-the-ide-to-store-settings-caches-plugins-and-logs.html",
                "Cache", true));

            L.Add(new EnvVarRule("adobe-mediacache", "Adobe 媒体缓存",
                null, "%LOCALAPPDATA%\\Adobe\\Media Cache",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "Adobe Premiere Pro,Photoshop,AfterFX",
                "https://helpx.adobe.com/premiere/desktop/troubleshooting/media-issues/automatically-manage-your-media-cache-files.html",
                "Cache", true));

            L.Add(new EnvVarRule("zoom-cache", "Zoom 缓存",
                null, "%APPDATA%\\Zoom\\data",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "zoom",
                "https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0058835",
                "Cache", true));

            L.Add(new EnvVarRule("slack-cache", "Slack 缓存",
                null, "%APPDATA%\\Slack\\Cache",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "slack",
                "https://www.suptask.com/blog/clear-slack-cache",
                "Cache", true));

            L.Add(new EnvVarRule("teams-cache", "Microsoft Teams 缓存",
                null, "%APPDATA%\\Microsoft\\Teams",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "teams,ms-teams",
                "https://learn.microsoft.com/en-us/troubleshoot/microsoftteams/teams-administration/clear-teams-cache",
                "Cache", true));

            L.Add(new EnvVarRule("outlook-ost", "Outlook 邮件数据",
                null, "%LOCALAPPDATA%\\Microsoft\\Outlook",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "outlook",
                "https://learn.microsoft.com/en-us/answers/questions/594758/path-of-ost-and-pst-files-of-microsoft-outlook-in",
                "Data", true));

            L.Add(new EnvVarRule("onedrive", "OneDrive 同步目录",
                null, "%USERPROFILE%\\OneDrive",
                RedirectKind.JunctionOnly, RuleConfidence.Official, EnvValueMode.Direct,
                "OneDrive",
                "https://support.microsoft.com/en-us/onedrive/change-the-location-of-your-onedrive-folder",
                "Data", true));

            // ============ C. 不建议整体迁移，仅归属标注（Cleanable） ============
            L.Add(new EnvVarRule("vs-designer-cache", "Visual Studio Designer 缓存",
                null, "%LOCALAPPDATA%\\Microsoft\\VisualStudio",
                RedirectKind.Cleanable, RuleConfidence.Official, EnvValueMode.Direct,
                "devenv,VS",
                "https://developercommunity.visualstudio.com/t/Visual-studio-2022-keep-too-many-designe/10105903",
                "Cache", false));

            L.Add(new EnvVarRule("windows-update", "Windows 更新缓存",
                null, "%WINDIR%\\SoftwareDistribution",
                RedirectKind.Cleanable, RuleConfidence.Official, EnvValueMode.Direct,
                "",
                "https://maketecheasier.com/delete-windows-10-update-cache/",
                "Update", false));

            L.Add(new EnvVarRule("vs-package-cache", "Visual Studio 安装包缓存",
                null, "%PROGRAMDATA%\\Package Cache",
                RedirectKind.Cleanable, RuleConfidence.Official, EnvValueMode.Direct,
                "",
                "https://learn.microsoft.com/en-us/visualstudio/install/disable-or-move-the-package-cache",
                "Update", false));

            L.Add(new EnvVarRule("nvidia-downloader", "NVIDIA 驱动下载缓存",
                null, "%PROGRAMDATA%\\NVIDIA Corporation\\Downloader",
                RedirectKind.Cleanable, RuleConfidence.Official, EnvValueMode.Direct,
                "",
                "https://www.nvidia.com/en-us/geforce/forums/geforce-experience/14/151126/",
                "Update", false));

            L.Add(new EnvVarRule("node_modules", "node_modules 项目依赖",
                null, "\\node_modules",
                RedirectKind.Cleanable, RuleConfidence.Official, EnvValueMode.Direct,
                "",
                "https://itnext.io/how-i-cleaned-up-my-hard-drive-from-over-50-gbs-of-npm-dependencies-5d2d7d2ad476",
                "Data", false));

            return L;
        }
    }

    /// <summary>外部 envrules.xml 的顶层包装（XmlSerializer 需集合包装类）。</summary>
    [XmlRoot("EnvRules")]
    public class EnvVarRuleCollection
    {
        [XmlElement("Rule")]
        public List<EnvVarRule> Items { get; set; }

        public EnvVarRuleCollection()
        {
            Items = new List<EnvVarRule>();
        }
    }
}