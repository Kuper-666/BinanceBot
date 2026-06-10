using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BinanceBotWpf
{
    public class PnLToBrushConverter : IValueConverter
    {
        public static readonly PnLToBrushConverter Instance = new ();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            decimal pnl = value as decimal? ?? 0;
            if (pnl > 0) return Brushes.LightGreen;
            if (pnl < 0) return Brushes.Salmon;
            return Brushes.White;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException ();
        }
    }
}