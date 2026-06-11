using BinanceBotWpf.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

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

        public void AppendLog(string text)
        {
            Dispatcher.Invoke (() =>
            {
                var run = new Run (text + "\n");
                var paragraph = new Paragraph (run);
                LogsRichTextBox.Document.Blocks.Add (paragraph);
                LogsRichTextBox.ScrollToEnd ();
                // Ограничиваем количество строк (оставляем последние 1000)
                while (LogsRichTextBox.Document.Blocks.Count > 1000)
                    LogsRichTextBox.Document.Blocks.Remove (LogsRichTextBox.Document.Blocks.FirstBlock);
            });
        }

        public void ClearLogs()
        {
            Dispatcher.Invoke (() =>
            {
                LogsRichTextBox.Document.Blocks.Clear ();
                LogsRichTextBox.Document.Blocks.Add (new Paragraph (new Run ("")));
            });
        }
    }
}