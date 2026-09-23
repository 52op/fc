using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FC.Converters;
using FC.Models;

namespace FC.ViewModels
{
    /// <summary>
    /// 「最大文件」窗口 VM：展示最近一次扫描收集到的全局最大文件（Top N），
    /// 支持打开所在目录并选中。快速复用模式未完整枚举文件时会标注提示。
    /// </summary>
    public class LargeFilesViewModel : ViewModelBase
    {
        /// <summary>展品行（不可变快照，供 DataGrid 绑定）</summary>
        public sealed class LargeFileRow
        {
            public string FullPath { get; set; }
            public string Name { get; set; }
            public string DirectoryText { get; set; }
            public long Size { get; set; }
            public string SizeText { get; set; }
            public string ModifiedText { get; set; }
            public string TagText { get; set; }

            public LargeFileRow() { }

            public LargeFileRow(FileEntry e)
            {
                FullPath = e.FullPath;
                Name = e.Name;
                DirectoryText = ParentDir(e.FullPath);
                Size = e.Size;
                SizeText = FC.Converters.SizeText.Format(e.Size);
                if (e.LastWriteTime > DateTime.MinValue)
                {
                    ModifiedText = e.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                }
                if (!string.IsNullOrEmpty(e.SpecialNote))
                {
                    TagText = e.SpecialNote;
                }
                else if (e.IsHiddenOrSystem)
                {
                    TagText = "隐藏/系统文件";
                }
                else
                {
                    TagText = "";
                }
            }

            private static string ParentDir(string path)
            {
                try
                {
                    return System.IO.Path.GetDirectoryName(path);
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        private string _statusText;

        public LargeFilesViewModel(List<FileEntry> files, bool complete)
        {
            OpenCommand = new RelayCommand(OpenSelected, o => o is LargeFileRow);
            CopyPathCommand = new RelayCommand(CopyPath, o => o is LargeFileRow);

            Rows = new ObservableCollection<LargeFileRow>();
            if (files != null)
            {
                foreach (var e in files)
                {
                    Rows.Add(new LargeFileRow(e));
                }
            }

            if (Rows.Count == 0)
            {
                if (complete)
                {
                    StatusText = "未找到 ≥ 8MB 的大文件（或尚未扫描）。";
                }
                else
                {
                    StatusText = "找不到大文件列表：尚未扫描，或上次扫描启用了“快速复用”（未变目录不枚举文件，不收集大文件）。";
                }
            }
            else if (!complete)
            {
                StatusText = string.Format("来自最近一次扫描（快速复用模式下收集不完整，仅展示已枚举部分）。合计 {0} 个。", Rows.Count);
            }
            else
            {
                StatusText = string.Format("共 {0} 个最大的文件（≥ 8MB）。双击行可打开所在目录。", Rows.Count);
            }
        }

        public ObservableCollection<LargeFileRow> Rows { get; private set; }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value); }
        }

        public RelayCommand OpenCommand { get; private set; }

        public RelayCommand CopyPathCommand { get; private set; }

        private void OpenSelected(object o)
        {
            var row = o as LargeFileRow;
            if (row == null || string.IsNullOrEmpty(row.FullPath))
            {
                return;
            }
            if (!OpenContainingSelect(row.FullPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.DirectoryText)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception)
                {
                }
            }
        }

        private void CopyPath(object o)
        {
            var row = o as LargeFileRow;
            if (row == null || string.IsNullOrEmpty(row.FullPath))
            {
                return;
            }
            try
            {
                System.Windows.Clipboard.SetText(row.FullPath);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>打开所在目录并选中目标（与树行右键一致，P/Invoke 稳定）。</summary>
        private static bool OpenContainingSelect(string targetPath)
        {
            try
            {
                string parent = System.IO.Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(parent) || !System.IO.Directory.Exists(parent))
                {
                    return false;
                }
                bool exists = System.IO.File.Exists(targetPath) || System.IO.Directory.Exists(targetPath);
                if (!exists)
                {
                    return false;
                }

                IntPtr pidlParent;
                uint sfgao;
                if (SHParseDisplayName(parent, IntPtr.Zero, out pidlParent, 0, out sfgao) != 0 || pidlParent == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr pidlItem;
                if (SHParseDisplayName(targetPath, IntPtr.Zero, out pidlItem, 0, out sfgao) != 0 || pidlItem == IntPtr.Zero)
                {
                    CoTaskMemFree(pidlParent);
                    return false;
                }
                try
                {
                    return SHOpenFolderAndSelectItems(pidlParent, 1, new[] { pidlItem }, 0) == 0;
                }
                finally
                {
                    CoTaskMemFree(pidlParent);
                    CoTaskMemFree(pidlItem);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName,
            IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder, uint cidl,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray, SizeParamIndex = 1)]
            System.IntPtr[] apidl, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);
    }
}