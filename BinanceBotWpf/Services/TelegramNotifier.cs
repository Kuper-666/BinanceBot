using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Отправка уведомлений в Telegram, обработка команд, клавиатура.
    /// </summary>
    public class TelegramNotifier
    {
        private readonly TelegramBotClient _botClient;
        private readonly string _chatId;
        private readonly bool _enabled;
        private Func<string, string, Task> _commandHandler;
        private CancellationTokenSource _cts;
        public string GetChatId() => _chatId;

        private readonly ConcurrentDictionary<string, DateTime> _lastCommandTime = new ();
        private readonly TimeSpan _commandCooldown = TimeSpan.FromSeconds (2);

        public bool IsEnabled => _enabled;

        public TelegramNotifier(string botToken, string chatId)
        {
            _botClient = new TelegramBotClient (botToken);
            _chatId = chatId;
            _enabled = !string.IsNullOrEmpty (botToken) && !string.IsNullOrEmpty (chatId);

            // Отладочный вывод
            System.Diagnostics.Debug.WriteLine ($"Telegram инициализация: Token={!string.IsNullOrEmpty (botToken)}, ChatId={!string.IsNullOrEmpty (chatId)}, Enabled={_enabled}");

            if (_enabled)
            {
                try
                {
                    // Проверяем подключение синхронно (для старой версии библиотеки)
                    var me = _botClient.GetMeAsync ().GetAwaiter ().GetResult ();
                    System.Diagnostics.Debug.WriteLine ($"Telegram бот: @{me.Username}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine ($"Telegram ошибка: {ex.Message}");
                    // Не отключаем _enabled, так как бот может работать позже
                }
            }
        }

        /// <summary>Клавиатура с кнопками для быстрых команд.</summary>
        public ReplyKeyboardMarkup GetMainKeyboard()
        {
            return new ReplyKeyboardMarkup (new[]
            {
                new KeyboardButton[] { "📊 Статус", "💼 Баланс" },
                new KeyboardButton[] { "🧠 Переобучить ML", "📁 Экспорт" },
                new KeyboardButton[] { "▶️ Запуск", "⏹️ Стоп" },
                new KeyboardButton[] { "📈 График PnL", "🔄 Обновить" },
                new KeyboardButton[] { "❓ Помощь" }
            })
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = false
            };
        }

        /// <summary>Отправляет приветственное сообщение с клавиатурой.</summary>
        public async Task SendWelcomeMessageAsync(string chatId)
        {
            if (!_enabled) return;
            var message = "🤖 *Binance Trading Bot*\n\n" +
                          "Я автоматический торговый бот на базе SMA и ML.\n" +
                          "Выберите действие (кнопки внизу) или введите команду:";
            try
            {
                await _botClient.SendTextMessageAsync (
                    chatId: chatId,
                    text: message,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: GetMainKeyboard ()
                );
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"SendWelcomeMessage error: {ex.Message}"); }
        }

        /// <summary>Отправка текстового сообщения.</summary>
        public async Task SendMessageAsync(string text, string targetChatId = null)
        {
            if (!_enabled) return;
            try
            {
                await _botClient.SendTextMessageAsync (targetChatId ?? _chatId, text, parseMode: ParseMode.Html);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Telegram send error: {ex.Message}"); }
        }

        /// <summary>Отправка изображения (заглушка).</summary>
        public async Task SendPhotoAsync(string chatId, System.IO.Stream photoStream, string caption = null)
        {
            await SendMessageAsync ($"📊 График временно недоступен. Используйте /performance для статистики. (caption: {caption})", chatId);
        }

        /// <summary>Запуск прослушивания входящих сообщений.</summary>
        public void StartListening(Func<string, string, Task> onCommandReceived)
        {
            if (_commandHandler != null) return;
            _commandHandler = onCommandReceived;
            _cts = new CancellationTokenSource ();
            _ = Task.Run (() => ListenLoop (_cts.Token));
            _ = Task.Run (async () => { await Task.Delay (2000); await SendWelcomeMessageAsync (_chatId); });
        }

        public void StopListening() => _cts?.Cancel ();

        private async Task ListenLoop(CancellationToken token)
        {
            int offset = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var updates = await _botClient.GetUpdatesAsync (offset: offset, timeout: 30, cancellationToken: token);
                    foreach (var update in updates)
                    {
                        offset = update.Id + 1;

                        if (update.CallbackQuery != null)
                        {
                            await HandleCallbackQuery (update.CallbackQuery);
                            continue;
                        }

                        if (update.Message?.Text != null)
                        {
                            string text = update.Message.Text.Trim ();
                            string chatId = update.Message.Chat.Id.ToString ();

                            // Преобразуем русские тексты кнопок в команды
                            string command = text switch
                            {
                                "📊 Статус" => "/status",
                                "💼 Баланс" => "/balance",
                                "🧠 Переобучить ML" => "/retrain",
                                "📁 Экспорт" => "/export",
                                "▶️ Запуск" => "/start",
                                "⏹️ Стоп" => "/stop",
                                "📈 График PnL" => "/chart",
                                "🔄 Обновить" => "/update",
                                "❓ Помощь" => "/help",
                                _ => text
                            };

                            await _commandHandler?.Invoke (command, chatId);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine ($"Telegram listen error: {ex.Message}");
                    await Task.Delay (5000, token);
                }
            }
        }

        /// <summary>
        /// Проверяет, не является ли команда дубликатом (дребезг)
        /// </summary>
        private bool IsDuplicateCommand(string chatId, string command)
        {
            string key = $"{chatId}:{command}";

            if (_lastCommandTime.TryGetValue (key, out var lastTime) &&
                DateTime.UtcNow - lastTime < _commandCooldown)
            {
                System.Diagnostics.Debug.WriteLine ($"⚠️ Дребезг: игнорирую повторную команду {command}");
                return true;
            }

            _lastCommandTime[key] = DateTime.UtcNow;

            if (_lastCommandTime.Count > 1000)
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes (5);
                foreach (var kv in _lastCommandTime.Where (x => x.Value < cutoff).ToList ())
                {
                    _lastCommandTime.TryRemove (kv.Key, out _);
                }
            }

            return false;
        }

        private async Task HandleCallbackQuery(CallbackQuery callbackQuery)
        {
            if (callbackQuery == null) return;

            var chatId = callbackQuery.Message.Chat.Id.ToString ();
            var data = callbackQuery.Data;

            await _botClient.AnswerCallbackQueryAsync (callbackQuery.Id);

            string command = data switch
            {
                "status" => "/status",
                "balance" => "/balance",
                "retrain" => "/retrain",
                "export" => "/export",
                "start_bot" => "/start",
                "stop_bot" => "/stop",
                "pnl_chart" => "/chart",
                "help" => "/help",
                "optimize" => "/optimize",
                "update" => "/update",
                _ => null
            };

            if (!string.IsNullOrEmpty (command))
                await _commandHandler?.Invoke (command, chatId);
        }

        /// <summary>Уведомление о сделке.</summary>
        public async Task SendTradeNotification(string symbol, string action, decimal price, decimal quantity, decimal pnl = 0, string reason = "")
        {
            string emoji = action == "BUY" ? "🟢" : "🔴";
            string pnlText = pnl != 0 ? $"\n💰 PnL: {( pnl >= 0 ? "+" : "" )}{pnl:F2} USDC" : "";
            string msg = $"{emoji} <b>{action}</b> {symbol}\n" +
                         $"💵 Цена: {price:F4}\n" +
                         $"📦 Кол-во: {quantity:F6}" +
                         pnlText +
                         ( string.IsNullOrEmpty (reason) ? "" : $"\n📝 {reason}" );
            await SendMessageAsync (msg);
        }

        /// <summary>Уведомление об ошибке.</summary>
        public async Task SendErrorNotification(string error) =>
            await SendMessageAsync ($"⚠️ <b>Ошибка бота</b>\n<code>{error}</code>");

        /// <summary>Ежедневный отчёт.</summary>
        public async Task SendDailyReport(decimal totalPnL, decimal winRate, int totalTrades, int winningTrades, int losingTrades)
        {
            string msg = $"📊 <b>Ежедневный отчёт</b>\n" +
                         $"💰 Общий PnL: {totalPnL:F2} USDC\n" +
                         $"🎯 Win Rate: {winRate:F1}%\n" +
                         $"📈 Сделок: {totalTrades} (✅{winningTrades} / ❌{losingTrades})";
            await SendMessageAsync (msg);
        }

        /// <summary>Тест подключения</summary>
        public async Task<bool> TestConnectionAsync()
        {
            if (!_enabled) return false;
            try
            {
                await SendMessageAsync ("✅ Бот подключён к Telegram");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}