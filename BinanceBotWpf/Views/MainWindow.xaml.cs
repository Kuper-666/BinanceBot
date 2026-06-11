using BinanceBotWpf.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace BinanceBotWpf
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent ();
            DataContext = viewModel;
            Instance = this;
        }

        /// <summary>Прокручивает лог вниз</summary>
        public void ScrollLogsToEnd()
        {
            Dispatcher.BeginInvoke (new System.Action (() =>
            {
                var listBox = FindName ("LogsListBox") as ListBox;
                if (listBox != null && listBox.Items.Count > 0)
                    listBox.ScrollIntoView (listBox.Items[listBox.Items.Count - 1]);
            }));
        }
    }
}