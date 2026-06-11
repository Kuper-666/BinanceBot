using BinanceBotWpf.ViewModels;
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

            // Подписываемся на событие прокрутки
            viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof (MainWindowViewModel.RequestLogsScroll) && _logsScrollViewer != null)
                {
                    _logsScrollViewer.ScrollToEnd ();
                }
            };
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Находим ScrollViewer при переключении на вкладку логов
            if (e.Source is TabControl tabControl && tabControl.SelectedItem is TabItem tabItem && tabItem.Name == "LogsTab")
            {
                FindAndScrollLogs ();
            }
        }

        private void FindAndScrollLogs()
        {
            var scrollViewer = FindVisualChild<ScrollViewer> (this, "LogsScrollViewer");
            if (scrollViewer != null)
            {
                _logsScrollViewer = scrollViewer;
                _logsScrollViewer.ScrollToEnd ();
            }
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