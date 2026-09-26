using System;
using System.Runtime.InteropServices;

namespace FC.Services
{
    /// <summary>
    /// 环境变量读写管理（用户级/系统级）+ 变更广播。
    /// - 用户级：EnvironmentTarget.User → HKCU\Environment，无需管理员。
    /// - 系统级：EnvironmentTarget.Machine → HKLM，需管理员（调用方需提权）。
    /// 写完后广播 WM_SETTINGCHANGE 让新启动进程立即读到新值（已运行进程仍读旧值，故迁移前须先杀进程）。
    /// </summary>
    public static class EnvVarManager
    {
        public enum Scope
        {
            User,
            Machine
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        private const uint HWND_BROADCAST = 0xffff;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint TimeoutMs = 3000;

        /// <summary>读取指定作用域的变量值（不存在返回 null）。</summary>
        public static string Get(string name, Scope scope)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            try
            {
                return Environment.GetEnvironmentVariable(name, ToTarget(scope));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>设置变量值（value 为 null/空 = 删除该变量）。成功后广播。</summary>
        public static bool Set(string name, string value, Scope scope, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name))
            {
                error = "环境变量名为空。";
                return false;
            }
            try
            {
                Environment.SetEnvironmentVariable(name, string.IsNullOrEmpty(value) ? null : value, ToTarget(scope));
                Broadcast();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>删除变量。返回是否成功。</summary>
        public static bool Remove(string name, Scope scope, out string error)
        {
            return Set(name, null, scope, out error);
        }

        /// <summary>该作用域是否需要管理员权限才能写。</summary>
        public static bool NeedsAdmin(Scope scope)
        {
            return scope == Scope.Machine;
        }

        /// <summary>广播环境变量变更，通知 Explorer 等刷新。</summary>
        public static void Broadcast()
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout(
                    new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment",
                    SMTO_ABORTIFHUNG, TimeoutMs, out result);
            }
            catch (Exception)
            {
                // 广播失败不影响变量本身已写入
            }
        }

        private static EnvironmentVariableTarget ToTarget(Scope scope)
        {
            return scope == Scope.Machine
                ? EnvironmentVariableTarget.Machine
                : EnvironmentVariableTarget.User;
        }
    }
}