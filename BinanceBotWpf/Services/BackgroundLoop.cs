using System;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Абстрактный базовый класс для фоновых циклических задач.
    /// </summary>
    public abstract class BackgroundLoop
    {
        private readonly Action<string> _logger;
        private readonly string _name;
        private Task _task;
        private CancellationTokenSource _cts;

        protected BackgroundLoop(string name, Action<string> logger)
        {
            _name = name;
            _logger = logger;
        }

        /// <summary>
        /// Запускает фоновый цикл.
        /// </summary>
        public void Start()
        {
            if (_task != null && !_task.IsCompleted) return;
            _cts = new CancellationTokenSource ();
            _task = Task.Run (() => RunAsync (_cts.Token));
            _logger?.Invoke ($"🟢 Запущен фоновый цикл: {_name}");
        }

        /// <summary>
        /// Останавливает фоновый цикл.
        /// </summary>
        public async Task StopAsync()
        {
            if (_cts == null) return;
            _cts.Cancel ();
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {
                // Ожидаемо
            }
            _cts.Dispose ();
            _cts = null;
            _logger?.Invoke ($"🔴 Остановлен фоновый цикл: {_name}");
        }

        /// <summary>
        /// Основной метод цикла. Вызывается в фоновом потоке.
        /// </summary>
        protected abstract Task ExecuteAsync(CancellationToken token);

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ExecuteAsync (token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ Ошибка в цикле {_name}: {ex.Message}");
                    await Task.Delay (5000, token);
                }
            }
        }
    }
}