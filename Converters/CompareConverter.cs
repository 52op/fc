using System;
using System.Globalization;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>值 == 参数（字符串比较）→ true。用于菜单/单选选中态。</summary>
    public class CompareConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string v = value as string;
            string p = parameter as string;
            return string.Equals(v, p, StringComparison.Ordinal);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}