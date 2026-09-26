using System;
using System.IO;
using System.Threading;
using FC.Models;
using FC.Services;
using FC.ViewModels;

class EnvUiTest
{
    [STAThread]
    private static int Main()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string extr = Path.Combine(home, ".vscode", "extensions");
        string plugin = Path.Combine(extr, "some.extension-1.0.0");

        var services = AppServices.CreateDefault();

        // 节点：.vscode\extensions
        var node = new DiskNode
        {
            FullPath = extr,
            Name = "extensions",
            IsFolder = true,
            ParentNode = new DiskNode { FullPath = Path.Combine(home, ".vscode"), Name = ".vscode", IsFolder = true }
        };
        var vm = new TreeNodeViewModel(node, services, 100);
        Console.WriteLine("Node        : " + extr);
        Console.WriteLine("EnvTagText  : [" + vm.EnvTagText + "]");
        Console.WriteLine("CanMigrate  : " + vm.CanMigrateViaEnv);
        if (string.IsNullOrEmpty(vm.EnvTagText) || !vm.CanMigrateViaEnv)
        {
            Console.WriteLine("FAIL: extensions should have tag + can migrate");
            Console.WriteLine("  direct Match: " + (EnvVarRuleSet.Match(extr) == null ? "null" : EnvVarRuleSet.Match(extr).Id));
            return 1;
        }
        Console.WriteLine("OK: extensions node tagged + migratable");

        // 节点：插件子目录（应无标注）
        var node2 = new DiskNode { FullPath = plugin, Name = "some.extension-1.0.0", IsFolder = true, ParentNode = node };
        var vm2 = new TreeNodeViewModel(node2, services, 100);
        Console.WriteLine("Plugin      : " + plugin);
        Console.WriteLine("EnvTagText  : [" + vm2.EnvTagText + "]");
        Console.WriteLine("CanMigrate  : " + vm2.CanMigrateViaEnv);
        if (!string.IsNullOrEmpty(vm2.EnvTagText) || vm2.CanMigrateViaEnv)
        {
            Console.WriteLine("FAIL: plugin should NOT be tagged/migratable");
            return 1;
        }
        Console.WriteLine("OK: plugin node not tagged");

        // 节点：.gradle（应标注）
        string gradle = Path.Combine(home, ".gradle");
        var node3 = new DiskNode { FullPath = gradle, Name = ".gradle", IsFolder = true,
            ParentNode = new DiskNode { FullPath = home, Name = "letvar", IsFolder = true } };
        var vm3 = new TreeNodeViewModel(node3, services, 100);
        Console.WriteLine("Gradle      : " + gradle);
        Console.WriteLine("EnvTagText  : [" + vm3.EnvTagText + "]");
        Console.WriteLine("CanMigrate  : " + vm3.CanMigrateViaEnv);
        if (string.IsNullOrEmpty(vm3.EnvTagText) || !vm3.CanMigrateViaEnv)
        {
            Console.WriteLine("FAIL: .gradle should be tagged + migratable");
            return 1;
        }
        Console.WriteLine("OK: .gradle tagged + migratable");

        Console.WriteLine("ALL ENV-UI TESTS PASSED");
        return 0;
    }
}