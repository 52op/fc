using System;
using FC.Services;

class MultiRootTest
{
    private static int Main()
    {
        int fail = 0;

        // 相同
        Check(PathUtil.IsSameOrAncestorPath(@"C:\Users\letvar", @"C:\Users\letvar"), true, "same", ref fail);
        Check(PathUtil.IsSameOrAncestorPath(@"C:\", @"C:\Users"), true, "C:\\ -> C:\\Users", ref fail);
        Check(PathUtil.IsSameOrAncestorPath(@"C:", @"C:\Users"), true, "C: -> C:\\Users", ref fail);

        // c:\test 覆盖（c: 是 c:\test 的祖先）
        Check(PathUtil.IsSameOrAncestorPath(@"C:\", @"C:\test"), true, "C:\\ -> C:\\test (disk covers subdir)", ref fail);
        Check(PathUtil.IsSameOrAncestorPath(@"C:\test", @"C:\test\sub"), true, "C:\\test -> C:\\test\\sub", ref fail);

        // 不相关（C 盘 vs D 盘）
        Check(PathUtil.IsSameOrAncestorPath(@"D:\", @"C:\Users"), false, "D:\\ vs C:\\Users", ref fail);

        // 兄弟/前缀陷阱：C:\test 不应吞掉 C:\testing
        Check(PathUtil.IsSameOrAncestorPath(@"C:\test", @"C:\testing"), false, "C:\\test vs C:\\testing (prefix trap)", ref fail);

        // 反向：子不应吞父
        Check(PathUtil.IsSameOrAncestorPath(@"C:\test\sub", @"C:\test"), false, "child should not cover parent", ref fail);

        // 大小写
        Check(PathUtil.IsSameOrAncestorPath(@"c:\users", @"C:\Users\letvar"), true, "case insensitive", ref fail);

        if (fail == 0)
        {
            Console.WriteLine("ALL MULTIROOT TESTS PASSED");
            return 0;
        }
        Console.WriteLine("FAILED: " + fail);
        return 1;
    }

    private static void Check(bool actual, bool expected, string label, ref int fail)
    {
        if (actual != expected)
        {
            Console.WriteLine("FAIL " + label + ": expected " + expected + " got " + actual);
            fail++;
        }
        else
        {
            Console.WriteLine("OK   " + label);
        }
    }
}