using BinanceBotWpf.ViewModels;
using System;
using System.Windows;

namespace BinanceBotWpf
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent ();
            DataContext = viewModel;

            try
            {
                var uri = new Uri ("pack://application:,,,/Resources/binance_bot.ico");
                if (System.IO.File.Exists (uri.LocalPath) || Application.GetResourceStream (uri) != null)
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage (uri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"Не удалось загрузить иконку: {ex.Message}");
            }

            LogTextBox.TextChanged += (s, e) => LogTextBox.ScrollToEnd ();
        }
    }
}