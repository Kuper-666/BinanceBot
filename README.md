# BinanceBotWpf

Автоматический торговый бот для Binance с ИИ-стратегиями, ML-сигналами, Telegram-уведомлениями и live-дашбордом.

## Возможности

- **Торговля на споте** — автоматические покупки/продажи по SMA crossover + RSI + MACD + Bollinger Bands
- **3-эшелонная ИИ-архитектура:**
  - Эшелон 1: AdaptiveAgent — адаптация к волатильности рынка
  - Эшелон 2: SignalValidator — валидация сигналов (ONNX / эвристика)
  - Эшелон 3: NewsSentinel — фильтр по новостному фону
- **Мульти-таймфрейм анализ** — основной сигнал на 1h, подтверждение входа на 5m
- **Управление рисками** — динамический стоп-лосс, тейк-профит, trailing stop, maxSize позиции
- **Сеточный бот (Grid Bot)** — автоматическая сетка ордеров
- **DCA (усреднение)** — долларово-костовое усреднение
- **Volume Breakout** — стратегия пробоя объёма
- **Telegram-бот** — уведомления, статистика, управление командами
- **Live-дашборд** — WebSocket-дашборд с графиками (порт 8765)
- **Фильтр сессий** — EU/US/Asia с возможностью ограничения торговли
- **Котировка USDC/USDT** — переключение между парами
- **Fear & Greed Index** — блокировка сделок при Extreme Greed
- **Whale Monitor** — мониторинг крупных сделок
- **Бэктестинг** — тестирование стратегий на исторических данных
- **Автоматическая оптимизация** — подбор параметров стратегии

## Требования

- Windows 10/11
- .NET 8.0 SDK
- Binance API ключ (现货)

## Установка

```bash
# Клонируйте репозиторий
git clone https://github.com/Kuper-666/BinanceBot.git
cd BinanceBot

# Соберите проект
dotnet build BinanceBotWpf.sln

# Запустите бота
dotnet run --project BinanceBotWpf/BinanceBotWpf.csproj
```

## Настройка

1. При первом запуске создастся `config.json` с дефолтными значениями
2. Заполните `ApiKey` и `ApiSecret` — получить можно в [Binance API Management](https://www.binance.com/en/my/settings/api-management)
3. **Важно:** ограничьте права API — разрешите только спот-торговлю, отключите вывод средств
4. Перезапустите бота

### config.json

```json
{
  "ApiKey": "ваш_api_ключ",
  "ApiSecret": "ваш_api_секрет",
  "IsTestnet": false,
  "TelegramBotToken": "токен_бота_из_BotFather",
  "TelegramChatId": "ваш_chat_id",
  "MinUsdcBalance": 5.50
}
```

### Trading Settings (в UI)

Настройки стратегии сохраняются в `Data/trading_settings.json`:

- SMA периоды (по умолчанию Fast=9, Slow=21)
- RSI пороги (Buy < 30, Sell > 70)
- Риск на сделку (по умолчанию 1%)
- Максимум открытых позиций
- Таймфреймы (основной 1h, вход 5m)
- Котировка (USDC / USDT / Both)

## Архитектура

```
BinanceBotWpf/              # Основной WPF проект
├── Models/                  # Данные: BotConfig, BinanceClient, TechnicalAnalysis
├── Services/                # Бизнес-логика: TradingStrategy, RiskCalculator, etc.
├── ViewModels/              # MVVM: MainWindowViewModel
├── Views/                   # XAML: MainWindow.xaml
└── App.xaml.cs              # Точка входа, инициализация

BinanceBotWpf.Tests/         # Unit-тесты (xUnit)
DataDownloader/              # CLI-утилита для скачивания исторических данных
binance-dashboard/           # React-дашборд (отдельный проект)
```

### Ключевые сервисы

| Сервис | Назначение |
|---|---|
| `TradingService` | Основной цикл торговли |
| `TradingStrategy` | Генерация сигналов (SMA+RSI+MACD+BB+LSMA) |
| `StrategyEngine` | Базовый SMA crossover |
| `RiskCalculator` | Расчёт размера позиции и рисков |
| `AiRiskEngine` | ИИ-оценка рисков |
| `AdaptiveAgent` | Адаптация к волатильности (ATR+ADX) |
| `SignalValidator` | Валидация сигналов (ONNX/эвристика) |
| `NewsSentinel` | Новостной фильтр (SQLite) |
| `BinanceClient` | HTTP-клиент Binance API |
| `PositionProtector` | Защита позиций (SL/TP/Trailing) |
| `MarketSessionService` | Определение рыночной сессии |

## Тестирование

```bash
# Запуск всех тестов
dotnet test BinanceBotWpf.Tests/BinanceBotWpf.Tests.csproj

# Тесты покрывают:
# - TechnicalAnalysis (SMA, RSI, MACD, BB, LSMA, ATR, OBV)
# - StrategyEngine (Golden/Death Cross)
# - TradingStrategy (сигналы, индикаторы, multi-TF confirmation)
# - SignalValidator (ONNX и эвристика)
# - RiskManager (лимиты позиций, дневной лимит)
# - RiskCalculator (размер позиции, динамический риск)
# - AiRiskEngine (stp-loss, размер, grid)
# - BotConfig (шифрование, миграция, сериализация)
# - BacktestEngine (нулевые сделки, drawdown, win rate)
# - AdaptiveAgent (фактор, регим, ADX)
# - FileLogger (создание, запись, dispose)
# - Dashboard (формат JSON, каналы, данные)
# - MarketSessionService (EU/US/Asia/Overlap)
```

## Торговые сессии

| Сессия | Время (UTC) | Особенности |
|---|---|---|
| Asia | 00:00–08:00 | Низкая волатильность |
| Europe | 07:00–16:00 | Высокий объём |
| US | 13:00–22:00 | Высокий объём |
| EU+US Overlap | 13:00–16:00 | Максимальный объём |

## Команды Telegram

| Команда | Описание |
|---|---|
| `/start` | Запуск торговли |
| `/stop` | Остановка торговли |
| `/status` | Текущий статус |
| `/balance` | Баланс |
| `/stats` | Статистика сделок |
| `/pnl` | График PnL |

## Безопасность

- API ключи шифруются через DPAPI (привязаны к Windows-аккаунту)
- В логах ключи маскируются (показываются только последние 4 символа)
- Рекомендуется: отключить вывод средств, ограничить IP, разрешить только спот-торговлю

## Лицензия

MIT
