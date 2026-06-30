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
            Closing += OnClosing;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Instance = null;
        }

        public void AppendLog(string text)
        {
            try
            {
                Dispatcher.Invoke (() =>
                {
                    // Запоминаем, был ли пользователь внизу ДО добавления новой строки.
                    // Небольшой допуск (40px), чтобы не сбивать автоскролл из-за погрешностей рендера.
                    bool wasAtBottom = LogsRichTextBox.VerticalOffset >= LogsRichTextBox.ExtentHeight - LogsRichTextBox.ViewportHeight - 40;

                    var run = new Run (text);
                    var paragraph = new Paragraph (run) { Margin = new Thickness(0) };
                    LogsRichTextBox.Document.Blocks.Add (paragraph);

                    // Ограничиваем количество строк (оставляем последние 1000)
                    while (LogsRichTextBox.Document.Blocks.Count > 1000)
                        LogsRichTextBox.Document.Blocks.Remove (LogsRichTextBox.Document.Blocks.FirstBlock);

                    // Автоскролл только если пользователь и так был внизу — иначе не мешаем читать историю
                    if (wasAtBottom)
                        LogsRichTextBox.ScrollToEnd ();
                });
            }
            catch (System.ObjectDisposedException) { }
            catch (System.InvalidOperationException) { }
        }

        public void ClearLogs()
        {
            try
            {
                Dispatcher.Invoke (() =>
                {
                    LogsRichTextBox.Document.Blocks.Clear ();
                    LogsRichTextBox.Document.Blocks.Add (new Paragraph (new Run ("")));
                });
            }
            catch (System.ObjectDisposedException) { }
            catch (System.InvalidOperationException) { }
        }
    }
}