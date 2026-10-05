using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class HttpStatusException : IOException
    {
        internal int StatusCode { get; }
        internal HttpStatusException(int code) : base("HTTP " + code) { StatusCode = code; }
    }

    internal static class ReliableHttp
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        internal const int Attempts = 3;

        internal static bool IsTransient(int status) => status == 408 || status == 429 || status >= 500;

        internal static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, HttpClient client = null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("An HTTPS download address is required.");
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                HttpResponseMessage response;
                try { response = await (client ?? Client).GetAsync(uri, completion, token).ConfigureAwait(false); }
                catch (HttpRequestException) when (attempt + 1 < Attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false);
                    continue;
                }
                if (response.IsSuccessStatusCode) return response;
                int status = (int)response.StatusCode;
                TimeSpan delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromSeconds(attempt + 1);
                response.Dispose();
                // Do not retry a permanent rejection, or ignore a long server-requested backoff.
                if (!IsTransient(status) || attempt + 1 >= Attempts || delay > TimeSpan.FromSeconds(30))
                    throw new HttpStatusException(status);
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, token).ConfigureAwait(false);
            }
        }

        internal static async Task DownloadAsync(string url, string path, TimeSpan timeout, CancellationToken token,
            Action<long, long> progress = null, HttpClient client = null)
        {
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(timeout);
                        using (var response = await GetAsync(url, deadline.Token, HttpCompletionOption.ResponseHeadersRead, client).ConfigureAwait(false))
                        using (deadline.Token.Register(response.Dispose))
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            long expected = response.Content.Headers.ContentLength ?? -1;
                            long received = 0;
                            progress?.Invoke(0, expected);
                            var buffer = new byte[81920];
                            while (true)
                            {
                                using (var inactivity = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                                {
                                    inactivity.CancelAfter(TimeSpan.FromMinutes(1));
                                    using (inactivity.Token.Register(response.Dispose))
                                    {
                                        int count = await input.ReadAsync(buffer, 0, buffer.Length, inactivity.Token).ConfigureAwait(false);
                                        if (count == 0) break;
                                        await output.WriteAsync(buffer, 0, count, inactivity.Token).ConfigureAwait(false);
                                        received += count;
                                        progress?.Invoke(received, expected);
                                    }
                                }
                            }
                            deadline.Token.ThrowIfCancellationRequested();
                            if (received == 0 || (expected >= 0 && received != expected))
                                throw new InvalidDataException("The downloaded file is incomplete.");
                            return;
                        }
                    }
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (Exception ex) when (attempt + 1 < Attempts &&
                    !(ex is HttpStatusException) && (ex is HttpRequestException || ex is IOException || ex is InvalidDataException || ex is OperationCanceledException || ex is ObjectDisposedException))
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token).ConfigureAwait(false);
                }
            }
        }
    }
}
