using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BacktestRunner
{
    class Program
    {
        static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds (30) };

        static async Task Main (string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            string[] pairs = { "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT" };
            string interval = "1h";
            int candles = 2200;
            decimal capital = 1000m;

            foreach (string arg in args)
            {
                if (arg.StartsWith ("--pairs="))
                {
                    pairs = arg.Substring ("--pairs=".Length).Split (',');
                }
                else if (arg.StartsWith ("--interval="))
                {
                    interval = arg.Substring ("--interval=".Length);
                }
                else if (arg.StartsWith ("--candles=") && int.TryParse (arg.Substring ("--candles=".Length), out int c))
                {
                    candles = c;
                }
                else if (arg.StartsWith ("--capital=") && decimal.TryParse (arg.Substring ("--capital=".Length), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal cap))
                {
                    capital = cap;
                }
            }

            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ("  БЭКТЕСТИНГ: Базовая стратегия vs Золотая архитектура (3 мес.)");
            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ();

            List<BacktestResult> allBasicResults = new List<BacktestResult> ();
            List<BacktestResult> allEnhancedResults = new List<BacktestResult> ();
            List<(string Pair, string Strategy, List<decimal> Curve)> allCurves = new List<(string, string, List<decimal>)> ();

            foreach (string pair in pairs)
            {
                Console.Write ($"📥 Загрузка {pair}... ");
                List<Kline> klines = await DownloadKlinesAsync (pair, interval, candles);
                Console.WriteLine ($"{klines.Count} свечей ({klines.First ().OpenTime:dd.MM} – {klines.Last ().OpenTime:dd.MM.yyyy})");

                if (klines.Count < 200)
                {
                    Console.WriteLine ("  ⚠️ Мало данных, пропуск");
                    continue;
                }

                BacktestResult basicResult = RunBasicSmaStrategy (klines, capital);
                BacktestResult enhancedResult = RunEnhancedStrategy (klines, capital);

                allBasicResults.Add (basicResult);
                allEnhancedResults.Add (enhancedResult);
                allCurves.Add ((pair, "basic", basicResult.EquityCurve));
                allCurves.Add ((pair, "enhanced", enhancedResult.EquityCurve));

                Console.WriteLine ($"  Базовая:  доходность={basicResult.TotalReturn,7:F2}%  winRate={basicResult.WinRate,5:F1}%  сделок={basicResult.TotalTrades,3}  maxDD={basicResult.MaxDrawdown,5:F2}%  Sharpe={basicResult.SharpeRatio,5:F2}  maxWinStreak={basicResult.MaxWinStreak}  maxLoseStreak={basicResult.MaxLoseStreak}  avgBars={basicResult.AvgTradeBars:F1}");
                Console.WriteLine ($"  Улучш.:   доходность={enhancedResult.TotalReturn,7:F2}%  winRate={enhancedResult.WinRate,5:F1}%  сделок={enhancedResult.TotalTrades,3}  maxDD={enhancedResult.MaxDrawdown,5:F2}%  Sharpe={enhancedResult.SharpeRatio,5:F2}  maxWinStreak={enhancedResult.MaxWinStreak}  maxLoseStreak={enhancedResult.MaxLoseStreak}  avgBars={enhancedResult.AvgTradeBars:F1}");
                Console.WriteLine ();
            }

            if (allBasicResults.Count == 0)
            {
                Console.WriteLine ("❌ Нет данных для бэктеста");
                return;
            }

            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ("  АГРЕГИРОВАННЫЕ РЕЗУЛЬТАТЫ (средние по всем парам)");
            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ();
            PrintAggregated ("Базовая SMA+RSI", allBasicResults);
            PrintAggregated ("Золотая архитектура (LSMA+Adaptive+Validator)", allEnhancedResults);

            Console.WriteLine ();
            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ("  СРАВНЕНИЕ");
            Console.WriteLine ("════════════════════════════════════════════════════════════════");

            decimal basicAvg = allBasicResults.Average (r => r.TotalReturn);
            decimal enhancedAvg = allEnhancedResults.Average (r => r.TotalReturn);
            decimal basicWinRate = allBasicResults.Average (r => r.WinRate);
            decimal enhancedWinRate = allEnhancedResults.Average (r => r.WinRate);
            decimal basicDD = allBasicResults.Average (r => r.MaxDrawdown);
            decimal enhancedDD = allEnhancedResults.Average (r => r.MaxDrawdown);

            Console.WriteLine ($"  Доходность:    {basicAvg,7:F2}% → {enhancedAvg,7:F2}%  ({(enhancedAvg - basicAvg):+.F2;-F2}%)");
            Console.WriteLine ($"  Win Rate:       {basicWinRate,6:F1}% → {enhancedWinRate,6:F1}%  ({(enhancedWinRate - basicWinRate):+.F1;-F1}%)");
            Console.WriteLine ($"  Max Drawdown:   {basicDD,6:F2}% → {enhancedDD,6:F2}%  ({(enhancedDD - basicDD):+.F2;-F2}%)");

            int pairsImproved = 0;
            for (int i = 0; i < allBasicResults.Count; i++)
            {
                if (allEnhancedResults[i].TotalReturn > allBasicResults[i].TotalReturn)
                    pairsImproved++;
            }
            Console.WriteLine ($"  Пар с улучшением: {pairsImproved}/{allBasicResults.Count}");
            Console.WriteLine ();

            // ═══ Equity Curve CSV Export ═══
            string csvPath = Path.Combine (AppContext.BaseDirectory, "..", "..", "..", "..", "backtest_results.csv");
            using (StreamWriter writer = new StreamWriter (csvPath))
            {
                writer.WriteLine ("pair,strategy,equity_value");
                foreach ((string Pair, string Strategy, List<decimal> Curve) entry in allCurves)
                {
                    foreach (decimal value in entry.Curve)
                    {
                        writer.WriteLine ($"{entry.Pair},{entry.Strategy},{value.ToString (CultureInfo.InvariantCulture)}");
                    }
                }
            }
            Console.WriteLine ($"📁 Equity curves saved to: {Path.GetFullPath (csvPath)}");
            Console.WriteLine ();

            // ═══ JSON Report ═══
            var report = new
            {
                timestamp = DateTime.UtcNow.ToString ("o"),
                pairs,
                interval,
                candles,
                capital,
                basic = PrintAggregatedJson ("Basic SMA+RSI", allBasicResults),
                enhanced = PrintAggregatedJson ("Golden Architecture", allEnhancedResults),
                comparison = new
                {
                    returnDelta = enhancedAvg - basicAvg,
                    winRateDelta = enhancedWinRate - basicWinRate,
                    drawdownDelta = enhancedDD - basicDD,
                    pairsImproved,
                    totalPairs = allBasicResults.Count
                }
            };
            string jsonPath = Path.Combine (AppContext.BaseDirectory, "..", "..", "..", "..", "backtest_report.json");
            await File.WriteAllTextAsync (jsonPath, JsonSerializer.Serialize (report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine ($"📋 JSON report saved to: {Path.GetFullPath (jsonPath)}");
            Console.WriteLine ();

            // ═══ Parameter sweep ═══
            Dictionary<string, List<Kline>> allData = new Dictionary<string, List<Kline>> ();
            foreach (string pair in pairs)
            {
                Console.Write ($"📥 Перезагрузка {pair} для sweep... ");
                List<Kline> data = await DownloadKlinesAsync (pair, interval, candles);
                Console.WriteLine ($"{data.Count} свечей");
                if (data.Count >= 200)
                {
                    allData[pair] = data;
                }
            }
            await RunParameterSweep (allData, capital);
        }

        static void PrintAggregated (string label, List<BacktestResult> results)
        {
            decimal avgReturn = results.Average (r => r.TotalReturn);
            decimal avgWinRate = results.Average (r => r.WinRate);
            int totalTrades = results.Sum (r => r.TotalTrades);
            int totalWins = results.Sum (r => r.WinningTrades);
            decimal avgDD = results.Average (r => r.MaxDrawdown);
            decimal avgSharpe = results.Average (r => r.SharpeRatio);
            decimal avgPF = results.Where (r => r.ProfitFactor > 0).DefaultIfEmpty (new BacktestResult () { ProfitFactor = 0 }).Average (r => r.ProfitFactor);
            decimal avgMaxWinStreak = results.Average (r => (decimal)r.MaxWinStreak);
            decimal avgMaxLoseStreak = results.Average (r => (decimal)r.MaxLoseStreak);
            decimal avgTradeBars = results.Average (r => r.AvgTradeBars);
            decimal calmar = avgDD > 0 ? avgReturn / avgDD : 0;

            Console.WriteLine ($"  [{label}]");
            Console.WriteLine ($"    Средняя доходность:  {avgReturn,7:F2}%");
            Console.WriteLine ($"    Средний win rate:    {avgWinRate,5:F1}%");
            Console.WriteLine ($"    Всего сделок:        {totalTrades}");
            Console.WriteLine ($"    Прибыльных:          {totalWins}");
            Console.WriteLine ($"    Средний Max DD:      {avgDD,5:F2}%");
            Console.WriteLine ($"    Средний Sharpe:      {avgSharpe,5:F2}");
            Console.WriteLine ($"    Средний Calmar:      {calmar,5:F2}");
            Console.WriteLine ($"    Средний PF:          {avgPF,5:F2}");
            Console.WriteLine ($"    Средний Max Win Streak:  {avgMaxWinStreak:F1}");
            Console.WriteLine ($"    Средний Max Lose Streak: {avgMaxLoseStreak:F1}");
            Console.WriteLine ($"    Средняя длительность:    {avgTradeBars:F1} баров");
            Console.WriteLine ();
        }

        static object PrintAggregatedJson (string label, List<BacktestResult> results)
        {
            return new
            {
                label,
                avgReturn = results.Average (r => r.TotalReturn),
                avgWinRate = results.Average (r => r.WinRate),
                totalTrades = results.Sum (r => r.TotalTrades),
                totalWins = results.Sum (r => r.WinningTrades),
                avgMaxDrawdown = results.Average (r => r.MaxDrawdown),
                avgSharpe = results.Average (r => r.SharpeRatio),
                avgCalmar = results.Average (r => r.MaxDrawdown > 0 ? r.TotalReturn / r.MaxDrawdown : 0),
                avgProfitFactor = results.Where (r => r.ProfitFactor > 0).DefaultIfEmpty (new BacktestResult () { ProfitFactor = 0 }).Average (r => r.ProfitFactor),
                avgMaxWinStreak = results.Average (r => (decimal)r.MaxWinStreak),
                avgMaxLoseStreak = results.Average (r => (decimal)r.MaxLoseStreak),
                avgTradeBars = results.Average (r => r.AvgTradeBars)
            };
        }

        static Task RunParameterSweep (Dictionary<string, List<Kline>> allData, decimal capital)
        {
            Console.WriteLine ();
            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ("  ПОДБОР ОПТИМАЛЬНЫХ ПОРОГОВ (parameter sweep)");
            Console.WriteLine ("════════════════════════════════════════════════════════════════");
            Console.WriteLine ();

            decimal bestScore = -999999;
            decimal bestVolThresh = 0, bestAtrThresh = 0, bestSlMult = 0, bestPeriodMult = 0;
            int bestRsiLow = 0, bestRsiHigh = 0;
            int totalCombos = 0;

            decimal[] volThresholds = new decimal[] { 6m, 8m, 10m };
            decimal[] atrThresholds = new decimal[] { 0.12m, 0.15m, 0.20m };
            decimal[] slMultipliers = new decimal[] { 0.3m, 0.4m, 0.5m };
            decimal[] periodMultipliers = new decimal[] { 0.2m, 0.3m, 0.4m };
            int[] rsiLowBounds = new int[] { 18, 20 };
            int[] rsiHighBounds = new int[] { 80, 82 };

            foreach (decimal volT in volThresholds)
            foreach (decimal atrT in atrThresholds)
            foreach (decimal slM in slMultipliers)
            foreach (decimal perM in periodMultipliers)
            foreach (int rsiL in rsiLowBounds)
            foreach (int rsiH in rsiHighBounds)
            {
                totalCombos++;
                List<BacktestResult> results = new List<BacktestResult> ();

                foreach (KeyValuePair<string, List<Kline>> kvp in allData)
                {
                    BacktestResult r = RunCalibratedBacktest (kvp.Value, volT, atrT, slM, perM, rsiL, rsiH, capital);
                    results.Add (r);
                }

                decimal avgReturn = results.Average (r => r.TotalReturn);
                decimal avgDD = results.Average (r => r.MaxDrawdown);
                int totalTrades = results.Sum (r => r.TotalTrades);

                decimal score = avgReturn - 0.5m * avgDD;
                if (totalTrades >= 30)
                {
                    score += 1m;
                }

                if (score > bestScore && totalTrades >= 20)
                {
                    bestScore = score;
                    bestVolThresh = volT;
                    bestAtrThresh = atrT;
                    bestSlMult = slM;
                    bestPeriodMult = perM;
                    bestRsiLow = rsiL;
                    bestRsiHigh = rsiH;
                }
            }

            Console.WriteLine ($"  Перебрано комбинаций: {totalCombos}");
            Console.WriteLine ();
            Console.WriteLine ($"  🏆 ОПТИМАЛЬНЫЕ ПОРОГИ:");
            Console.WriteLine ($"    Volume Ratio threshold:   {bestVolThresh}");
            Console.WriteLine ($"    ATR % threshold:          {bestAtrThresh}");
            Console.WriteLine ($"    SL multiplier:            {bestSlMult}");
            Console.WriteLine ($"    Period adaptation:        {bestPeriodMult}");
            Console.WriteLine ($"    RSI bounds:               {bestRsiLow}/{bestRsiHigh}");
            Console.WriteLine ($"    Score:                    {bestScore:F2}");
            Console.WriteLine ();

            Console.WriteLine ("  Результаты с оптимальными порогами:");
            List<BacktestResult> optResults = new List<BacktestResult> ();
            foreach (KeyValuePair<string, List<Kline>> kvp in allData)
            {
                BacktestResult r = RunCalibratedBacktest (kvp.Value, bestVolThresh, bestAtrThresh, bestSlMult, bestPeriodMult, bestRsiLow, bestRsiHigh, capital);
                optResults.Add (r);
                Console.WriteLine ($"    {kvp.Key}: доходность={r.TotalReturn,7:F2}%  winRate={r.WinRate,5:F1}%  сделок={r.TotalTrades,3}  maxDD={r.MaxDrawdown,5:F2}%  maxWinStreak={r.MaxWinStreak}  maxLoseStreak={r.MaxLoseStreak}  avgBars={r.AvgTradeBars:F1}");
            }
            Console.WriteLine ();
            PrintAggregated ("Оптимальная конфигурация", optResults);
            return Task.CompletedTask;
        }

        static BacktestResult RunCalibratedBacktest (
            List<Kline> klines, decimal volThresh, decimal atrThresh,
            decimal slMult, decimal periodMult, int rsiLow, int rsiHigh,
            decimal capital, decimal commission = 0.0004m)
        {
            List<decimal> closes = klines.Select (k => k.Close).ToList ();
            List<decimal> highs = klines.Select (k => k.High).ToList ();
            List<decimal> lows = klines.Select (k => k.Low).ToList ();
            List<decimal> volumes = klines.Select (k => k.Volume).ToList ();

            int fastPeriod = 9, slowPeriod = 21, rsiPeriod = 14;
            decimal stopLoss = 0.02m, takeProfit = 0.04m;

            List<decimal> fastSma = CalcSMA (closes, fastPeriod);
            List<decimal> slowSma = CalcSMA (closes, slowPeriod);
            List<decimal> rsi = CalcRSI (closes, rsiPeriod);
            List<decimal> atrList = CalcATR (highs, lows, closes, 14);
            (List<decimal> upper, List<decimal> lower) bb = CalcBollingerBands (closes, 20);
            List<decimal> macd = CalcMACD (closes);
            List<decimal> lsma = CalcLSMA (closes, 20);

            decimal capitalStart = capital;
            decimal position = 0, entryPrice = 0, peakCapital = capital, maxDrawdown = 0;
            int winning = 0, losing = 0;
            int tradeBars = 0;
            int totalTradeBars = 0;
            int currentWinStreak = 0, currentLoseStreak = 0;
            int maxWinStreak = 0, maxLoseStreak = 0;
            List<decimal> equityCurve = new List<decimal> { capital };

            int startIdx = slowPeriod + 10;

            for (int i = startIdx; i < closes.Count; i++)
            {
                decimal price = closes[i];
                bool buySignal = false, sellSignal = false;

                int effectiveFast = fastPeriod;
                int effectiveSlow = slowPeriod;
                decimal adaptiveSl = stopLoss;
                decimal adaptiveTp = takeProfit;

                if (i >= 100)
                {
                    List<decimal> recentAtr = atrList.Skip (Math.Max (0, i - 100)).Take (100).Where (v => v > 0).ToList ();
                    List<decimal> olderAtr = atrList.Skip (Math.Max (0, i - 200)).Take (Math.Max (0, Math.Min (100, i - 100))).Where (v => v > 0).ToList ();

                    if (recentAtr.Count > 10 && olderAtr.Count > 10)
                    {
                        decimal atrRatio = olderAtr.Average () > 0 ? recentAtr.Last () / olderAtr.Average () : 1m;
                        decimal af = Math.Clamp (MapRange (atrRatio, 0.5m, 2.0m, 0.7m, 1.5m), 0.5m, 1.5m);
                        effectiveFast = Math.Max (3, (int)Math.Round (fastPeriod * (1m + (af - 1m) * periodMult)));
                        effectiveSlow = Math.Max (effectiveFast + 3, (int)Math.Round (slowPeriod * (1m + (af - 1m) * periodMult)));
                        adaptiveSl = stopLoss * (1m + (af - 1m) * slMult);
                        adaptiveTp = takeProfit * (1m + (af - 1m) * slMult);

                        if (effectiveFast != fastPeriod || effectiveSlow != slowPeriod)
                        {
                            List<decimal> aF = CalcSMA (closes, effectiveFast);
                            List<decimal> aS = CalcSMA (closes, effectiveSlow);
                            if (aF[i] > 0 && aS[i] > 0)
                            {
                                fastSma[i] = aF[i];
                                slowSma[i] = aS[i];
                            }
                        }
                    }
                }

                if (i > 0 && fastSma[i] > 0 && slowSma[i] > 0)
                {
                    bool crossUp = fastSma[i - 1] <= slowSma[i - 1] && fastSma[i] > slowSma[i];
                    bool crossDown = fastSma[i - 1] >= slowSma[i - 1] && fastSma[i] < slowSma[i];
                    if (crossUp)
                    {
                        bool c = rsi[i] < 40 || price <= bb.lower[i] || (macd[i] > 0 && macd[i] > macd[Math.Max (0, i - 1)]);
                        if (c)
                        {
                            buySignal = true;
                        }
                    }
                    if (crossDown)
                    {
                        bool c = rsi[i] > 60 || price >= bb.upper[i] || (macd[i] < 0 && macd[i] < macd[Math.Max (0, i - 1)]);
                        if (c)
                        {
                            sellSignal = true;
                        }
                    }
                }

                if (lsma[i] > 0 && i > 1)
                {
                    bool up = closes[i] > lsma[i] && lsma[i] > lsma[i - 1];
                    bool down = closes[i] < lsma[i] && lsma[i] < lsma[i - 1];
                    if (buySignal && down)
                    {
                        buySignal = false;
                    }
                    if (sellSignal && up)
                    {
                        sellSignal = false;
                    }
                }

                decimal volR = volumes[i] / volumes.Skip (Math.Max (0, i - 20)).Take (20).Average ();
                decimal atrP = closes[i] > 0 && atrList[i] > 0 ? atrList[i] / closes[i] : 0;
                bool risk = volR > volThresh || atrP > atrThresh || rsi[i] > rsiHigh || rsi[i] < rsiLow;
                if (risk)
                {
                    buySignal = false;
                    sellSignal = false;
                }

                if (position == 0 && buySignal)
                {
                    decimal cost = capital * commission;
                    capital -= cost;
                    position = capital / price;
                    capital = 0;
                    entryPrice = price;
                    tradeBars = 0;
                }
                else if (position > 0)
                {
                    tradeBars++;
                    decimal sl = entryPrice * (1 - adaptiveSl);
                    decimal tp = entryPrice * (1 + adaptiveTp);
                    if (sellSignal || price <= sl || price >= tp)
                    {
                        capital = position * price;
                        decimal cost = capital * commission;
                        capital -= cost;
                        position = 0;

                        totalTradeBars += tradeBars;

                        decimal pnl = (price - entryPrice) / entryPrice;
                        if (pnl > 0)
                        {
                            winning++;
                            currentWinStreak++;
                            currentLoseStreak = 0;
                            if (currentWinStreak > maxWinStreak)
                            {
                                maxWinStreak = currentWinStreak;
                            }
                        }
                        else
                        {
                            losing++;
                            currentLoseStreak++;
                            currentWinStreak = 0;
                            if (currentLoseStreak > maxLoseStreak)
                            {
                                maxLoseStreak = currentLoseStreak;
                            }
                        }
                        if (capital > peakCapital)
                        {
                            peakCapital = capital;
                        }
                        decimal dd = (peakCapital - capital) / peakCapital * 100;
                        if (dd > maxDrawdown)
                        {
                            maxDrawdown = dd;
                        }
                    }
                }

                equityCurve.Add (position > 0 ? position * price : capital);
            }

            if (position > 0)
            {
                capital = position * closes.Last ();
            }
            decimal totalReturn = (capital - capitalStart) / capitalStart * 100;
            int totalTrades = winning + losing;
            decimal winRate = totalTrades > 0 ? (decimal)winning / totalTrades * 100 : 0;
            decimal pf = losing > 0 && winning > 0 ? (decimal)winning / losing : 0;

            decimal sharpe = 0;
            if (equityCurve.Count > 1)
            {
                List<decimal> rets = new List<decimal> ();
                for (int j = 1; j < equityCurve.Count; j++)
                {
                    if (equityCurve[j - 1] > 0)
                    {
                        rets.Add ((equityCurve[j] - equityCurve[j - 1]) / equityCurve[j - 1]);
                    }
                }
                if (rets.Count > 0)
                {
                    decimal avg = rets.Average ();
                    decimal std = StdDev (rets);
                    sharpe = std > 0 ? avg / std * (decimal)Math.Sqrt (252) : 0;
                }
            }

            decimal avgTradeBars = totalTrades > 0 ? (decimal)totalTradeBars / totalTrades : 0;

            return new BacktestResult
            {
                TotalReturn = totalReturn, WinRate = winRate, TotalTrades = totalTrades,
                WinningTrades = winning, LosingTrades = losing, MaxDrawdown = maxDrawdown,
                SharpeRatio = sharpe, ProfitFactor = pf,
                EquityCurve = equityCurve,
                MaxWinStreak = maxWinStreak, MaxLoseStreak = maxLoseStreak,
                AvgTradeBars = avgTradeBars
            };
        }

        // ═══════════════════════════════════════════════════
        //  ЗАГРУЗКА ДАННЫХ С BINANCE (публичное API)
        // ═══════════════════════════════════════════════════

        static async Task<List<Kline>> DownloadKlinesAsync (string symbol, string interval, int limit)
        {
            List<Kline> klines = new List<Kline> ();
            long endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

            while (klines.Count < limit)
            {
                int batchSize = Math.Min (1000, limit - klines.Count);
                string url = $"https://api.binance.com/api/v3/klines?symbol={symbol}&interval={interval}&limit={batchSize}";
                if (klines.Count > 0)
                {
                    url += $"&endTime={endTime}";
                }

                try
                {
                    string json = await Http.GetStringAsync (url);
                    List<List<JsonElement>> arr = JsonSerializer.Deserialize<List<List<JsonElement>>> (json);
                    if (arr == null || arr.Count == 0)
                    {
                        break;
                    }

                    foreach (List<JsonElement> candle in arr)
                    {
                        klines.Add (new Kline
                        {
                            OpenTime = DateTimeOffset.FromUnixTimeMilliseconds (candle[0].GetInt64 ()).DateTime,
                            Open = decimal.Parse (candle[1].GetString (), CultureInfo.InvariantCulture),
                            High = decimal.Parse (candle[2].GetString (), CultureInfo.InvariantCulture),
                            Low = decimal.Parse (candle[3].GetString (), CultureInfo.InvariantCulture),
                            Close = decimal.Parse (candle[4].GetString (), CultureInfo.InvariantCulture),
                            Volume = decimal.Parse (candle[5].GetString (), CultureInfo.InvariantCulture)
                        });
                    }

                    endTime = new DateTimeOffset (klines.Last ().OpenTime.AddHours (-1)).ToUnixTimeMilliseconds ();
                    await Task.Delay (200);
                }
                catch (Exception ex)
                {
                    Console.WriteLine ($"\n  ⚠️ Ошибка загрузки: {ex.Message}");
                    break;
                }
            }

            klines.Reverse ();
            return klines;
        }

        // ═══════════════════════════════════════════════════
        //  БАЗОВАЯ СТРАТЕГИЯ (SMA crossover + RSI)
        // ═══════════════════════════════════════════════════

        static BacktestResult RunBasicSmaStrategy (List<Kline> klines, decimal capital)
        {
            return RunBacktest (klines, fastPeriod: 9, slowPeriod: 21, rsiPeriod: 14,
                stopLoss: 0.02m, takeProfit: 0.04m, useAdaptive: false, useLsma: false, useValidator: false, capital: capital);
        }

        // ═══════════════════════════════════════════════════
        //  УЛУЧШЕННАЯ СТРАТЕГИЯ (LSMA + Adaptive + Validator)
        // ═══════════════════════════════════════════════════

        static BacktestResult RunEnhancedStrategy (List<Kline> klines, decimal capital)
        {
            return RunBacktest (klines, fastPeriod: 9, slowPeriod: 21, rsiPeriod: 14,
                stopLoss: 0.02m, takeProfit: 0.04m, useAdaptive: true, useLsma: true, useValidator: true, capital: capital);
        }

        // ═══════════════════════════════════════════════════
        //  УНИВЕРСАЛЬНЫЙ ДВИЖОК БЭКТЕСТА
        // ═══════════════════════════════════════════════════

        static BacktestResult RunBacktest (
            List<Kline> klines, int fastPeriod, int slowPeriod, int rsiPeriod,
            decimal stopLoss, decimal takeProfit,
            bool useAdaptive, bool useLsma, bool useValidator,
            decimal capital, decimal commission = 0.0004m)
        {
            List<decimal> closes = klines.Select (k => k.Close).ToList ();
            List<decimal> highs = klines.Select (k => k.High).ToList ();
            List<decimal> lows = klines.Select (k => k.Low).ToList ();
            List<decimal> volumes = klines.Select (k => k.Volume).ToList ();

            List<decimal> fastSma = CalcSMA (closes, fastPeriod);
            List<decimal> slowSma = CalcSMA (closes, slowPeriod);
            List<decimal> rsi = CalcRSI (closes, rsiPeriod);
            List<decimal> atrList = CalcATR (highs, lows, closes, 14);
            (List<decimal> upper, List<decimal> lower) bb = CalcBollingerBands (closes, 20);
            List<decimal> macd = CalcMACD (closes);
            List<decimal> lsma = useLsma ? CalcLSMA (closes, 20) : null;

            decimal capitalStart = capital;
            decimal position = 0;
            decimal entryPrice = 0;
            decimal peakCapital = capital;
            decimal maxDrawdown = 0;
            int winning = 0, losing = 0;
            int tradeBars = 0;
            int totalTradeBars = 0;
            int currentWinStreak = 0, currentLoseStreak = 0;
            int maxWinStreak = 0, maxLoseStreak = 0;
            List<decimal> equityCurve = new List<decimal> { capital };

            int startIdx = slowPeriod + 10;

            for (int i = startIdx; i < closes.Count; i++)
            {
                decimal price = closes[i];
                bool buySignal = false, sellSignal = false;

                int effectiveFast = fastPeriod;
                int effectiveSlow = slowPeriod;
                decimal adaptiveSl = stopLoss;
                decimal adaptiveTp = takeProfit;

                if (useAdaptive && i >= 100)
                {
                    List<decimal> recentAtr = atrList.Skip (Math.Max (0, i - 100)).Take (100).Where (v => v > 0).ToList ();
                    List<decimal> olderAtr = atrList.Skip (Math.Max (0, i - 200)).Take (Math.Max (0, Math.Min (100, i - 100))).Where (v => v > 0).ToList ();

                    if (recentAtr.Count > 10 && olderAtr.Count > 10)
                    {
                        decimal currentAtr = recentAtr.Last ();
                        decimal avgAtr = olderAtr.Average ();
                        decimal atrRatio = avgAtr > 0 ? currentAtr / avgAtr : 1m;

                        decimal adaptiveFactor = MapRange (atrRatio, 0.5m, 2.0m, 0.7m, 1.5m);
                        adaptiveFactor = Math.Clamp (adaptiveFactor, 0.5m, 1.5m);

                        effectiveFast = Math.Max (3, (int)Math.Round (fastPeriod * (1m + (adaptiveFactor - 1m) * 0.3m)));
                        effectiveSlow = Math.Max (effectiveFast + 3, (int)Math.Round (slowPeriod * (1m + (adaptiveFactor - 1m) * 0.3m)));
                        adaptiveSl = stopLoss * (1m + (adaptiveFactor - 1m) * 0.4m);
                        adaptiveTp = takeProfit * (1m + (adaptiveFactor - 1m) * 0.4m);

                        if (effectiveFast != fastPeriod || effectiveSlow != slowPeriod)
                        {
                            List<decimal> adaptedFast = CalcSMA (closes, effectiveFast);
                            List<decimal> adaptedSlow = CalcSMA (closes, effectiveSlow);

                            if (i > 0 && adaptedFast[i] > 0 && adaptedSlow[i] > 0)
                            {
                                fastSma[i] = adaptedFast[i];
                                slowSma[i] = adaptedSlow[i];
                            }
                        }
                    }
                }

                if (i > 0 && fastSma[i] > 0 && slowSma[i] > 0)
                {
                    bool crossUp = fastSma[i - 1] <= slowSma[i - 1] && fastSma[i] > slowSma[i];
                    bool crossDown = fastSma[i - 1] >= slowSma[i - 1] && fastSma[i] < slowSma[i];

                    if (crossUp)
                    {
                        bool confirmation = rsi[i] < 40 || price <= bb.lower[i] || (macd[i] > 0 && macd[i] > macd[Math.Max (0, i - 1)]);
                        if (confirmation)
                        {
                            buySignal = true;
                        }
                    }

                    if (crossDown)
                    {
                        bool confirmation = rsi[i] > 60 || price >= bb.upper[i] || (macd[i] < 0 && macd[i] < macd[Math.Max (0, i - 1)]);
                        if (confirmation)
                        {
                            sellSignal = true;
                        }
                    }
                }

                if (useLsma && lsma != null && lsma[i] > 0 && i > 1)
                {
                    bool lsmaUptrend = closes[i] > lsma[i] && lsma[i] > lsma[i - 1];
                    bool lsmaDowntrend = closes[i] < lsma[i] && lsma[i] < lsma[i - 1];

                    if (buySignal && lsmaDowntrend)
                    {
                        buySignal = false;
                    }
                    if (sellSignal && lsmaUptrend)
                    {
                        sellSignal = false;
                    }
                }

                if (useValidator)
                {
                    decimal volRatio = volumes[i] / volumes.Skip (Math.Max (0, i - 20)).Take (20).Average ();
                    decimal atrPercent = closes[i] > 0 && atrList[i] > 0 ? atrList[i] / closes[i] : 0;

                    bool riskFlag = false;
                    if (volRatio > 8m)
                    {
                        riskFlag = true;
                    }
                    if (atrPercent > 0.15m)
                    {
                        riskFlag = true;
                    }

                    if (rsi[i] > 80 || rsi[i] < 20)
                    {
                        riskFlag = true;
                    }

                    if (riskFlag)
                    {
                        buySignal = false;
                        sellSignal = false;
                    }
                }

                if (position == 0 && buySignal)
                {
                    decimal cost = capital * commission;
                    capital -= cost;
                    position = capital / price;
                    capital = 0;
                    entryPrice = price;
                    tradeBars = 0;
                }
                else if (position > 0)
                {
                    tradeBars++;
                    decimal sl = entryPrice * (1 - adaptiveSl);
                    decimal tp = entryPrice * (1 + adaptiveTp);
                    bool stopHit = price <= sl;
                    bool takeHit = price >= tp;

                    if (sellSignal || stopHit || takeHit)
                    {
                        capital = position * price;
                        decimal cost = capital * commission;
                        capital -= cost;
                        position = 0;

                        totalTradeBars += tradeBars;

                        decimal pnl = (price - entryPrice) / entryPrice;
                        if (pnl > 0)
                        {
                            winning++;
                            currentWinStreak++;
                            currentLoseStreak = 0;
                            if (currentWinStreak > maxWinStreak)
                            {
                                maxWinStreak = currentWinStreak;
                            }
                        }
                        else
                        {
                            losing++;
                            currentLoseStreak++;
                            currentWinStreak = 0;
                            if (currentLoseStreak > maxLoseStreak)
                            {
                                maxLoseStreak = currentLoseStreak;
                            }
                        }

                        if (capital > peakCapital)
                        {
                            peakCapital = capital;
                        }
                        decimal dd = (peakCapital - capital) / peakCapital * 100;
                        if (dd > maxDrawdown)
                        {
                            maxDrawdown = dd;
                        }
                    }
                }

                equityCurve.Add (position > 0 ? position * price : capital);
            }

            if (position > 0)
            {
                capital = position * closes.Last ();
            }

            decimal totalReturn = (capital - capitalStart) / capitalStart * 100;
            int totalTrades = winning + losing;
            decimal winRate = totalTrades > 0 ? (decimal)winning / totalTrades * 100 : 0;

            decimal sharpe = 0;
            if (equityCurve.Count > 1)
            {
                List<decimal> rets = new List<decimal> ();
                for (int j = 1; j < equityCurve.Count; j++)
                {
                    if (equityCurve[j - 1] > 0)
                    {
                        rets.Add ((equityCurve[j] - equityCurve[j - 1]) / equityCurve[j - 1]);
                    }
                }
                if (rets.Count > 0)
                {
                    decimal avg = rets.Average ();
                    decimal std = StdDev (rets);
                    sharpe = std > 0 ? avg / std * (decimal)Math.Sqrt (252) : 0;
                }
            }

            decimal profitFactor = losing > 0 && winning > 0 ? (decimal)winning / losing : 0;
            decimal avgTradeBars = totalTrades > 0 ? (decimal)totalTradeBars / totalTrades : 0;

            return new BacktestResult
            {
                TotalReturn = totalReturn,
                WinRate = winRate,
                TotalTrades = totalTrades,
                WinningTrades = winning,
                LosingTrades = losing,
                MaxDrawdown = maxDrawdown,
                SharpeRatio = sharpe,
                ProfitFactor = profitFactor,
                EquityCurve = equityCurve,
                MaxWinStreak = maxWinStreak,
                MaxLoseStreak = maxLoseStreak,
                AvgTradeBars = avgTradeBars
            };
        }

        // ═══════════════════════════════════════════════════
        //  МУЛЬТИ-ПОЗИЦИОННЫЙ БЭКТЕСТ
        // ═══════════════════════════════════════════════════

        static BacktestResult RunMultiPositionBacktest (
            List<Kline> klines, int fastPeriod, int slowPeriod, int rsiPeriod,
            decimal stopLoss, decimal takeProfit, int maxPositions,
            bool useAdaptive, bool useLsma, bool useValidator,
            decimal capital, decimal commission = 0.0004m)
        {
            List<decimal> closes = klines.Select (k => k.Close).ToList ();
            List<decimal> highs = klines.Select (k => k.High).ToList ();
            List<decimal> lows = klines.Select (k => k.Low).ToList ();
            List<decimal> volumes = klines.Select (k => k.Volume).ToList ();

            List<decimal> fastSma = CalcSMA (closes, fastPeriod);
            List<decimal> slowSma = CalcSMA (closes, slowPeriod);
            List<decimal> rsi = CalcRSI (closes, rsiPeriod);
            List<decimal> atrList = CalcATR (highs, lows, closes, 14);
            (List<decimal> upper, List<decimal> lower) bb = CalcBollingerBands (closes, 20);
            List<decimal> macd = CalcMACD (closes);
            List<decimal> lsma = useLsma ? CalcLSMA (closes, 20) : null;

            decimal capitalStart = capital;
            decimal freeCash = capital;
            decimal peakCapital = capital;
            decimal maxDrawdown = 0;
            int winning = 0, losing = 0;
            int totalTradeBars = 0;
            int currentWinStreak = 0, currentLoseStreak = 0;
            int maxWinStreak = 0, maxLoseStreak = 0;
            List<decimal> equityCurve = new List<decimal> { capital };

            var openPositions = new List<MultiPosition> ();
            int startIdx = slowPeriod + 10;

            for (int i = startIdx; i < closes.Count; i++)
            {
                decimal price = closes[i];

                for (int p = openPositions.Count - 1; p >= 0; p--)
                {
                    var pos = openPositions[p];
                    pos.BarsHeld++;
                    decimal sl = pos.EntryPrice * (1 - stopLoss);
                    decimal tp = pos.EntryPrice * (1 + takeProfit);

                    if (price <= sl || price >= tp)
                    {
                        decimal proceeds = pos.Quantity * price;
                        decimal cost = proceeds * commission;
                        freeCash += proceeds - cost;
                        totalTradeBars += pos.BarsHeld;

                        decimal pnl = (price - pos.EntryPrice) / pos.EntryPrice;
                        if (pnl > 0) { winning++; currentWinStreak++; currentLoseStreak = 0; if (currentWinStreak > maxWinStreak) maxWinStreak = currentWinStreak; }
                        else { losing++; currentLoseStreak++; currentWinStreak = 0; if (currentLoseStreak > maxLoseStreak) maxLoseStreak = currentLoseStreak; }

                        openPositions.RemoveAt (p);
                    }
                }

                decimal totalEquity = freeCash + openPositions.Sum (op => op.Quantity * price);
                if (totalEquity > peakCapital) peakCapital = totalEquity;
                decimal dd = peakCapital > 0 ? (peakCapital - totalEquity) / peakCapital * 100 : 0;
                if (dd > maxDrawdown) maxDrawdown = dd;

                if (openPositions.Count < maxPositions && freeCash > capital * 0.05m)
                {
                    bool buySignal = false;
                    if (i > 0 && fastSma[i] > 0 && slowSma[i] > 0)
                    {
                        bool crossUp = fastSma[i - 1] <= slowSma[i - 1] && fastSma[i] > slowSma[i];
                        if (crossUp)
                        {
                            bool c = rsi[i] < 40 || price <= bb.lower[i] || (macd[i] > 0 && macd[i] > macd[Math.Max (0, i - 1)]);
                            if (c) buySignal = true;
                        }
                    }

                    if (useLsma && lsma != null && lsma[i] > 0 && i > 1)
                    {
                        if (buySignal && closes[i] < lsma[i] && lsma[i] < lsma[i - 1]) buySignal = false;
                    }

                    if (useValidator)
                    {
                        decimal volRatio = volumes[i] / volumes.Skip (Math.Max (0, i - 20)).Take (20).Average ();
                        decimal atrPercent = closes[i] > 0 && atrList[i] > 0 ? atrList[i] / closes[i] : 0;
                        if (volRatio > 8m || atrPercent > 0.15m || rsi[i] > 80 || rsi[i] < 20) buySignal = false;
                    }

                    if (buySignal)
                    {
                        decimal investPerPos = freeCash * 0.3m;
                        if (investPerPos > capital * 0.05m)
                        {
                            decimal cost = investPerPos * commission;
                            freeCash -= (investPerPos + cost);
                            openPositions.Add (new MultiPosition
                            {
                                EntryPrice = price,
                                Quantity = investPerPos / price,
                                BarsHeld = 0
                            });
                        }
                    }
                }

                equityCurve.Add (freeCash + openPositions.Sum (op => op.Quantity * price));
            }

            decimal finalPrice = closes.Last ();
            foreach (var pos in openPositions)
            {
                freeCash += pos.Quantity * finalPrice * (1 - commission);
                totalTradeBars += pos.BarsHeld;
                decimal pnl = (finalPrice - pos.EntryPrice) / pos.EntryPrice;
                if (pnl > 0) winning++; else losing++;
            }

            decimal totalReturn = (freeCash - capitalStart) / capitalStart * 100;
            int totalTrades = winning + losing;
            decimal winRate = totalTrades > 0 ? (decimal)winning / totalTrades * 100 : 0;
            decimal profitFactor = losing > 0 && winning > 0 ? (decimal)winning / losing : 0;

            decimal sharpe = 0;
            if (equityCurve.Count > 1)
            {
                List<decimal> rets = new List<decimal> ();
                for (int j = 1; j < equityCurve.Count; j++)
                {
                    if (equityCurve[j - 1] > 0)
                        rets.Add ((equityCurve[j] - equityCurve[j - 1]) / equityCurve[j - 1]);
                }
                if (rets.Count > 0)
                {
                    decimal avg = rets.Average ();
                    decimal std = StdDev (rets);
                    sharpe = std > 0 ? avg / std * (decimal)Math.Sqrt (252) : 0;
                }
            }

            decimal avgTradeBarsResult = totalTrades > 0 ? (decimal)totalTradeBars / totalTrades : 0;

            return new BacktestResult
            {
                TotalReturn = totalReturn, WinRate = winRate, TotalTrades = totalTrades,
                WinningTrades = winning, LosingTrades = losing, MaxDrawdown = maxDrawdown,
                SharpeRatio = sharpe, ProfitFactor = profitFactor,
                EquityCurve = equityCurve,
                MaxWinStreak = maxWinStreak, MaxLoseStreak = maxLoseStreak,
                AvgTradeBars = avgTradeBarsResult
            };
        }

        class MultiPosition
        {
            public decimal EntryPrice { get; set; }
            public decimal Quantity { get; set; }
            public int BarsHeld { get; set; }
        }

        // ═══════════════════════════════════════════════════
        //  ИНДИКАТОРЫ
        // ═══════════════════════════════════════════════════

        static List<decimal> CalcSMA (List<decimal> data, int period)
        {
            List<decimal> result = new List<decimal> (data.Count);
            decimal sum = 0;
            for (int i = 0; i < data.Count; i++)
            {
                sum += data[i];
                if (i >= period)
                {
                    sum -= data[i - period];
                }
                result.Add (i >= period - 1 ? sum / period : 0);
            }
            return result;
        }

        static List<decimal> CalcRSI (List<decimal> data, int period)
        {
            List<decimal> result = Enumerable.Repeat (50m, data.Count).ToList ();
            if (data.Count <= period)
            {
                return result;
            }

            decimal avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                decimal diff = data[i] - data[i - 1];
                if (diff > 0)
                {
                    avgGain += diff;
                }
                else
                {
                    avgLoss += Math.Abs (diff);
                }
            }
            avgGain /= period;
            avgLoss /= period;
            result[period] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);

            for (int i = period + 1; i < data.Count; i++)
            {
                decimal diff = data[i] - data[i - 1];
                decimal gain = diff > 0 ? diff : 0;
                decimal loss = diff < 0 ? Math.Abs (diff) : 0;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
                result[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            }
            return result;
        }

        static List<decimal> CalcATR (List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            List<decimal> result = Enumerable.Repeat (0m, closes.Count).ToList ();
            if (closes.Count <= 1)
            {
                return result;
            }

            List<decimal> tr = new List<decimal> { highs[0] - lows[0] };
            for (int i = 1; i < closes.Count; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs (highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs (lows[i] - closes[i - 1]);
                tr.Add (Math.Max (tr1, Math.Max (tr2, tr3)));
            }

            decimal currentAtr = tr.Take (period).Average ();
            if (period - 1 < result.Count)
            {
                result[period - 1] = currentAtr;
            }
            for (int i = period; i < tr.Count; i++)
            {
                currentAtr = (currentAtr * (period - 1) + tr[i]) / period;
                result[i] = currentAtr;
            }
            return result;
        }

        static (List<decimal> upper, List<decimal> lower) CalcBollingerBands (List<decimal> data, int period)
        {
            List<decimal> upper = Enumerable.Repeat (0m, data.Count).ToList ();
            List<decimal> lower = Enumerable.Repeat (0m, data.Count).ToList ();
            List<decimal> sma = CalcSMA (data, period);

            for (int i = period - 1; i < data.Count; i++)
            {
                decimal sumSq = 0;
                for (int j = 0; j < period; j++)
                {
                    sumSq += (data[i - j] - sma[i]) * (data[i - j] - sma[i]);
                }
                decimal stdDev = (decimal)Math.Sqrt ((double)(sumSq / period));
                upper[i] = sma[i] + 2 * stdDev;
                lower[i] = sma[i] - 2 * stdDev;
            }
            return (upper, lower);
        }

        static List<decimal> CalcMACD (List<decimal> data)
        {
            List<decimal> fast = CalcEMA (data, 12);
            List<decimal> slow = CalcEMA (data, 26);
            List<decimal> macdLine = new List<decimal> ();
            for (int i = 0; i < data.Count; i++)
            {
                macdLine.Add (fast[i] > 0 && slow[i] > 0 ? fast[i] - slow[i] : 0);
            }
            return macdLine;
        }

        static List<decimal> CalcEMA (List<decimal> data, int period)
        {
            List<decimal> result = Enumerable.Repeat (0m, data.Count).ToList ();
            if (data.Count == 0)
            {
                return result;
            }
            decimal mult = 2.0m / (period + 1);
            result[0] = data[0];
            for (int i = 1; i < data.Count; i++)
            {
                result[i] = (data[i] - result[i - 1]) * mult + result[i - 1];
            }
            return result;
        }

        static List<decimal> CalcLSMA (List<decimal> data, int period)
        {
            List<decimal> result = Enumerable.Repeat (0m, data.Count).ToList ();
            if (data.Count < period)
            {
                return result;
            }

            for (int i = period - 1; i < data.Count; i++)
            {
                decimal sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                for (int j = 0; j < period; j++)
                {
                    decimal x = j;
                    decimal y = data[i - period + 1 + j];
                    sumX += x;
                    sumY += y;
                    sumXY += x * y;
                    sumX2 += x * x;
                }
                decimal denom = period * sumX2 - sumX * sumX;
                if (denom == 0)
                {
                    result[i] = sumY / period;
                    continue;
                }
                decimal slope = (period * sumXY - sumX * sumY) / denom;
                decimal intercept = (sumY - slope * sumX) / period;
                result[i] = intercept + slope * (period - 1);
            }
            return result;
        }

        static decimal MapRange (decimal value, decimal fromLow, decimal fromHigh, decimal toLow, decimal toHigh)
        {
            value = Math.Clamp (value, fromLow, fromHigh);
            decimal t = (value - fromLow) / (fromHigh - fromLow);
            return toLow + t * (toHigh - toLow);
        }

        static decimal StdDev (List<decimal> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            decimal avg = values.Average ();
            decimal sumSq = values.Select (v => (v - avg) * (v - avg)).Sum ();
            return (decimal)Math.Sqrt ((double)(sumSq / values.Count));
        }

        // ═══════════════════════════════════════════════════
        //  МОДЕЛИ ДАННЫХ
        // ═══════════════════════════════════════════════════

        class Kline
        {
            public DateTime OpenTime { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
        }

        class BacktestResult
        {
            public decimal TotalReturn { get; set; }
            public decimal WinRate { get; set; }
            public int TotalTrades { get; set; }
            public int WinningTrades { get; set; }
            public int LosingTrades { get; set; }
            public decimal MaxDrawdown { get; set; }
            public decimal SharpeRatio { get; set; }
            public decimal ProfitFactor { get; set; }
            public List<decimal> EquityCurve { get; set; } = new List<decimal> ();
            public int MaxWinStreak { get; set; }
            public int MaxLoseStreak { get; set; }
            public decimal AvgTradeBars { get; set; }
        }
    }
}
