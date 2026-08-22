#nullable enable
// HttpRetry.csx
// Generic retry/backoff helper for transient HTTP failures (5xx, 408, 429, timeouts / network errors).
// Non-transient errors (4xx other than 408/429) fail fast without retrying.

#load "Logging.csx"

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public static class HttpRetry
{
    private static readonly int[] BackoffSecondsSchedule = { 1, 3, 9 };

    /// <summary>
    /// Executes the given HTTP request factory with retries on transient failures.
    /// </summary>
    /// <param name="requestFactory">Creates a fresh HttpRequestMessage for each attempt (requests can't be reused).</param>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        Logger logger,
        string operationName)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= BackoffSecondsSchedule.Length; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(request).ConfigureAwait(false);

                if (IsTransientStatus(response.StatusCode))
                {
                    if (attempt < BackoffSecondsSchedule.Length)
                    {
                        logger.Warn($"{operationName}: transient HTTP status {(int)response.StatusCode} on attempt {attempt + 1}, retrying in {BackoffSecondsSchedule[attempt]}s.");
                        response.Dispose();
                        await Task.Delay(TimeSpan.FromSeconds(BackoffSecondsSchedule[attempt])).ConfigureAwait(false);
                        continue;
                    }

                    logger.Error($"{operationName}: exhausted retries, last status {(int)response.StatusCode}.");
                    return response;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Non-transient client error (e.g. 401, 400, 404): fail fast, no retry.
                    logger.Error($"{operationName}: non-retryable HTTP status {(int)response.StatusCode}.");
                    return response;
                }

                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is TimeoutException)
            {
                lastException = ex;
                if (attempt < BackoffSecondsSchedule.Length)
                {
                    logger.Warn($"{operationName}: network error on attempt {attempt + 1} ({ex.Message}), retrying in {BackoffSecondsSchedule[attempt]}s.");
                    await Task.Delay(TimeSpan.FromSeconds(BackoffSecondsSchedule[attempt])).ConfigureAwait(false);
                    continue;
                }
            }
        }

        logger.Error($"{operationName}: failed after retries.", lastException ?? new Exception("Unknown error"));
        throw new HttpRequestException($"{operationName} failed after {BackoffSecondsSchedule.Length + 1} attempts.", lastException);
    }

    private static bool IsTransientStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code == 408 || code == 429 || code >= 500;
    }
}
