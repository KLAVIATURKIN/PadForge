using System;
using System.Globalization;
using System.Windows.Data;
using PadForge.Common;

namespace PadForge.Converters
{
    /// <summary>
    /// The language a culture's own name is declared in. The language picker
    /// lists every language by its own name, and each row declares its own
    /// language, so 中文 is drawn in the Chinese font and 日本語 in the
    /// Japanese one whatever language the interface is in (#504).
    /// </summary>
    public sealed class CultureLanguageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => UiLanguage.Declared(value as CultureInfo);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
