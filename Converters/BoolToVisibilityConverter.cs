using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>
    /// bool → Visibility。XAML 里用 ConverterParameter="Invert" 反转。
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        private static readonly Visibility TrueValue = Visibility.Visible;
        private static readonly Visibility FalseValue = Visibility.Collapsed;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool && (bool)value;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            {
                b = !b;
            }
            return b ? TrueValue : FalseValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility)
            {
                bool result = (Visibility)value == TrueValue;
                if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
                {
                    result = !result;
                }
                return result;
            }
            return false;
        }
    }
}