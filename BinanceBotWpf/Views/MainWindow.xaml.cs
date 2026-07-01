using BinanceBotWpf.ViewModels;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace BinanceBotWpf
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }
        private bool _chartReady;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent ();
            DataContext = viewModel;
            Instance = this;
            Closing += OnClosing;

            BalanceChartWebView.CoreWebView2InitializationCompleted += async (s, e) =>
            {
                string htmlPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Html", "balance_chart.html");
                if (File.Exists (htmlPath))
                {
                    BalanceChartWebView.CoreWebView2.Navigate ("file:///" + htmlPath.Replace ('\\', '/'));
                    BalanceChartWebView.NavigationCompleted += (s2, e2) =>
                    {
                        _chartReady = true;
                    };
                }
            };
            _ = BalanceChartWebView.EnsureCoreWebView2Async ();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Instance = null;
        }

        public void PushChartPoint (string time, decimal balance)
        {
            if (!_chartReady) return;
            try
            {
                BalanceChartWebView.CoreWebView2.ExecuteScriptAsync (
                    $"updateSinglePoint('{time}', {(double)balance})");
            }
            catch { }
        }

        public void PushChartFull (string timesJson, string valuesJson)
        {
            if (!_chartReady) return;
            try
            {
                BalanceChartWebView.CoreWebView2.ExecuteScriptAsync (
                    $"updateChart('{timesJson.Replace ("'", "\\'")}', '{valuesJson.Replace ("'", "\\'")}')");
            }
            catch { }
        }

        public void AppendLog(string text)
        {
            try
            {
                Dispatcher.Invoke (() =>
                {
                    bool wasAtBottom = LogsRichTextBox.VerticalOffset >= LogsRichTextBox.ExtentHeight - LogsRichTextBox.ViewportHeight - 40;

                    var run = new Run (text);
                    var paragraph = new Paragraph (run) { Margin = new Thickness(0) };
                    LogsRichTextBox.Document.Blocks.Add (paragraph);

                    while (LogsRichTextBox.Document.Blocks.Count > 1000)
                        LogsRichTextBox.Document.Blocks.Remove (LogsRichTextBox.Document.Blocks.FirstBlock);

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
