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
    public class TelegramNotifier
    {
        private readonly TelegramBotClient _botClient;
        private readonly string _chatId;
        private bool _enabled;
        private Func<string, string, Task> _commandHandler;
        private CancellationTokenSource _cts;

        public string GetChatId() => _chatId;
        public bool IsEnabled => _enabled;

        private readonly ConcurrentDictionary<string, DateTime> _lastCommandTime = new ();
        private readonly TimeSpan _commandCooldown = TimeSpan.FromSeconds (2);

        public event Action<bool, string> OnStatusChanged; // (isEnabled, message)

        public TelegramNotifier(string botToken, string chatId)
        {
            _chatId = chatId;
            _enabled = false;

            if (string.IsNullOrEmpty (botToken) || string.IsNullOrEmpty (chatId))
                return;

            try
            {
                _botClient = new TelegramBotClient (botToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"Telegram init error: {ex.Message}");
                OnStatusChanged?.Invoke (false, $"Ошибка инициализации Telegram: {ex.Message}");
            }
        }

        public async Task SendMessageAsync(string text, string targetChatId = null)
        {
            if (!_enabled) return;
            try
            {
                await _botClient.SendTextMessageAsync (targetChatId ?? _chatId, text, parseMode: ParseMode.Html);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Telegram send error: {ex.Message}"); }
        }

        public void StartListening(Func<string, string, Task> onCommandReceived)
        {
            if (_botClient == null || _commandHandler != null) return;
            _commandHandler = onCommandReceived;
            _cts = new CancellationTokenSource ();

            _ = Task.Run (async () =>
            {
                try
                {
                    // Асинхронно проверяем подключение без зависания UI-потока
                    var me = await _botClient.GetMeAsync ();
                    _enabled = true;
                    System.Diagnostics.Debug.WriteLine ($"Telegram бот @{me.Username} готов");
                    OnStatusChanged?.Invoke (true, $"Подключён бот @{me.Username}");

                    // Запуск цикла получения обновлений
                    _ = Task.Run (() => ListenLoop (_cts.Token));

                    // Отправка приветствия
                    await Task.Delay (2000);
                    await SendWelcomeMessageAsync (_chatId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine ($"Telegram connection failed: {ex.Message}");
                    OnStatusChanged?.Invoke (false, $"Не удалось подключиться к Telegram: {ex.Message}");
                }
            });
        }

        public async Task<bool> TestConnectionAsync()
        {
            if (!_enabled) return false;
            try
            {
                await SendMessageAsync ("✅ Бот подключён к Telegram");
                return true;
            }
            catch { return false; }
        }

        public async Task SendTradeNotification(string symbol, string action, decimal price, decimal quantity, decimal pnl = 0, string reason = "")
        {
            if (!_enabled) return;
            string emoji = action == "BUY" ? "🟢" : "🔴";
            string pnlText = pnl != 0 ? $"\n💰 PnL: {( pnl >= 0 ? "+" : "" )}{pnl:F2} USDC" : "";
            string msg = $"{emoji} <b>{action}</b> {symbol}\n" +
                         $"💵 Цена: {price:F4}\n" +
                         $"📦 Кол-во: {quantity:F6}" +
                         pnlText +
                         ( string.IsNullOrEmpty (reason) ? "" : $"\n📝 {reason}" );
            await SendMessageAsync (msg);
        }

        public async Task SendErrorNotification(string error) =>
            await SendMessageAsync ($"⚠️ <b>Ошибка бота</b>\n<code>{error}</code>");

        public async Task SendDailyReport(decimal totalPnL, decimal winRate, int totalTrades, int winningTrades, int losingTrades)
        {
            if (!_enabled) return;
            string msg = $"📊 <b>Ежедневный отчёт</b>\n" +
                         $"💰 Общий PnL: {totalPnL:F2} USDC\n" +
                         $"🎯 Win Rate: {winRate:F1}%\n" +
                         $"📈 Сделок: {totalTrades} (✅{winningTrades} / ❌{losingTrades})";
            await SendMessageAsync (msg);
        }

        private async Task SendWelcomeMessageAsync(string chatId)
        {
            if (!_enabled) return;
            var message = "🤖 *Binance Trading Bot*\n\n" +
                          "Я автоматический торговый бот на базе SMA и ML.\n" +
                          "Выберите действие (кнопки внизу) или введите команду:";
            try
            {
                await _botClient.SendTextMessageAsync (chatId, message,
                    parseMode: ParseMode.Markdown, replyMarkup: GetMainKeyboard ());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"SendWelcomeMessage error: {ex.Message}"); }
        }

        private ReplyKeyboardMarkup GetMainKeyboard()
        {
            return new ReplyKeyboardMarkup (new[]
            {
                new KeyboardButton[] { "📊 Статус", "💼 Баланс" },
                new KeyboardButton[] { "🧠 Переобучить ML", "📁 Экспорт" },
                new KeyboardButton[] { "▶️ Запуск", "⏹️ Стоп" },
                new KeyboardButton[] { "📈 График PnL", "🔄 Обновить" },
                new KeyboardButton[] { "❓ Помощь" }
            })
            { ResizeKeyboard = true, OneTimeKeyboard = false };
        }

        private async Task ListenLoop(CancellationToken token)
        {
            int offset = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var updates = await _botClient.GetUpdatesAsync (offset, timeout: 30, cancellationToken: token);
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

        private async Task HandleCallbackQuery(CallbackQuery callbackQuery)
        {
            if (callbackQuery == null) return;
            await _botClient.AnswerCallbackQueryAsync (callbackQuery.Id);
            string chatId = callbackQuery.Message.Chat.Id.ToString ();
            string command = callbackQuery.Data switch
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
    }
}