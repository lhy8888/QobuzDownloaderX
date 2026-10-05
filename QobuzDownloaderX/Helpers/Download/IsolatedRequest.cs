using System;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    internal static class IsolatedRequest
    {
        internal static async Task<T> RunAsync<T>(Func<CancellationToken, T> request, TimeSpan timeout, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(timeout);
                var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (deadline.Token.Register(() => interrupted.TrySetResult(true)))
                {
                    var work = Task.Run(() => request(deadline.Token), deadline.Token);
                    if (await Task.WhenAny(work, interrupted.Task).ConfigureAwait(false) != work)
                    {
                        // A dependency may ignore cancellation. Its result remains private and is never published.
                        var observed = work.ContinueWith(t => { var ignored = t.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("The service request timed out.");
                    }
                    token.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new TimeoutException("The service request timed out.");
                    return await work.ConfigureAwait(false);
                }
            }
        }
    }
}
