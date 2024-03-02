using AAES.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AAES
{
    internal sealed class WaitingCancellationOptions
    {
        private readonly TaskCompletionSource<object?> tcs = new();
        private CancellationTokenRegistration tokenRegistration;
        private int disposed = 0;

        private WaitingCancellationOptions()
        {
        }

        internal static WaitingCancellationOptions? CreateIfNeed(
            TimeSpan? waitingTimeout,
            CancellationToken token)
        {
            WaitingCancellationOptions? options = null;

            if (token != default)
            {
                options ??= new();
                options.tokenRegistration = token.Register(() =>
                {
                    options.tcs.TrySetException(new WaitingCanceledException(token));
                    options.Dispose();
                });
            }

            if (waitingTimeout.HasValue && waitingTimeout.Value >= TimeSpan.Zero)
            {
                options ??= new();
                var cts = waitingTimeout.Value >= TimeSpan.FromMinutes(1) ? new CancellationTokenSource() : null;
                var delayTask = Task.Delay(waitingTimeout.Value, cts?.Token ?? default);
                Task.WhenAny(options.tcs.Task, delayTask)
                    .ContinueWith(task =>
                    {
                        if (task.Result == delayTask)
                            options.tcs.TrySetException(new WaitingTimeoutException(waitingTimeout.Value));
                        else
                            cts?.Cancel();

                        cts?.Dispose();
                        options.Dispose();
                    });
            }

            return options;
        }

        public void ThrowIfCanceled()
        {
            this.tcs.TrySetResult(null);
            this.Dispose();
            this.tcs.Task.GetAwaiter().GetResult();
        }

        public async Task<TResult?> With<TResult>(Task<TResult?> task)
        {
            if (this.tcs.Task == await Task.WhenAny(task, this.tcs.Task))
                this.tcs.Task.GetAwaiter().GetResult(); // throw if canceled
            return await task;
        }

        private void Dispose()
        {
            if (0 != Interlocked.CompareExchange(ref this.disposed, 1, 0))
                return;

            this.tokenRegistration.Dispose();
        }
    }
}
