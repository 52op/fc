using System;
using System.Globalization;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>
    /// ProgressBar 自定义模板用：Indicator 宽度 = Value(0-100) / 100 * 可用宽度。
    /// values[0] = ProgressBar.Value；values[1] = 模板根 ActualWidth。
    /// 直接驱动 Border.Width，Value 一变条立刻变（不依赖默认模板的内部动画）。
    /// </summary>
    public class ProgressBarWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double value = values != null && values.Length > 0 && values[0] is double
                ? (double)values[0]
                : 0.0;
            double width = values != null && values.Length > 1 && values[1] is double
                ? (double)values[1]
                : 0.0;

            if (value < 0)
            {
                value = 0;
            }
            if (value > 100)
            {
                value = 100;
            }
            if (width <= 0)
            {
                return 0.0;
            }
            double w = width * value / 100.0;
            return w < 1.0 ? 0.0 : w;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}