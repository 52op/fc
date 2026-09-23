using System.Collections.Generic;
using System.Collections.ObjectModel;
using FC.Converters;
using FC.Models;

namespace FC.ViewModels
{
    /// <summary>
    /// 「类型统计」窗口 VM：最近一次扫描按扩展名聚合的占用分布（Top N）。
    /// 行含占用、占总量百分比与条形宽度、文件数。
    /// </summary>
    public class TypeStatsViewModel : ViewModelBase
    {
        /// <summary>展品行（不可变快照）</summary>
        public sealed class TypeRow
        {
            public string ExtensionText { get; set; }
            public string BytesText { get; set; }
            public string CountText { get; set; }
            public double Percent { get; set; }
            public double BarWidth { get; set; }
        }

        private string _statusText;
        private double _totalPercent;

        public TypeStatsViewModel(List<TypeStat> stats, long totalBytes)
        {
            Rows = new ObservableCollection<TypeRow>();
            if (stats != null)
            {
                foreach (var s in stats)
                {
                    Rows.Add(new TypeRow
                    {
                        ExtensionText = string.IsNullOrEmpty(s.Extension) ? "（无扩展名）" : s.Extension,
                        BytesText = SizeText.Format(s.Bytes),
                        CountText = SizeText.FormatCount(s.Count),
                        Percent = totalBytes > 0 ? s.Bytes * 100.0 / totalBytes : 0,
                        BarWidth = totalBytes > 0 ? System.Math.Max(0, System.Math.Min(100, s.Bytes * 100.0 / totalBytes)) : 0
                    });
                }
            }

            if (Rows.Count == 0)
            {
                StatusText = "没有类型统计：尚未扫描，或上次扫描启用了“快速复用”。";
            }
            else
            {
                foreach (var r in Rows)
                {
                    _totalPercent += r.Percent;
                }
                StatusText = string.Format("按扩展名聚合（Top {0}），共覆盖总占用约 {1:0.0}%。双击右键可复制扩展名。",
                    Rows.Count, _totalPercent);
            }
        }

        public ObservableCollection<TypeRow> Rows { get; private set; }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value); }
        }
    }
}