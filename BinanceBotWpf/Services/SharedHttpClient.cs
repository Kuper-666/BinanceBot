using System;
using System.Net.Http;

namespace BinanceBotWpf.Services
{
    public static class SharedHttpClient
    {
        private static readonly Lazy<HttpClient> _instance = new (() =>
            new HttpClient { Timeout = TimeSpan.FromSeconds (30) });

        public static HttpClient Instance => _instance.Value;
    }
}
