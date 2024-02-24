using Microsoft.VisualBasic;
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
        public static volatile bool DebugMode
#if DEBUG
            = true;
#else
            = false;
#endif

        private static int LastId;
        public int Id { get; } = Interlocked.Increment(ref LastId);

        public DebugInformation? DebugInfo { get; }

        private static readonly Task Locked = new(() => throw new InvalidOperationException());
        private Task? lastTask;

        public ExclusiveResource(
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            this.DebugInfo = DebugMode ? new(filePath, lineNumber) : null;
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

            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            var resourceList = resources
                .Where(e => e != null)
                .Distinct()
                /// for <see cref="ExchangeLastTask"/>
                .OrderBy(e => e.Id)
                .ThenBy(e => e.GetHashCode())
                .ToList();

            var tcs = new TaskCompletionSource<object?>();
            ExchangeLastTask(resourceList, tcs.Task, out var previousTask);

            var task = new ExclusiveResourceTask(
                tcs,
                resourceList,
                previousTask,
                DeadlockDetector.Invoker.Current);

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

        private static async void InvokeAsync(
            Func<CancellationToken, ValueTask> taskFactory,
            ExclusiveResourceTask task,
            CancellationToken cancellationToken)
        {
            Debug.Assert(task.tcs != null);

            Exception? exception = null;
            try
            {
                DeadlockDetector.BeginTask(task);

                if (cancellationToken.IsCancellationRequested == false)
                    await taskFactory.Invoke(cancellationToken);
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
                    var exchanged = Interlocked.CompareExchange(ref resource.lastTask, null, task.tcs.Task);
                    Debug.Assert(exchanged != null);
                }

                if (exception == null)
                    task.tcs.SetResult(null);
                else
                    task.tcs.SetException(exception);
            }
        }

        public sealed class DebugInformation
        {
            public readonly string FilePath;
            public readonly int LineNumber;
            internal DeadlockDetector.AssignedInfo? AssignedTo;

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
            internal sealed class AssignedInfo
            {
                public ExclusiveResourceTask Task { get; init; }
                public Invoker Invoker { get; init; }

                public AssignedInfo(ExclusiveResourceTask task, Invoker invoker)
                {
                    Debug.Assert(task.debugInfo.Parent == invoker.Parent);
                    this.Task = task;
                    this.Invoker = invoker;
                }

                public override string ToString() => this.Task.ToString();
            }

            internal sealed class Invoker
            {
                private static readonly Invoker root = new(null, default);
                private static readonly AsyncLocal<Invoker> asyncLocal = new();
                private static int lastId;

                public static Invoker? Current => DebugMode
                    ? (asyncLocal.Value ?? root)
                    : null;

                public int Id { get; } = Interlocked.Increment(ref lastId);
                public Invoker? Parent { get; }
                public AssignedInfo AssignedInfo { get; }
                public ConcurrentDictionary<int, Invoker> ChildInvokers { get; } = new();
                public ConcurrentDictionary<int, ExclusiveResourceTask> ChildTasks { get; } = new();
                public ConcurrentDictionary<int, ExclusiveResourceTask> AwaitingTasks { get; } = new();

                private Invoker(Invoker? parent, ExclusiveResourceTask task)
                {
                    this.Parent = parent;
                    this.AssignedInfo = new(task, this);
                }

                public static Invoker Begin(ExclusiveResourceTask task)
                {
                    var parent = Current;
                    Debug.Assert(parent != null && DebugMode);

                    var child = asyncLocal.Value = new Invoker(parent, task);

                    var added = parent.ChildInvokers.TryAdd(child.Id, child);
                    Debug.Assert(added);

                    return child;
                }

                public static AssignedInfo End()
                {
                    var child = asyncLocal.Value;
                    Debug.Assert(child != null && DebugMode);

                    var parent = child.Parent;
                    Debug.Assert(parent != null);

                    var removed = parent.ChildInvokers.TryRemove(new(child.Id, child));
                    Debug.Assert(removed);

                    return child.AssignedInfo;
                }

                public override string ToString() => $"{nameof(Invoker)}({this.Id})";
            }

            public static void BeginTask(ExclusiveResourceTask task)
            {
                if (task.debugInfo.Activated == false)
                    return;

                var invoker = Invoker.Begin(task);

                foreach (var resource in task.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var exchanged = Interlocked.CompareExchange(
                        ref resource.DebugInfo.AssignedTo,
                        invoker.AssignedInfo,
                        null);
                    Debug.Assert(exchanged == null);
                }
            }

            public static void EndTask(ExclusiveResourceTask task)
            {
                if (task.debugInfo.Activated == false)
                    return;

                var assignedInfo = Invoker.End();
                Debug.Assert(assignedInfo.Task == task);
                Debug.Assert(assignedInfo.Invoker.Parent == task.debugInfo.Parent);
                Debug.Assert(task.debugInfo.Parent != null);

                foreach (var resource in task.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var exchanged = Interlocked.CompareExchange(
                        ref resource.DebugInfo.AssignedTo,
                        null,
                        assignedInfo);
                    Debug.Assert(exchanged == assignedInfo);
                }

                var removed = task.debugInfo.Parent.ChildTasks.TryRemove(new(task.Id, task));
                Debug.Assert(removed);
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

                    for (var i = invoker; i != null; i = i.Parent)
                    {
                        var anyDeadlockResource = task.ResourceList
                            .Intersect(i.AssignedInfo.Task.ResourceList)
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
                    Debug.Assert(task.debugInfo.Parent != null);

                    foreach (var node in GetNextNodes(task).Distinct())
                    {
                        var newFootprints = footprints.ToList();

                        if (AddFootprint(newFootprints, node.AssignedInfo) == false)
                            return newFootprints;

                        if (AddFootprint(newFootprints, node.Resource) == false)
                            return newFootprints;

                        var dectected = TraversialRAG(
                            node.AssignedInfo.Task,
                            newFootprints);

                        if (dectected != null)
                            return dectected;
                    }

                    return null;
                }

                static IEnumerable<(ExclusiveResource Resource, AssignedInfo AssignedInfo)> GetNextNodes(ExclusiveResourceTask from)
                {
                    if (from.IsCompleted)
                        yield break;

                    foreach (var resource in from.ResourceList)
                    {
                        Debug.Assert(resource.DebugInfo != null);
                        var assignedInfo = Volatile.Read(ref resource.DebugInfo.AssignedTo);
                        if (assignedInfo == null)
                            continue;

                        if (assignedInfo.Task != from)
                        {
                            yield return (resource, assignedInfo);
                            continue;
                        }

                        foreach (var childTask in assignedInfo.Invoker.AwaitingTasks.Values)
                            foreach (var e in GetNextNodes(childTask))
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

    public readonly struct ExclusiveResourceTask : IEquatable<ExclusiveResourceTask>
    {
        private readonly IReadOnlyList<ExclusiveResource>? resourceList;
        internal readonly TaskCompletionSource<object?>? tcs;
        internal readonly DebugInformation debugInfo;

        internal ExclusiveResourceTask(
            TaskCompletionSource<object?> tcs,
            IReadOnlyList<ExclusiveResource> resourceList,
            Task previousTask,
            ExclusiveResource.DeadlockDetector.Invoker? parent)
        {
            this.tcs = tcs;
            this.resourceList = resourceList;
            this.debugInfo = parent == null
                ? default
                : new()
                {
                    Parent = parent,
                    PreviousTask = previousTask,
                };

            if (this.debugInfo.Activated)
            {
                Debug.Assert(parent != null);
                var added = parent.ChildTasks.TryAdd(this.Id, this);
                Debug.Assert(added);
            }
        }

        internal Task InternalTask => this.tcs?.Task ?? Task.CompletedTask;
        public int Id => this.InternalTask.Id;
        public AggregateException? Exception => this.InternalTask.Exception;
        public bool IsFaulted => this.InternalTask.IsFaulted;
        public bool IsCompleted => this.InternalTask.IsCompleted;
        public bool IsCanceled => this.InternalTask.IsCanceled;
        public bool IsCompletedSuccessfully => this.InternalTask.IsCompletedSuccessfully;
        public IReadOnlyList<ExclusiveResource> ResourceList => this.resourceList ?? Array.Empty<ExclusiveResource>();

        public Awaiter GetAwaiter() => new(this);

        public Task AsTask()
        {
            return this.debugInfo.Activated
                ? WrapForDeadlockDetector(this)
                : this.InternalTask;

            static async Task WrapForDeadlockDetector(ExclusiveResourceTask current) => await current;
        }

        public async void Forget()
        {
            /// for <see cref="UnhandledExceptionEventHandler"/>
            await this.InternalTask;
        }

        public override string ToString()
            => $"{nameof(ExclusiveResourceTask)}({this.Id})";

        public override int GetHashCode()
            => this.InternalTask.GetHashCode();

        public bool Equals(ExclusiveResourceTask other)
            => ReferenceEquals(this.InternalTask, other.InternalTask);

        public override bool Equals(object? obj)
            => (obj is ExclusiveResourceTask other && Equals(other)) || ReferenceEquals(this.InternalTask, obj);

        public static bool operator ==(ExclusiveResourceTask left, ExclusiveResourceTask right)
            => left.Equals(right);

        public static bool operator !=(ExclusiveResourceTask left, ExclusiveResourceTask right)
            => left.Equals(right) == false;

        public readonly struct Awaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly Task internalTask;

            internal Awaiter(ExclusiveResourceTask task)
            {
                this.internalTask = task.InternalTask;
                if (task.debugInfo.Parent != null)
                    ExclusiveResource.DeadlockDetector.CheckDeadlock(task);
            }

            public bool IsCompleted => this.internalTask.IsCompleted;
            public void OnCompleted(Action continuation) => this.internalTask.GetAwaiter().OnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation) => this.internalTask.GetAwaiter().UnsafeOnCompleted(continuation);
            public object? GetResult()
            {
                this.internalTask.GetAwaiter().GetResult();
                return null;
            }
        }

        internal readonly struct DebugInformation
        {
            public ExclusiveResource.DeadlockDetector.Invoker? Parent { get; init; }
            public Task? PreviousTask { get; init; }

            public bool Activated
            {
                get
                {
                    Debug.Assert((this.Parent != null && this.PreviousTask != null)
                              || (this.Parent == null && this.PreviousTask == null));

                    Debug.Assert(ExclusiveResource.DebugMode == (this.Parent != null));

                    return this.Parent != null;
                }
            }
        }
    }
}

