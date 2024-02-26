﻿using Microsoft.VisualBasic;
using Microsoft.VisualStudio.TestPlatform;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Resources;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Dev
{
    public sealed class ExclusiveResource
    {
        public static readonly int DebugLevel;

        static ExclusiveResource()
        {
            var debugLevelStr = Environment.GetEnvironmentVariable("EXCLUSIVE_RESOURCE_DEBUG_LEVEL");
            if (int.TryParse(debugLevelStr, out var debugLevel))
                DebugLevel = debugLevel;
            else
#if DEBUG
                DebugLevel = 1;
#else
                DebugLevel = 0;
#endif
        }

        private static int LastId;
        public int Id { get; } = Interlocked.Increment(ref LastId);

        public DebugInformation? DebugInfo { get; }

        private static readonly Task Locked = new(() => throw new InvalidOperationException());
        private Task? lastTask;

        public ExclusiveResource(
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            this.DebugInfo = DebugLevel >= 1 ? new(filePath, lineNumber) : null;
        }

        public override string ToString() => this.DebugInfo == null
            ? $"{nameof(ExclusiveResource)}({this.Id})"
            : $"{nameof(ExclusiveResource)}:{this.DebugInfo}({this.Id})";

        public void Access(
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask> taskFactory)
        {
            Access(new[] { this }, cancellationToken, taskFactory);
        }

        public ExclusiveResourceTask AwaitableAccess(
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask> taskFactory)
        {
            return AwaitableAccess(new[] { this }, cancellationToken, taskFactory);
        }

        public ExclusiveResourceTask<TResult?> AwaitableAccess<TResult>(
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask<TResult?>> taskFactory)
        {
            return AwaitableAccess(new[] { this }, cancellationToken, taskFactory);
        }

        public static void Access(
            IEnumerable<ExclusiveResource> resources,
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask> taskFactory)
        {
            AwaitableAccess(resources, cancellationToken, taskFactory).Forget();
        }

        public static ExclusiveResourceTask AwaitableAccess(
            IEnumerable<ExclusiveResource> resources,
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask> taskFactory)
        {
            if (taskFactory == null)
                throw new ArgumentException(nameof(taskFactory));

            return AwaitableAccess<object?>(
                resources,
                cancellationToken,
                async cancellationToken =>
                {
                    await taskFactory.Invoke(cancellationToken);
                    return null;
                });
        }

        public static ExclusiveResourceTask<TResult> AwaitableAccess<TResult>(
            IEnumerable<ExclusiveResource> resources,
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask<TResult>> taskFactory)
        {
            if (taskFactory == null)
                throw new ArgumentException(nameof(taskFactory));

            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            var resourceList = resources
                .Where(e => e != null)
                .Distinct()
                /// for <see cref="ExchangeLastTask"/>
                .OrderBy(e => e.Id)
                .ThenBy(e => e.GetHashCode())
                .ToList();

            var tcs = new TaskCompletionSource<TResult?>();
            ExchangeLastTask(resourceList, tcs.Task, out var previousTask);

            var invoker = DeadlockDetector.Invoker.Current;
            var task = new ExclusiveResourceTask<TResult>(tcs, resourceList, previousTask, invoker);

            if (DebugLevel >= 2)
            {
                Debug.Assert(invoker != null);
                var added = invoker.ChildTasks.TryAdd(task.Id, task);
                Debug.Assert(added);
            }

            if (previousTask.IsCompleted)
                InvokeAsync(taskFactory, task, cancellationToken);
            else
                previousTask.ContinueWith(_ => InvokeAsync(taskFactory, task, cancellationToken));

            return task;
        }

        private static void ExchangeLastTask(
            IReadOnlyList<ExclusiveResource> resourceList,
            Task currentTask,
            out Task previousTask)
        {
            if (resourceList.Count == 0)
            {
                previousTask = Task.CompletedTask;
                return;
            }

            while (resourceList.Count == 1)
            {
                var snapshot = Volatile.Read(ref resourceList[0].lastTask);
                if (snapshot == Locked)
                    continue;

                if (snapshot != Interlocked.CompareExchange(ref resourceList[0].lastTask, currentTask, snapshot))
                    continue;

                previousTask = snapshot ?? Task.CompletedTask;
                return;
            }

            var snapshots = new Task?[resourceList.Count];
            var exchangeds = new Task?[resourceList.Count];

            RETRY:
            for (var i = 0; i < resourceList.Count; ++i)
            {
                snapshots[i] = Volatile.Read(ref resourceList[i].lastTask);
                if (snapshots[i] == Locked)
                    goto RETRY;
            }

            // locking...
            for (var i = 0; i < resourceList.Count; ++i)
            {
                exchangeds[i] = Interlocked.CompareExchange(ref resourceList[i].lastTask, Locked, snapshots[i]);
                if (exchangeds[i] != snapshots[i])
                {
                    // rollback...
                    for (var j = i - 1; j >= 0; --j)
                    {
                        var t = Interlocked.Exchange(ref resourceList[j].lastTask, exchangeds[j]);
                        Debug.Assert(t == Locked);
                    }

                    goto RETRY;
                }
            }

            // update
            for (var i = 0; i < resourceList.Count; ++i)
            {
                var t = Interlocked.Exchange(ref resourceList[i].lastTask, currentTask);
                Debug.Assert(t == Locked);
            }

            #pragma warning disable CS8620
            var previousTaskSet = new HashSet<Task>(exchangeds.Where(e => e != null));
            #pragma warning restore CS8620
            previousTask = previousTaskSet.Count switch
            {
                0 => Task.CompletedTask,
                1 => previousTaskSet.First(),
                _ => Task.WhenAll(previousTaskSet)
            };
        }

        private static async void InvokeAsync<TResult>(
            Func<CancellationToken, ValueTask<TResult>> taskFactory,
            ExclusiveResourceTask<TResult> task,
            CancellationToken cancellationToken)
        {
            Exception? exception = null;
            TResult? result = default;
            try
            {
                DeadlockDetector.BeginTask(task);

                if (cancellationToken.IsCancellationRequested == false)
                    result = await taskFactory.Invoke(cancellationToken);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                DeadlockDetector.EndTask(task);

                foreach (var resource in task.ResourceList)
                {
                    var exchanged = Interlocked.CompareExchange(ref resource.lastTask, null, task.InternalTask);
                    Debug.Assert(exchanged != null);
                }

                if (exception == null)
                    task.Tcs.SetResult(result);
                else
                    task.Tcs.SetException(exception);
            }
        }

        public sealed class DebugInformation
        {
            public readonly string FilePath;
            public readonly int LineNumber;
            internal ExclusiveResourceTask? AssignedTo;

            internal DebugInformation(string filePath, int lineNumber)
            {
                FilePath = filePath;
                LineNumber = lineNumber;
            }

            public override string ToString()
                => $"{Path.GetFileName(this.FilePath)}:{this.LineNumber}";
        }

        internal static class DeadlockDetector
        {
            internal sealed class Invoker
            {
                private static readonly AsyncLocal<Invoker> asyncLocal = new();
                private static int lastId;

                public static Invoker Root { get; } = new(null, null);
                public static Invoker? Current => DebugLevel >= 1
                    ? (asyncLocal.Value ?? Root)
                    : null;

                public int Id { get; } = Interlocked.Increment(ref lastId);
                public Invoker? Parent { get; }
                public ExclusiveResourceTask? Task { get; }
                public ConcurrentDictionary<int, Invoker> ChildInvokers { get; } = new();
                public ConcurrentDictionary<int, ExclusiveResourceTask> ChildTasks { get; } = new();
                public ConcurrentDictionary<int, ExclusiveResourceTask> AwaitingTasks { get; } = new();

                private Invoker(Invoker? parent, ExclusiveResourceTask? task)
                {
                    this.Parent = parent;
                    this.Task = task;
                }

                public static void Begin(ExclusiveResourceTask task)
                {
                    var parent = Current;
                    Debug.Assert(parent != null && task.DebugInfo != null);

                    var child = asyncLocal.Value = new Invoker(parent, task);
                    task.DebugInfo.Invoker = child;

                    if (DebugLevel >= 2)
                    {
                        var added = parent.ChildInvokers.TryAdd(child.Id, child);
                        Debug.Assert(added);
                    }
                }

                public static void End()
                {
                    var child = asyncLocal.Value;
                    Debug.Assert(child != null && DebugLevel >= 1);

                    var parent = child.Parent;
                    Debug.Assert(parent != null);

                    if (DebugLevel >= 2)
                    {
                        var removed = parent.ChildInvokers.TryRemove(new(child.Id, child));
                        Debug.Assert(removed);
                    }
                }

                public override string ToString() => $"{nameof(Invoker)}({this.Id})";
            }

            public static void BeginTask(ExclusiveResourceTask task)
            {
                if (task.DebugInfo == null)
                    return;

                Invoker.Begin(task);

                foreach (var resource in task.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var exchanged = Interlocked.CompareExchange(
                        ref resource.DebugInfo.AssignedTo,
                        task,
                        null);
                    Debug.Assert(exchanged == null);
                }
            }

            public static void EndTask(ExclusiveResourceTask task)
            {
                if (task.DebugInfo == null)
                    return;

                Invoker.End();

                foreach (var resource in task.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var exchanged = Interlocked.CompareExchange(
                        ref resource.DebugInfo.AssignedTo,
                        null,
                        task);
                    Debug.Assert(exchanged == task);
                }

                if (DebugLevel >= 2)
                {
                    var removed = task.DebugInfo.Parent.ChildTasks.TryRemove(new(task.Id, task));
                    Debug.Assert(removed);
                }
            }

            public static void CheckDeadlock(ExclusiveResourceTask task)
            {
                if (task.IsCompleted)
                    return;

                var invoker = Invoker.Current;
                Debug.Assert(invoker != null);

                var needRollback = true;
                try
                {
                    var added = invoker.AwaitingTasks.TryAdd(task.Id, task);
                    Debug.Assert(added);

                    for (var i = invoker; i != Invoker.Root; i = i.Parent)
                    {
                        Debug.Assert(i?.Task != null);
                        var anyDeadlockResource = task.ResourceList
                            .Intersect(i.Task.ResourceList)
                            .FirstOrDefault();
                        if (anyDeadlockResource != null)
                            throw new DeadlockDetectedException(new[] { anyDeadlockResource });
                    }

                    var deadlockList = TraversialRAG(task, new());
                    if (deadlockList != null)
                        throw new DeadlockDetectedException(deadlockList);

                    needRollback = false;
                    task.InternalTask.ContinueWith(_ =>
                    {
                        var removed = invoker.AwaitingTasks.TryRemove(new(task.Id, task));
                        Debug.Assert(removed);
                    });
                }
                finally
                {
                    if (needRollback)
                    {
                        var removed = invoker.AwaitingTasks.TryRemove(new(task.Id, task));
                        Debug.Assert(removed);
                    }
                }

                static IReadOnlyList<object>? TraversialRAG(
                    ExclusiveResourceTask task,
                    List<object> footprints)
                {
                    Debug.Assert(task.DebugInfo.Parent != null);

                    foreach (var node in GetNextNodes(task).Distinct())
                    {
                        var newFootprints = footprints.ToList();

                        if (AddFootprint(newFootprints, node.Resource) == false)
                            return newFootprints;

                        if (AddFootprint(newFootprints, node.AssignedTo) == false)
                            return newFootprints;

                        var dectected = TraversialRAG(
                            node.AssignedTo,
                            newFootprints);

                        if (dectected != null)
                            return dectected;
                    }

                    return null;
                }

                static IEnumerable<(ExclusiveResource Resource, ExclusiveResourceTask AssignedTo)> GetNextNodes(ExclusiveResourceTask from)
                {
                    if (from.IsCompleted)
                        yield break;

                    foreach (var resource in from.ResourceList)
                    {
                        Debug.Assert(resource.DebugInfo != null);
                        var assignedTo = Volatile.Read(ref resource.DebugInfo.AssignedTo);
                        if (assignedTo == null)
                            continue;

                        if (assignedTo != from)
                        {
                            yield return (resource, assignedTo);
                            continue;
                        }

                        Debug.Assert(assignedTo.DebugInfo?.Invoker != null);
                        foreach (var otherTask in assignedTo.DebugInfo.Invoker.AwaitingTasks.Values)
                            foreach (var e in GetNextNodes(otherTask))
                                yield return e;
                    }

                }

                static bool AddFootprint(List<object> footprints, object node)
                {
                    var detectedCircularRef = footprints.Contains(node);
                    footprints.Add(node);
                    return detectedCircularRef == false;
                }
            }
        }

        public sealed class DeadlockDetectedException : Exception
        {
            public DeadlockDetectedException(IReadOnlyList<object> deadlockNodeList)
                : base(new StringBuilder()
                      .AppendLine("deadlock detected.")
                      .AppendJoin(Environment.NewLine, deadlockNodeList.Select(e => $"  {e}"))
                      .ToString())
            {
                this.DeadlockNodeList = deadlockNodeList;
            }

            public IReadOnlyList<object> DeadlockNodeList { get; }
        }
    }

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

        public async void Forget()
        {
            /// for <see cref="UnhandledExceptionEventHandler"/>
            await this;
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

    public class ExclusiveResourceTask<TResult> : ExclusiveResourceTask
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

            static async Task<TResult?> WrapForDeadlockDetector(ExclusiveResourceTask<TResult> current) => await current;
        }

        new public ResultAwaiter GetAwaiter()
        {
            return new(this);
        }

        public readonly struct ResultAwaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task<TResult?> internalTask;

            internal ResultAwaiter(ExclusiveResourceTask<TResult> task)
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

