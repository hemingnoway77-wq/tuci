using System.Globalization;
using System.Windows.Data;

namespace VocabCollector.Desktop;

/// <summary>Displays a zero-based alternation index as a printed 01-based ordinal.</summary>
public sealed class OrdinalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && index >= 0 ? (index + 1).ToString("00", CultureInfo.InvariantCulture) : "00";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
