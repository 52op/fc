using System;

namespace FC.Converters
{
    /// <summary>显示单位模式（全局静态，切换后由 VM 广播刷新）</summary>
    public enum SizeUnitMode
    {
        Auto,
        KB,
        MB,
        GB
    }

    /// <summary>字节数 → 人类可读文本（"1.5 GB"）。集中到一处，转换器与日志共用。</summary>
    public static class SizeText
    {
        public static SizeUnitMode Mode = SizeUnitMode.Auto;

        public static string Format(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            switch (Mode)
            {
                case SizeUnitMode.KB:
                    return FormatFixed(bytes, 1, "KB");
                case SizeUnitMode.MB:
                    return FormatFixed(bytes, 2, "MB");
                case SizeUnitMode.GB:
                    return FormatFixed(bytes, 3, "GB");
                default:
                    return FormatAuto(bytes);
            }
        }

        private static string FormatFixed(long bytes, int unitIndex, string unitName)
        {
            double v = bytes;
            for (int i = 0; i < unitIndex; i++)
            {
                v /= 1024d;
            }
            return v.ToString("0.0") + " " + unitName;
        }

        private static string FormatAuto(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024d && unit < units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }

            if (unit == 0)
            {
                return value.ToString("0") + " B";
            }
            return value.ToString("0.0") + " " + units[unit];
        }

        public static string Format(long? bytes)
        {
            return bytes.HasValue ? Format(bytes.Value) : "未知";
        }

        public static string FormatCount(long count)
        {
            return count.ToString("N0");
        }
    }
}