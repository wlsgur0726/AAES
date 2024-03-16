using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AAES.Exceptions;

namespace AAES
{
    public abstract class AAESTask
    {
        public static event Func<Exception, bool>? UnhandledExceptionHandler;

        public IReadOnlyList<AAESResource> ResourceList { get; }
        internal WaitingCancellationOptions? WaitingCancellationOptions { get; }
        internal DebugInformation? DebugInfo { get; }

        internal AAESTask(
            IReadOnlyList<AAESResource> resourceList,
            WaitingCancellationOptions? waitingCancellationOptions,
            Task previousTask,
            AAESDebug.Caller caller,
            AAESDebug.Invoker? creator)
        {
            this.ResourceList = resourceList;
            this.WaitingCancellationOptions = waitingCancellationOptions;
            this.DebugInfo = creator == null ? null : new(caller, creator, previousTask);
            Debug.Assert((creator != null) == (AAESDebug.RequiredDebugInfo));
        }

        internal abstract Task InternalTask { get; }
        public int Id => this.InternalTask.Id;
        public AggregateException? Exception => this.InternalTask.Exception;
        public bool IsFaulted => this.InternalTask.IsFaulted;
        public bool IsCompleted => this.InternalTask.IsCompleted;
        public bool IsCanceled => this.InternalTask.IsCanceled || (this.Exception?.InnerException is ICanceledException);
        public bool IsCompletedSuccessfully => this.InternalTask.IsCompletedSuccessfully;

        public override string ToString() => this.DebugInfo == null
            ? $"{nameof(AAESTask)}({this.Id})"
            : $"{nameof(AAESTask)}:{this.DebugInfo.Caller}({this.Id})";

        internal async void Forget(CancellationToken ignore)
        {
            /// for <see cref="UnhandledExceptionEventHandler"/>
            try
            {
                await this.InternalTask.ConfigureAwait(false);
            }
            catch (WaitingCanceledException ex) when (ex.CancellationToken == ignore)
            {
            }
            catch (Exception ex) when (UnhandledExceptionHandler?.Invoke(ex) ?? false)
            {
            }
        }

        public Task AsTask() => this.AsTaskImpl();
        protected abstract Task AsTaskImpl();
        protected abstract Task GetInternalTaskForAwaiter();

        public Awaiter GetAwaiter() => new(this);
        public readonly struct Awaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task internalTask;

            internal Awaiter(AAESTask task)
            {
                this.internalTask = task.GetInternalTaskForAwaiter();
                AAESDebug.DeadlockDetector.CheckDeadlock(task);
            }

            public bool IsCompleted => this.internalTask.IsCompleted;
            public void OnCompleted(Action continuation) => this.internalTask.GetAwaiter().OnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation) => this.internalTask.GetAwaiter().UnsafeOnCompleted(continuation);
            public void GetResult() => this.internalTask.GetAwaiter().GetResult();
        }

        internal sealed class DebugInformation
        {
            public AAESDebug.Caller Caller { get; }
            public AAESDebug.Invoker Creator { get; }
            public AAESDebug.AwaitingContextContainer Awaiters { get; } = new();
            public AAESDebug.Invoker? Invoker { get; set; }
            public Task PreviousTask { get; }

            public DebugInformation(
                AAESDebug.Caller caller,
                AAESDebug.Invoker creator,
                Task previousTask)
            {
                this.Caller = caller;
                this.Creator = creator;
                this.PreviousTask = previousTask;
            }
        }
    }

    public sealed class AAESTask<TResult> : AAESTask
    {
        internal TaskCompletionSource<TResult?> Tcs { get; }

        internal AAESTask(
            TaskCompletionSource<TResult?> tcs,
            IReadOnlyList<AAESResource> resourceList,
            WaitingCancellationOptions? waitingCancellationOptions,
            Task previousTask,
            AAESDebug.Caller caller,
            AAESDebug.Invoker? creator)
            : base(resourceList, waitingCancellationOptions, previousTask, caller, creator)
        {
            this.Tcs = tcs;
        }

        internal override Task InternalTask => this.Tcs.Task;

        protected override Task GetInternalTaskForAwaiter() => this.GetInternalTaskForResultAwaiter();
        private Task<TResult?> GetInternalTaskForResultAwaiter()
        {
            return this.WaitingCancellationOptions?.With(this.Tcs.Task)
                ?? this.Tcs.Task;
        }

        protected override Task AsTaskImpl() => this.AsTask();
        new public Task<TResult?> AsTask()
        {
            return AAESDebug.EnableDeadlockDetector
                ? WrapForDeadlockDetector(this)
                : this.GetInternalTaskForResultAwaiter();

            static async Task<TResult?> WrapForDeadlockDetector(AAESTask<TResult?> current) => await current;
        }

        new public ResultAwaiter GetAwaiter() => new(this);
        public readonly struct ResultAwaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task<TResult?> internalTask;

            internal ResultAwaiter(AAESTask<TResult?> task)
            {
                this.internalTask = task.GetInternalTaskForResultAwaiter();
                AAESDebug.DeadlockDetector.CheckDeadlock(task);
            }

            public bool IsCompleted => this.internalTask.IsCompleted;
            public void OnCompleted(Action continuation) => this.internalTask.GetAwaiter().OnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation) => this.internalTask.GetAwaiter().UnsafeOnCompleted(continuation);
            public TResult? GetResult() => this.internalTask.GetAwaiter().GetResult();
        }
    }
}
