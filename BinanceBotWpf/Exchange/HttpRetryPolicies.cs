using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;

namespace BinanceBotWpf.Exchange
{
    internal static class HttpRetryPolicies
    {
        public static AsyncRetryPolicy<HttpResponseMessage> CreateRetryPolicy(
            Action<string> log,
            int maxRetries = 3,
            int baseDelayMs = 1000)
        {
            return Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>()
                .OrResult(r => (int)r.StatusCode == 429 || (int)r.StatusCode == 418)
                .WaitAndRetryAsync(
                    maxRetries,
                    retryAttempt => TimeSpan.FromMilliseconds(Math.Min(baseDelayMs * Math.Pow(2, retryAttempt - 1), 32000)),
                    onRetry: (outcome, delay, retryAttempt, context) =>
                    {
                        string msg = outcome.Exception != null
                            ? $"HTTP error: {outcome.Exception.Message}"
                            : $"Status code: {(int)outcome.Result.StatusCode}";
                        log($"Retry {retryAttempt}/{maxRetries} after {delay.TotalMilliseconds}ms ({msg})");
                    });
        }

        public static AsyncRetryPolicy CreateTimeoutRetryPolicy(
            Action<string> log,
            int maxRetries = 3,
            int baseDelayMs = 1000)
        {
            return Policy
                .Handle<TaskCanceledException>()
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(
                    maxRetries,
                    retryAttempt => TimeSpan.FromMilliseconds(Math.Min(baseDelayMs * Math.Pow(2, retryAttempt - 1), 32000)),
                    onRetry: (exception, delay, retryAttempt, context) =>
                    {
                        log($"Timeout/network retry {retryAttempt}/{maxRetries} after {delay.TotalMilliseconds}ms");
                    });
        }
    }
}
