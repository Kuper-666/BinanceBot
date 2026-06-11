using BinanceBotWpf.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace BinanceBotWpf
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent ();
            DataContext = viewModel;

            // Подписываемся на изменение логов для автоскролла
            viewModel.SystemLogs.CollectionChanged += OnLogsCollectionChanged;
        }

        private void OnLogsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                Dispatcher.BeginInvoke (new System.Action (() =>
                {
                    var listBox = FindName ("LogsListBox") as ListBox;
                    if (listBox != null && listBox.Items.Count > 0)
                    {
                        listBox.ScrollIntoView (listBox.Items[listBox.Items.Count - 1]);
                    }
                }));
            }
        }
    }
}