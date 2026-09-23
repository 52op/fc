using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>
    /// 列宽显示转换：bool(是否显示) + double(列宽) → GridLength。
    /// 隐藏列宽归零，显示用原宽。
    /// </summary>
    public class ShowWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool show = values[0] is bool && (bool)values[0];
            double width = 0;
            if (values[1] is double)
            {
                width = (double)values[1];
            }
            else if (values[1] is double?)
            {
                width = ((double?)values[1]).GetValueOrDefault();
            }

            if (!show || width <= 0)
            {
                return new GridLength(0);
            }
            return new GridLength(width);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}