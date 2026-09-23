using System;
using System.Globalization;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>
    /// 表头排序箭头：输入 [SortColumnKey, SortDescending]，参数为当前列 key。
    /// 当前列 → 返回 "▲"（降序）或 "▼"（升序）；非当前列返回 ""。
    /// </summary>
    public class SortArrowConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string key = values[0] as string;
            bool desc = values.Length > 1 && values[1] is bool && (bool)values[1];
            string param = parameter as string;

            if (string.Equals(key, param, StringComparison.Ordinal))
            {
                return desc ? "\u25B2" : "\u25BC";
            }
            return "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}