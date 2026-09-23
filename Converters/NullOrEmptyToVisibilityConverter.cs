using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>字符串→Visibility：非空=Visible，空=Collapsed（ConverterParameter=Invert 反转）。</summary>
    public class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        private static readonly Visibility Visible = Visibility.Visible;
        private static readonly Visibility Hidden = Visibility.Collapsed;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasText = value != null && !string.IsNullOrEmpty(value.ToString());
            bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
            if (invert)
            {
                hasText = !hasText;
            }
            return hasText ? Visible : Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}