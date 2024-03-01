using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Lib
{
    public abstract class ExclusiveResourceTask
    {
        internal ExclusiveResourceTask(
            Task internalTask,
            IReadOnlyList<ExclusiveResource> resourceList,
            TimeSpan? waitingTimeout,
            Task previousTask,
            ExclusiveResource.DeadlockDetector.Invoker? parent)
        {
            this.InternalTask = internalTask;
            this.ResourceList = resourceList;
            this.DebugInfo = parent == null ? null : new(parent, previousTask);
            this.waitingTimeoutTimer = waitingTimeout.HasValue && waitingTimeout.Value > TimeSpan.Zero
                ? new(this, waitingTimeout.Value)
                : null;
            Debug.Assert((parent != null) == (ExclusiveResource.DebugLevel >= 1));
        }

        public int Id => this.InternalTask.Id;
        public AggregateException? Exception => this.InternalTask.Exception;
        public bool IsFaulted => this.InternalTask.IsFaulted;
        public bool IsCompleted => this.InternalTask.IsCompleted;
        public bool IsCanceled => this.InternalTask.IsCanceled;
        public bool IsCompletedSuccessfully => this.InternalTask.IsCompletedSuccessfully;
        public IReadOnlyList<ExclusiveResource> ResourceList { get; }

        internal Task InternalTask { get; }
        internal DebugInformation? DebugInfo { get; }

        protected Task? WaitingTimeoutTask => waitingTimeoutTimer?.TimerTask;
        private readonly WaitingTimeoutTimer? waitingTimeoutTimer;

        public override string ToString()
            => $"{nameof(ExclusiveResourceTask)}({this.Id})";

        public Awaiter GetAwaiter()
        {
            return new(this);
        }

        public Task AsTask() => this.AsTaskInternal();
        protected abstract Task AsTaskInternal();

        internal async void Forget()
        {
            /// for <see cref="UnhandledExceptionEventHandler"/>
            await this.InternalTask;
        }

        internal void ThrowIfWaitingTimeout()
        {
            if (this.waitingTimeoutTimer == null)
                return;

            if (this.waitingTimeoutTimer.TryCancel() == false)
                this.waitingTimeoutTimer.TimerTask.GetAwaiter().GetResult();
        }

        protected abstract Task GetInternalTaskForAwaiter();

        public readonly struct Awaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task internalTask;

            internal Awaiter(ExclusiveResourceTask task)
            {
                this.internalTask = task.GetInternalTaskForAwaiter();
                if (task.DebugInfo != null)
                    ExclusiveResource.DeadlockDetector.CheckDeadlock(task);
            }

            public bool IsCompleted => this.internalTask.IsCompleted;
            public void OnCompleted(Action continuation) => this.internalTask.GetAwaiter().OnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation) => this.internalTask.GetAwaiter().UnsafeOnCompleted(continuation);
            public void GetResult() => this.internalTask.GetAwaiter().GetResult();
        }

        internal sealed class DebugInformation
        {
            public ExclusiveResource.DeadlockDetector.Invoker Parent { get; }
            public ExclusiveResource.DeadlockDetector.Invoker? Invoker { get; set; }
            public Task PreviousTask { get; }

            public DebugInformation(
                ExclusiveResource.DeadlockDetector.Invoker parent,
                Task previousTask)
            {
                this.Parent = parent;
                this.PreviousTask = previousTask;
            }
        }

        private sealed class WaitingTimeoutTimer
        {
            private readonly CancellationTokenSource cts = new();
            private readonly ExclusiveResourceTask task;
            private readonly TimeSpan timeout;
            private int result;

            public WaitingTimeoutTimer(ExclusiveResourceTask task, TimeSpan timeout)
            {
                this.task = task;
                this.timeout = timeout;
                this.TimerTask = this.Start();
            }

            public Task TimerTask { get; }

            public bool TryCancel()
            {
                if (0 != Interlocked.CompareExchange(ref this.result, 1, 0))
                    return false;

                try
                {
                    this.cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                return true;
            }

            private async Task Start()
            {
                try
                {
                    await Task.Delay(this.timeout, this.cts.Token);

                    if (0 == Interlocked.CompareExchange(ref this.result, 2, 0))
                        throw new ExclusiveResource.WaitingTimeoutException(this.task.DebugInfo?.PreviousTask);
                }
                catch (OperationCanceledException e) when (e.CancellationToken == this.cts.Token)
                {
                    Debug.Assert(this.result == 1);
                }
                finally
                {
                    Debug.Assert(this.result != 0);
                    this.cts.Dispose();
                }
            }
        }
    }

    public sealed class ExclusiveResourceTask<TResult> : ExclusiveResourceTask
    {
        private readonly TaskCompletionSource<TResult?> tcs;

        internal ExclusiveResourceTask(
            TaskCompletionSource<TResult?> tcs,
            IReadOnlyList<ExclusiveResource> resourceList,
            TimeSpan? waitingTimeout,
            Task previousTask,
            ExclusiveResource.DeadlockDetector.Invoker? parent)
            : base(tcs.Task, resourceList, waitingTimeout, previousTask, parent)
        {
            this.tcs = tcs;
        }

        internal TaskCompletionSource<TResult?> Tcs => this.tcs;

        new public ResultAwaiter GetAwaiter()
        {
            return new(this);
        }

        protected override Task AsTaskInternal() => this.AsTask();
        new public Task<TResult?> AsTask()
        {
            return this.DebugInfo != null
                ? WrapForDeadlockDetector(this)
                : this.GetInternalTaskForResultAwaiter();

            static async Task<TResult?> WrapForDeadlockDetector(ExclusiveResourceTask<TResult?> current) => await current;
        }

        protected override Task GetInternalTaskForAwaiter() => this.GetInternalTaskForResultAwaiter();
        private Task<TResult?> GetInternalTaskForResultAwaiter()
        {
            Debug.Assert(this.tcs.Task == this.InternalTask);

            var waitingTimeoutTask = this.WaitingTimeoutTask;
            return waitingTimeoutTask == null
                ? this.tcs.Task
                : Combine(this.tcs.Task, waitingTimeoutTask);

            static async Task<TResult?> Combine(Task<TResult?> internalTask, Task waitingTimeoutTask)
            {
                await Task.WhenAny(internalTask, waitingTimeoutTask);
                waitingTimeoutTask.GetAwaiter().GetResult(); // throw if timeout
                return internalTask.Result;
            }
        }

        public readonly struct ResultAwaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task<TResult?> internalTask;

            internal ResultAwaiter(ExclusiveResourceTask<TResult?> task)
            {
                this.internalTask = task.GetInternalTaskForResultAwaiter();
                if (task.DebugInfo != null)
                    ExclusiveResource.DeadlockDetector.CheckDeadlock(task);
            }

            public bool IsCompleted => this.internalTask.IsCompleted;
            public void OnCompleted(Action continuation) => this.internalTask.GetAwaiter().OnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation) => this.internalTask.GetAwaiter().UnsafeOnCompleted(continuation);
            public TResult? GetResult() => this.internalTask.GetAwaiter().GetResult();
        }

    }
}
