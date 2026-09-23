using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FC.Converters;
using FC.Models;
using FC.Services;

namespace FC.ViewModels
{
    /// <summary>
    /// 「增量对比」窗口 VM：对比最近一次完整扫描（新快照）与上一次快照，
    /// 列出占用变化最大的目录（Top N），定位"最近是谁把盘吃大了"。
    /// </summary>
    public class DeltaViewModel : ViewModelBase
    {
        /// <summary>展品行</summary>
        public sealed class DeltaRow
        {
            public string Path { get; set; }
            public string OldText { get; set; }
            public string NewText { get; set; }
            public string DeltaText { get; set; }
            public string MarkText { get; set; }
        }

        private string _statusText;

        public DeltaViewModel(DiskNode root)
        {
            Rows = new ObservableCollection<DeltaRow>();

            if (root == null)
            {
                StatusText = "没有扫描结果。请先扫描一个盘符/目录。";
                return;
            }

            string file = SnapshotDiff.SnapshotFilePath(root.FullPath);
            ScanSnapshot oldSnap = ScanSnapshot.Load(file);
            ScanSnapshot newSnap = ScanSnapshot.Capture(root);

            if (oldSnap == null)
            {
                StatusText = "还没有上一次快照。本次扫描结果已存为新基准，下次扫描后即可对比增量。";
                // 保存基准
                ScanSnapshot.Save(file, newSnap);
                return;
            }

            // 扫描目标变化时不要比（快照文件名已按盘隔离，这里仅防御性检查）
            string oldTarget = oldSnap.Target ?? "";
            string newTarget = newSnap.Target ?? "";
            if (!string.Equals(oldTarget, newTarget, StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "上次快照扫描的是 " + oldTarget + "，与当前 " + newTarget + " 不同，已用当前结果替换基准。";
                ScanSnapshot.Save(file, newSnap);
                return;
            }

            var diffs = SnapshotDiff.Compute(oldSnap, newSnap, 50);
            foreach (var d in diffs)
            {
                Rows.Add(new DeltaRow
                {
                    Path = d.Path,
                    OldText = SizeText.Format(d.OldBytes),
                    NewText = SizeText.Format(d.NewBytes),
                    DeltaText = (d.DeltaBytes >= 0 ? "+" : "-") + SizeText.Format(Math.Abs(d.DeltaBytes)),
                    MarkText = d.IsNew ? "新增" : (d.IsRemoved ? "已删" : "")
                });
            }

            if (Rows.Count == 0)
            {
                StatusText = "与上次快照相比没有大小变化。";
            }
            else
            {
                long sum = diffs.Where(x => x.DeltaBytes > 0).Sum(x => x.DeltaBytes);
                StatusText = string.Format("共 {0} 个目录有变化（Top {1}），增长合计约 {2}。",
                    diffs.Count, Rows.Count, SizeText.Format(sum));
            }

            // 本次结果存为新基准
            ScanSnapshot.Save(file, newSnap);
        }

        public ObservableCollection<DeltaRow> Rows { get; private set; }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value); }
        }
    }
}