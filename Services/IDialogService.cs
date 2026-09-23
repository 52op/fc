using System;
using System.Threading;

namespace FC.Services
{
    /// <summary>进度对话框句柄。VM 用它写日志、取取消令牌，操作结束后 Close。</summary>
    public interface IProgressDialog : IDisposable
    {
        /// <summary>取消令牌：用户点"取消"或窗口被关闭时触发</summary>
        CancellationToken Token { get; }

        void AppendLog(string line);

        void SetStatus(string text);

        /// <summary>0-100 阶段进度（可选驱动）</summary>
        void SetProgress(double value);

        void Close();
    }

    /// <summary>
    /// UI 对话框抽象，让 ViewModel 不直接依赖 MessageBox / FolderBrowserDialog / Window。
    /// </summary>
    public interface IDialogService
    {
        /// <summary>弹文件夹选择框。用户取消时返回 null 且 cancelled=true。</summary>
        string PickFolder(string title, string initialPath, out bool cancelled);

        /// <summary>确认框（是/否）。</summary>
        bool Confirm(string title, string message);

        /// <summary>错误框。</summary>
        void ShowError(string title, string message);

        /// <summary>打开进度对话框（非阻塞）。</summary>
        IProgressDialog ShowProgress(string title, string operationLabel);
    }
}