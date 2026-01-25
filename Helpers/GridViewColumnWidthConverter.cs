using System;
using System.Globalization;
using System.Windows.Data;

namespace LidarrCompanion.Helpers
{
    /// <summary>
    /// Converter for GridView column widths that allows proportional sizing with minimum widths.
    /// ConverterParameter format: "proportion|minimumWidth" (e.g., "0.4|200" means 40% of available width, minimum 200px)
    /// </summary>
    public class GridViewColumnWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double actualWidth && parameter is string param)
            {
                var parts = param.Split('|');
                if (parts.Length == 2 
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var proportion)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minWidth))
                {
                    // Account for scrollbar width (approximately 20px) and padding
                    var availableWidth = actualWidth - 25;
                    var calculatedWidth = availableWidth * proportion;
                    
                    // Return the larger of calculated width or minimum width
                    return Math.Max(calculatedWidth, minWidth);
                }
            }
            
            return 100.0; // Default width if conversion fails
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
