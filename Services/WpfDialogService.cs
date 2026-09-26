using System;
using System.Windows;
using System.Windows.Interop;
using FC.Views;

namespace FC.Services
{
    /// <summary>
    /// WPF 实现。注意：.NET Framework 的 WPF 没有原生文件夹选择器，
    /// 所以 PickFolder 走 System.Windows.Forms.FolderBrowserDialog（经典样式对话框）。
    /// 所有对话框都挂到主窗作 owner：避免弹到主窗后面造成"点迁移没反应"的错觉。
    /// </summary>
    public class WpfDialogService : IDialogService
    {
        private sealed class Win32Window : System.Windows.Forms.IWin32Window
        {
            private readonly IntPtr _handle;

            public Win32Window(IntPtr handle)
            {
                _handle = handle;
            }

            public IntPtr Handle
            {
                get { return _handle; }
            }
        }

        public string PickFolder(string title, string initialPath, out bool cancelled)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = title ?? "选择目标目录";
                dlg.ShowNewFolderButton = true;

                if (!string.IsNullOrEmpty(initialPath))
                {
                    try
                    {
                        dlg.SelectedPath = initialPath;
                    }
                    catch (Exception)
                    {
                        // 无效初始路径忽略
                    }
                }

                System.Windows.Forms.DialogResult result;
                try
                {
                    result = dlg.ShowDialog(GetOwner());
                }
                catch (Exception)
                {
                    // 取 owner 失败等极端情况：退回无所有权模式
                    result = dlg.ShowDialog();
                }

                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    cancelled = false;
                    return dlg.SelectedPath;
                }
                cancelled = true;
                return null;
            }
        }

        public bool Confirm(string title, string message)
        {
            return MessageBox.Show(
                GetOwnerWindow(), message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes) == MessageBoxResult.Yes;
        }

        public void ShowError(string title, string message)
        {
            MessageBox.Show(GetOwnerWindow(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public void ShowInfo(string title, string message)
        {
            MessageBox.Show(GetOwnerWindow(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public IProgressDialog ShowProgress(string title, string operationLabel)
        {
            var win = new MigrateProgressWindow(title, operationLabel);
            var app = Application.Current;
            if (app != null && app.MainWindow != null && app.MainWindow.IsLoaded)
            {
                win.Owner = app.MainWindow;
            }
            win.Show();
            return win;
        }

        /// <summary>当前主窗口的 Win32 句柄包装（供 WinForms 对话框作 owner；无则 null）。</summary>
        private static System.Windows.Forms.IWin32Window GetOwner()
        {
            try
            {
                Window w = GetOwnerWindow();
                if (w == null)
                {
                    return null;
                }
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero)
                {
                    return null;
                }
                return new Win32Window(h);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>当前已加载的主窗口（供 WPF MessageBox 作 owner；无则 null）。</summary>
        private static Window GetOwnerWindow()
        {
            try
            {
                var app = Application.Current;
                if (app == null || app.MainWindow == null || !app.MainWindow.IsLoaded)
                {
                    return null;
                }
                return app.MainWindow;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}