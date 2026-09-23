using System;
using System.Globalization;
using System.Windows.Data;

namespace FC.Converters
{
    /// <summary>long / long? → "1.5 GB" 文本</summary>
    public class SizeToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long)
            {
                return SizeText.Format((long)value);
            }
            if (value is long?)
            {
                return SizeText.Format((long?)value);
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}