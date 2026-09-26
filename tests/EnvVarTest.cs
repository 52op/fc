using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FC.Models;
using FC.Services;

class EnvVarTest
{
    private static int Main()
    {
        try
        {
            // ===== 1. 规则匹配 =====
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string vscode = Path.Combine(home, ".vscode", "extensions");
            var m = EnvVarRuleSet.Match(vscode);
            if (m == null || m.Id != "vscode-extensions" || m.EnvVarName != "VSCODE_EXTENSIONS")
            {
                Console.WriteLine("FAIL match vscode: " + (m == null ? "null" : m.Id));
                return 1;
            }
            Console.WriteLine("OK match: " + m.Id + " -> " + m.EnvVarName + " (" + m.Confidence + ")");

            // 不匹配的路径
            if (EnvVarRuleSet.Match(Path.Combine(home, "some-random-folder-xyz")) != null)
            {
                Console.WriteLine("FAIL match: random path should not match");
                return 1;
            }

            // 相对尾部匹配：即使目录不在 %USERPROFILE% 下（结构相同）也应命中
            string alt = Path.Combine("D:", "backup", ".vscode", "extensions");
            var altMatch = EnvVarRuleSet.Match(alt);
            if (altMatch == null || altMatch.Id != "vscode-extensions")
            {
                Console.WriteLine("FAIL tail: alt-dir .vscode\\extensions should match by structure, got=" + (altMatch == null ? "null" : altMatch.Id));
                return 1;
            }
            Console.WriteLine("OK tail: alt-dir structure match works");
            string altGradle = Path.Combine("E:", "data", ".gradle");
            var ag = EnvVarRuleSet.Match(altGradle);
            if (ag == null || ag.Id != "gradle-home")
            {
                Console.WriteLine("FAIL tail: alt-dir .gradle should match, got=" + (ag == null ? "null" : ag.Id));
                return 1;
            }
            Console.WriteLine("OK tail: alt-dir .gradle match works");

            // ===== 2. 匹配层级（标注只落在规则目录本身，不下渗插件子目录）=====
            // 插件级子目录：直接 Match 必须返回 null（UI 标注不会显示在插件目录上）
            string leaf = Path.Combine(vscode, "some.extension-1.0.0", "out");
            string pluginDir = Path.Combine(vscode, "some.extension-1.0.0");
            if (EnvVarRuleSet.Match(pluginDir) != null)
            {
                Console.WriteLine("FAIL: plugin subdir should NOT direct-match");
                return 1;
            }
            if (EnvVarRuleSet.Match(leaf) != null)
            {
                Console.WriteLine("FAIL: deep leaf should NOT direct-match");
                return 1;
            }
            Console.WriteLine("OK level: plugin/deep subdirs do not match (tag only on rule dir itself)");

            // 向上归并工具（用于迁移单元定位，仍可用）
            var mm = EnvVarMatcher.FindMatch(leaf);
            if (mm == null || mm.Rule.Id != "vscode-extensions")
            {
                Console.WriteLine("FAIL upward: leaf did not resolve to ancestor rule");
                return 1;
            }
            if (!string.Equals(mm.MatchedPath.TrimEnd('\\'), vscode.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("FAIL upward: matched path=" + mm.MatchedPath);
                return 1;
            }
            Console.WriteLine("OK upward: " + leaf + " -> " + mm.MatchedPath);

            // ===== 3. 标注文本 =====
            string tag = EnvVarMatcher.BuildTagText(m);
            if (string.IsNullOrEmpty(tag) || tag.IndexOf("VSCODE_EXTENSIONS") < 0)
            {
                Console.WriteLine("FAIL tag: " + tag);
                return 1;
            }
            Console.WriteLine("OK tag: " + tag);

            // 内置规则数量
            var rules = EnvVarRuleSet.Load();
            if (rules.Count < 30)
            {
                Console.WriteLine("FAIL rules count too low: " + rules.Count);
                return 1;
            }
            int envCount = 0;
            foreach (var r in rules) { if (r.Kind == RedirectKind.EnvVar) envCount++; }
            Console.WriteLine("OK rules: total=" + rules.Count + " envvar=" + envCount);

            // ===== 4. 环境变量读写（用户级，独特名称）=====
            string varName = "FC_TEST_VAR_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string val1 = Path.Combine(Path.GetTempPath(), "fcenv1");
            string err;
            if (!EnvVarManager.Set(varName, val1, EnvVarManager.Scope.User, out err))
            {
                Console.WriteLine("FAIL env set: " + err);
                return 1;
            }
            string got = EnvVarManager.Get(varName, EnvVarManager.Scope.User);
            if (got != val1)
            {
                Console.WriteLine("FAIL env get: got=" + got + " expect=" + val1);
                return 1;
            }
            string val2 = Path.Combine(Path.GetTempPath(), "fcenv2");
            EnvVarManager.Set(varName, val2, EnvVarManager.Scope.User, out err);
            if (EnvVarManager.Get(varName, EnvVarManager.Scope.User) != val2)
            {
                Console.WriteLine("FAIL env update");
                return 1;
            }
            EnvVarManager.Remove(varName, EnvVarManager.Scope.User, out err);
            if (EnvVarManager.Get(varName, EnvVarManager.Scope.User) != null)
            {
                Console.WriteLine("FAIL env remove");
                return 1;
            }
            Console.WriteLine("OK env manager: set/get/update/remove");

            // ===== 5. 迁移记录序列化（含新字段）=====
            var rec = new MigrationRecord
            {
                SourcePath = @"C:\src\x",
                DestPath = @"D:\dst\x",
                MigrationKind = MigrationKind.EnvVar,
                EnvVarName = "VSCODE_EXTENSIONS",
                EnvVarScope = "User",
                EnvVarOldValue = null,
                EnvVarNewValue = @"D:\dst\x",
                SoftwareName = "VS Code 扩展",
                Status = MigrationStatus.Active
            };
            string recFile = Path.Combine(Path.GetTempPath(), "fc_rec_" + Guid.NewGuid().ToString("N") + ".xml");
            var store = new XmlRecordStore(recFile);
            store.Upsert(rec);
            var loaded = store.Load();
            if (loaded.Count != 1 || loaded[0].MigrationKind != MigrationKind.EnvVar
                || loaded[0].EnvVarName != "VSCODE_EXTENSIONS")
            {
                Console.WriteLine("FAIL record roundtrip");
                return 1;
            }
            File.Delete(recFile);
            Console.WriteLine("OK record: EnvVar fields roundtrip");

            // ===== 6. EnvVarMigrator 端到端 =====
            string srcRoot = Path.Combine(Path.GetTempPath(), "fc_src_" + Guid.NewGuid().ToString("N"));
            string dstRoot = Path.Combine(Path.GetTempPath(), "fc_dst_" + Guid.NewGuid().ToString("N"));
            string srcRoot2 = Path.Combine(Path.GetTempPath(), "fc_src2_" + Guid.NewGuid().ToString("N"));
            string dstRoot2 = Path.Combine(Path.GetTempPath(), "fc_dst2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(srcRoot, "sub"));
            File.WriteAllBytes(Path.Combine(srcRoot, "a.bin"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(srcRoot, "sub", "b.bin"), new byte[3000]);

            string migVar = "FC_TEST_MIGVAR_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var rule = new EnvVarRule("test-rule", "Test Software", migVar, srcRoot,
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct, "", null, "Cache", true);

            var migrator = new EnvVarMigrator(new TreeVerifier(), store);
            string verr = migrator.Validate(srcRoot, dstRoot);
            if (verr != null)
            {
                Console.WriteLine("FAIL migrator validate: " + verr);
                return 1;
            }

            var res = migrator.MigrateAsync(rule, srcRoot, dstRoot, EnvVarManager.Scope.User,
                false /* 不留 junction，验证源目录被删除 */, null, CancellationToken.None).Result;

            if (!res.Success)
            {
                Console.WriteLine("FAIL migrator: " + res.Message);
                return 1;
            }
            // 数据到达目标
            if (!File.Exists(Path.Combine(dstRoot, "a.bin")) || !File.Exists(Path.Combine(dstRoot, "sub", "b.bin")))
            {
                Console.WriteLine("FAIL migrator: dst missing files");
                return 1;
            }
            // 环境变量指向目标
            if (EnvVarManager.Get(migVar, EnvVarManager.Scope.User) != dstRoot)
            {
                Console.WriteLine("FAIL migrator: env not pointing to dest");
                return 1;
            }
            // 源目录被删除（未留 junction）
            if (Directory.Exists(srcRoot))
            {
                Console.WriteLine("FAIL migrator: source should be deleted");
                return 1;
            }
            Console.WriteLine("OK migrator: e2e migrate OK (files moved, env set, source removed)");

            // ===== 7. 还原流程（env 迁移反向）=====
            // 重建源目录并迁移（这次留 junction 兜底），再还原
            Directory.CreateDirectory(Path.Combine(srcRoot2, "sub2"));
            File.WriteAllBytes(Path.Combine(srcRoot2, "c.bin"), new byte[7000]);

            string migVar2 = "FC_TEST_MIGVAR2_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var rule2 = new EnvVarRule("test-rule2", "Test Software 2", migVar2, srcRoot2,
                RedirectKind.EnvVar, RuleConfidence.Official, EnvValueMode.Direct, "", null, "Cache", true);

            var res2 = migrator.MigrateAsync(rule2, srcRoot2, dstRoot2, EnvVarManager.Scope.User,
                true /* 留 junction */, null, CancellationToken.None).Result;
            if (!res2.Success)
            {
                Console.WriteLine("FAIL migrator2: " + res2.Message);
                return 1;
            }
            // 源位置应存在 junction
            if (!JunctionUtil.IsReparsePoint(srcRoot2))
            {
                Console.WriteLine("FAIL migrator2: junction fallback not created");
                return 1;
            }
            Console.WriteLine("OK migrator: junction fallback created");

            // 记录 + 还原
            var rec2 = new MigrationRecord
            {
                SourcePath = srcRoot2,
                DestPath = dstRoot2,
                MigrationKind = MigrationKind.EnvVar,
                EnvVarName = migVar2,
                EnvVarScope = "User",
                EnvVarOldValue = null,
                EnvVarNewValue = dstRoot2,
                Status = MigrationStatus.Active
            };
            store.Upsert(rec2);

            var restoreRes = migrator.RestoreAsync(rec2, migVar2, EnvVarManager.Scope.User, null,
                null, CancellationToken.None).Result;
            if (!restoreRes.Success)
            {
                Console.WriteLine("FAIL restore: " + restoreRes.Message);
                return 1;
            }
            // 数据回到源位置
            if (!File.Exists(Path.Combine(srcRoot2, "c.bin")) || !Directory.Exists(Path.Combine(srcRoot2, "sub2")))
            {
                Console.WriteLine("FAIL restore: data not back at source");
                return 1;
            }
            // 环境变量已删除（旧值为 null）
            if (EnvVarManager.Get(migVar2, EnvVarManager.Scope.User) != null)
            {
                Console.WriteLine("FAIL restore: env var not removed");
                return 1;
            }
            // 目标副本清理
            if (Directory.Exists(dstRoot2))
            {
                Console.WriteLine("FAIL restore: dest not cleaned");
                return 1;
            }
            Console.WriteLine("OK restore: e2e restore OK (data back, env removed, dest cleaned)");

            // 清理
            try { Directory.Delete(srcRoot2, true); } catch (Exception) { }
            try { Directory.Delete(dstRoot2, true); } catch (Exception) { }

            // 清理迁移记录
            var all = store.Load();
            all.Clear();
            store.SaveAll(all);

            Console.WriteLine("ALL ENVVAR TESTS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL exception: " + ex.Message + "\n" + ex.StackTrace);
            return 1;
        }
    }
}