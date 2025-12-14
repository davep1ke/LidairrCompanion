using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LidarrCompanion.Helpers
{
    public class ActionToBrushConverter : IValueConverter
    {
        // Mode can be "Background" or "Foreground" (case-insensitive). Default: Background
        public string Mode { get; set; } = "Background";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is ProposalActionType action))
            {
                return string.Equals(Mode, "Foreground", StringComparison.OrdinalIgnoreCase) ? Brushes.Black : Brushes.Transparent;
            }

            if (string.Equals(Mode, "Foreground", StringComparison.OrdinalIgnoreCase))
                return GetForeground(action);

            return GetBackground(action);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

        private static Brush GetBackground(ProposalActionType action)
        {
            return action switch
            {
                ProposalActionType.Import => Brushes.LightGreen,
                ProposalActionType.NotForImport => Brushes.Orange,
                ProposalActionType.Defer => Brushes.LightGoldenrodYellow,
                ProposalActionType.Unlink => Brushes.LightSalmon,
                ProposalActionType.Delete => Brushes.LightCoral,
                _ => Brushes.Transparent,
            };
        }

        private static Brush GetForeground(ProposalActionType action)
        {
            return action switch
            {
                ProposalActionType.Import => Brushes.Black,
                ProposalActionType.NotForImport => Brushes.White,
                ProposalActionType.Defer => Brushes.Black,
                ProposalActionType.Unlink => Brushes.Black,
                ProposalActionType.Delete => Brushes.White,
                _ => Brushes.Black,
            };
        }
    }
}
