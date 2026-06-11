using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BinanceBotWpf
{
    public class LogLevelToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string log = value as string ?? "";

            if (log.Contains ("❌") || log.Contains ("ERROR") || log.Contains ("Ошибка"))
                return new SolidColorBrush (Color.FromRgb (231, 76, 60)); // Красный

            if (log.Contains ("✅") || log.Contains ("Успешно") || log.Contains ("КУПЛЕНО"))
                return new SolidColorBrush (Color.FromRgb (46, 204, 113)); // Зелёный

            if (log.Contains ("⚠️") || log.Contains ("WARNING") || log.Contains ("Предупреждение"))
                return new SolidColorBrush (Color.FromRgb (241, 196, 15)); // Жёлтый

            if (log.Contains ("📊") || log.Contains ("📈") || log.Contains ("📉"))
                return new SolidColorBrush (Color.FromRgb (52, 152, 219)); // Синий

            if (log.Contains ("🟢") || log.Contains ("BUY") || log.Contains ("Покупка"))
                return new SolidColorBrush (Color.FromRgb (46, 204, 113)); // Зелёный

            if (log.Contains ("🔴") || log.Contains ("SELL") || log.Contains ("Продажа"))
                return new SolidColorBrush (Color.FromRgb (231, 76, 60)); // Красный

            return new SolidColorBrush (Color.FromRgb (189, 195, 199)); // Серый по умолчанию
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException ();
        }
    }
}