using System;
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
        private readonly bool _enabled;
        private Func<string, string, Task> _commandHandler;
        private CancellationTokenSource _cts;

        public TelegramNotifier(string botToken, string chatId)
        {
            _botClient = new TelegramBotClient (botToken);
            _chatId = chatId;
            _enabled = !string.IsNullOrEmpty (botToken) && !string.IsNullOrEmpty (chatId);
        }

        public ReplyKeyboardMarkup GetMainKeyboard()
        {
            return new ReplyKeyboardMarkup (new[]
            {
                new KeyboardButton[] { "📊 Статус", "💼 Баланс" },
                new KeyboardButton[] { "🧠 Переобучить ML", "📁 Экспорт" },
                new KeyboardButton[] { "▶️ Запуск", "⏹️ Стоп" },
                new KeyboardButton[] { "📈 График PnL", "❓ Помощь" }
            })
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = false
            };
        }

        public async Task SendWelcomeMessageAsync(string chatId)
        {
            if (!_enabled) return;
            var message = "🤖 *Binance Trading Bot*\n\n" +
                          "Я автоматический торговый бот на базе SMA и ML.\n" +
                          "Выберите действие (кнопки внизу) или введите команду:";
            try
            {
                await _botClient.SendMessage (chatId, message, parseMode: ParseMode.Markdown, replyMarkup: GetMainKeyboard ());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"SendWelcomeMessage error: {ex.Message}"); }
        }

        public async Task SendMessageAsync(string text, string targetChatId = null)
        {
            if (!_enabled) return;
            try
            {
                await _botClient.SendMessage (targetChatId ?? _chatId, text, parseMode: ParseMode.Html);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Telegram send error: {ex.Message}"); }
        }

        public void StartListening(Func<string, string, Task> onCommandReceived)
        {
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
                    var updates = await _botClient.GetUpdates (offset: offset, timeout: 30, cancellationToken: token);
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
                            await _commandHandler?.Invoke (text, chatId);
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
            var chatId = callbackQuery.Message.Chat.Id.ToString ();
            var data = callbackQuery.Data;
            await _botClient.AnswerCallbackQuery (callbackQuery.Id);
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
                _ => null
            };
            if (!string.IsNullOrEmpty (command))
                await _commandHandler?.Invoke (command, chatId);
        }

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

        public async Task SendErrorNotification(string error) =>
            await SendMessageAsync ($"⚠️ <b>Ошибка бота</b>\n<code>{error}</code>");

        public async Task SendDailyReport(decimal totalPnL, decimal winRate, int totalTrades, int winningTrades, int losingTrades)
        {
            string msg = $"📊 <b>Ежедневный отчёт</b>\n" +
                         $"💰 Общий PnL: {totalPnL:F2} USDC\n" +
                         $"🎯 Win Rate: {winRate:F1}%\n" +
                         $"📈 Сделок: {totalTrades} (✅{winningTrades} / ❌{losingTrades})";
            await SendMessageAsync (msg);
        }
    }
}