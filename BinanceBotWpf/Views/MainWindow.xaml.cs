using BinanceBotWpf.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BinanceBotWpf
{
    public partial class MainWindow : Window
    {
        private ScrollViewer _logsScrollViewer;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent ();
            DataContext = viewModel;

            // Подписываемся на изменение коллекции логов
            viewModel.SystemLogs.CollectionChanged += OnLogsCollectionChanged;

            // Когда вкладка станет видимой, находим ScrollViewer
            this.Loaded += (s, e) => FindLogsScrollViewer ();
        }

        private void OnLogsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogsListBox != null)
            {
                Dispatcher.BeginInvoke (new System.Action (() =>
                {
                    LogsListBox.ScrollIntoView (LogsListBox.Items[LogsListBox.Items.Count - 1]);
                }));
            }
        }

        private void FindLogsScrollViewer()
        {
            _logsScrollViewer = FindVisualChild<ScrollViewer> (this, "LogsScrollViewer");
        }

        private T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount (parent); i++)
            {
                var child = VisualTreeHelper.GetChild (parent, i);
                if (child is T element && element.Name == name)
                    return element;
                var result = FindVisualChild<T> (child, name);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}