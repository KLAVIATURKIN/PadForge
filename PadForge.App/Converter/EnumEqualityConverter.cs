using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// True when the bound enum value's name matches the converter
    /// parameter, the bool twin of <see cref="EnumEqualityVisibilityConverter"/>
    /// with the same matching: case-insensitive on the name, and a
    /// pipe-separated list accepts any of its values. Drives
    /// SegmentButton.IsSelected on the slot type tiles.
    /// </summary>
    public sealed class EnumEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return false;
            string current = value.ToString();
            foreach (var entry in parameter.ToString().Split('|'))
            {
                if (string.Equals(current, entry.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // One-way binding only.
            return Binding.DoNothing;
        }
    }
}
