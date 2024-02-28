using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Lib
{
    public abstract class ExclusiveResourceTask
    {
        internal ExclusiveResourceTask(
            Task internalTask,
            IReadOnlyList<ExclusiveResource> resourceList,
            Task previousTask,
            ExclusiveResource.DeadlockDetector.Invoker? parent)
        {
            this.InternalTask = internalTask;
            this.ResourceList = resourceList;
            this.DebugInfo = parent == null ? null : new(parent, previousTask);
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

        public override string ToString()
            => $"{nameof(ExclusiveResourceTask)}({this.Id})";

        public Awaiter GetAwaiter()
        {
            return new(this);
        }

        public Task AsTask()
        {
            return this.DebugInfo != null
                ? WrapForDeadlockDetector(this)
                : this.InternalTask;

            static async Task WrapForDeadlockDetector(ExclusiveResourceTask current) => await current;
        }

        internal async void Forget()
        {
            /// for <see cref="UnhandledExceptionEventHandler"/>
            await this.InternalTask;
        }

        public readonly struct Awaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task internalTask;

            internal Awaiter(ExclusiveResourceTask task)
            {
                this.internalTask = task.InternalTask;
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
    }

    public sealed class ExclusiveResourceTask<TResult> : ExclusiveResourceTask
    {
        private readonly TaskCompletionSource<TResult?> tcs;

        internal ExclusiveResourceTask(
            TaskCompletionSource<TResult?> tcs,
            IReadOnlyList<ExclusiveResource> resourceList,
            Task previousTask,
            ExclusiveResource.DeadlockDetector.Invoker? parent)
            : base(tcs.Task, resourceList, previousTask, parent)
        {
            this.tcs = tcs;
        }

        internal TaskCompletionSource<TResult?> Tcs => this.tcs;

        new public Task<TResult?> AsTask()
        {
            return this.DebugInfo != null
                ? WrapForDeadlockDetector(this)
                : this.tcs.Task;

            static async Task<TResult?> WrapForDeadlockDetector(ExclusiveResourceTask<TResult?> current) => await current;
        }

        new public ResultAwaiter GetAwaiter()
        {
            return new(this);
        }

        public readonly struct ResultAwaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task<TResult?> internalTask;

            internal ResultAwaiter(ExclusiveResourceTask<TResult?> task)
            {
                this.internalTask = task.tcs.Task;
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
