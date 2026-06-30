using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Exchange
{
    public class CompositeKlineProvider : IKlineProvider
    {
        private readonly IBinanceRestClient _restClient;
        private readonly KlineCache _cache;
        private readonly Action<string> _logger;

        public CompositeKlineProvider (IBinanceRestClient restClient, KlineCache cache, Action<string> logger)
        {
            _restClient = restClient;
            _cache = cache;
            _logger = logger;
        }

        public async Task<List<BinanceKline>> GetKlinesAsync (string symbol, string interval, int limit)
        {
            if (_cache.HasRealTimeData (symbol, interval))
            {
                return await _cache.GetKlinesAsync (symbol, interval, limit);
            }

            _logger?.Invoke ($"CompositeKlineProvider: загрузка {symbol} {interval} из REST API");
            List<BinanceKline> klines = await _restClient.GetKlinesAsync (symbol, interval, limit);
            _cache.SeedFromRest (symbol, interval, klines);
            return klines;
        }

        public bool HasRealTimeData (string symbol, string interval)
        {
            return _cache.HasRealTimeData (symbol, interval);
        }
    }
}
