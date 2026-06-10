using BinanceBotWpf.ViewModels;
using System.Windows;

namespace BinanceBotWpf
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent ();
            DataContext = viewModel;
        }
    }
}