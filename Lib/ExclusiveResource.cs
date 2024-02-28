using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lib
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
                DebugLevel = int.MaxValue;
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
            if (DebugLevel >= 1)
                this.DebugInfo = new(filePath, lineNumber);
        }

        public override string ToString() => this.DebugInfo == null
            ? $"{nameof(ExclusiveResource)}({this.Id})"
            : $"{nameof(ExclusiveResource)}:{this.DebugInfo}({this.Id})";

        public AccessTrigger Access => new(new[] { this });
        public static AccessTrigger AccessTo(params ExclusiveResource[] resources) => new(resources);
        public static AccessTrigger AccessTo(IEnumerable<ExclusiveResource> resources) => new(resources);

        public static ExclusiveResourceTask<TResult?> AwaitableAccess<TResult>(
            IEnumerable<ExclusiveResource> resources,
            CancellationOption cancellationOption,
            TimeSpan? waitingTimeout,
            Func<CancellationToken, ValueTask<TResult?>> taskFactory)
        {
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory));

            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            var resourceList = resources
                .Where(e => e != null)
                .Distinct()
                /// for <see cref="Locked"/>
                .OrderBy(e => e.Id)
                .ThenBy(e => e.GetHashCode())
                .ToList();

            var tcs = new TaskCompletionSource<TResult?>();
            ExchangeLastTask(resourceList, tcs.Task, out var previousTask);

            var invoker = DeadlockDetector.Invoker.Current;
            var task = new ExclusiveResourceTask<TResult?>(tcs, resourceList, previousTask, invoker);

            if (DebugLevel >= 2)
            {
                Debug.Assert(invoker != null);
                var added = invoker.ChildTasks.TryAdd(task.Id, task);
                Debug.Assert(added);
            }

            if (previousTask.IsCompleted)
            {
                InvokeAsync(taskFactory, null, cancellationOption, task);
            }
            else
            {
                var waitingTimeoutTimer = waitingTimeout.HasValue
                    ? new WaitingTimeoutTimer<TResult>(task, waitingTimeout.Value)
                    : null;
                previousTask.ContinueWith(_ => InvokeAsync(taskFactory, waitingTimeoutTimer, cancellationOption, task));
            }

            return task;
        }

        /// <summary>
        /// 모든 resource들의 <see cref="lastTask"/>를 원자적으로 교체한다.
        /// 원자적이게 하지 않으면 아래 예시처럼 데드락 발생 가능.
        /// <code>
        /// ex) t1, t2가 동시에 r1, r2를 점유 시도
        ///     event\state         r1.lastTask     r2.lastTask     t1.previousTask     t2.previousTask
        ///     ----------------------------------------------------------------------------------------
        ///     example state       t0              t0              unknown             unknown
        ///     exchange r1 (t1)    t1              t0              t0                  unknown
        ///     exchange r1 (t2)    t2              t0              t0                  t1
        ///     exchange r2 (t2)    t2              t2              t0                  t1, t0
        ///     exchange r2 (t1)    t2              t1              t0, t2              t1, t0
        /// </code>
        /// </summary>
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
            // get snapshot for locking
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
            Func<CancellationToken, ValueTask<TResult?>> taskFactory,
            WaitingTimeoutTimer<TResult>? waitingTimeoutTimer,
            CancellationOption cancellationOption,
            ExclusiveResourceTask<TResult?> task)
        {
            bool isWaitingTimeout = false;
            TResult? result = default;
            Exception? exception = null;
            try
            {
                DeadlockDetector.BeginTask(task);

                if (waitingTimeoutTimer != null && waitingTimeoutTimer.TryCancel() == false)
                {
                    isWaitingTimeout = true;
                    return;
                }

                if (cancellationOption.Token.IsCancellationRequested)
                {
                    if (cancellationOption.SuppressException)
                        return;
                    else
                        throw new OperationCanceledException(cancellationOption.Token);
                }

                result = await taskFactory.Invoke(cancellationOption.Token);
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

                if (exception != null)
                    task.Tcs.TrySetException(exception);
                else if (isWaitingTimeout == false)
                    task.Tcs.TrySetResult(result);
            }
        }

        public ref struct AccessTrigger
        {
            private IEnumerable<ExclusiveResource> resources;
            private CancellationOption cancellationOption;
            private TimeSpan? waitingTimeout;

            internal AccessTrigger(IEnumerable<ExclusiveResource> resources)
            {
                this.resources = resources;
                this.cancellationOption = default;
                this.waitingTimeout = null;
            }

            public AccessTrigger WithCancellationOption(CancellationOption option)
            {
                this.cancellationOption = option;
                return this;
            }

            public AccessTrigger WithWaitingTimeout(TimeSpan timeout)
            {
                this.waitingTimeout = timeout;
                return this;
            }

            public void Then(Func<CancellationToken, ValueTask> taskFactory)
            {
                this.ThenAsync(taskFactory).Forget();
            }

            public ExclusiveResourceTask ThenAsync(Func<CancellationToken, ValueTask> taskFactory)
            {
                if (taskFactory == null)
                    throw new ArgumentNullException(nameof(taskFactory));

                return this.ThenAsync<object>(async ct =>
                {
                    await taskFactory.Invoke(ct);
                    return null;
                });
            }

            public ExclusiveResourceTask<TResult?> ThenAsync<TResult>(Func<CancellationToken, ValueTask<TResult?>> taskFactory)
            {
                return AwaitableAccess(
                    resources: this.resources,
                    cancellationOption: this.cancellationOption,
                    waitingTimeout: this.waitingTimeout,
                    taskFactory: taskFactory);
            }
        }

        public readonly struct CancellationOption
        {
            public CancellationToken Token { get; init; }
            public bool SuppressException { get; init; } 
        }

        private sealed class WaitingTimeoutTimer<TResult>
        {
            private readonly CancellationTokenSource cts = new();
            private int result;

            public WaitingTimeoutTimer(ExclusiveResourceTask<TResult?> task, TimeSpan timeout)
            {
                this.Start(task, timeout);
            }

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

            private async void Start(ExclusiveResourceTask<TResult?> task, TimeSpan timeout)
            {
                try
                {
                    await Task.Delay(timeout, this.cts.Token);

                    if (0 == Interlocked.CompareExchange(ref this.result, 2, 0))
                        task.Tcs.TrySetException(new WaitingTimeoutException(task.DebugInfo?.PreviousTask));
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

        public sealed class DebugInformation
        {
            public readonly string FilePath;
            public readonly int LineNumber;
            internal ExclusiveResourceTask? AssignedTo;

            internal DebugInformation(string filePath, int lineNumber)
            {
                this.FilePath = filePath;
                this.LineNumber = lineNumber;
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

                public static void End(ExclusiveResourceTask task)
                {
                    var child = asyncLocal.Value;
                    Debug.Assert(child != null && task.DebugInfo != null);
                    Debug.Assert(child == task.DebugInfo.Invoker);
                    task.DebugInfo.Invoker = null;

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

                Invoker.End(task);

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

                    while (true)
                    {
                        var deadlockList = TraversialRAG(task, new());
                        if (deadlockList == null)
                            break;

                        if (HasResolvedState(deadlockList) == false)
                            throw new DeadlockDetectedException(deadlockList);
                    }

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

                static IEnumerable<(ExclusiveResource Resource, ExclusiveResourceTask AssignedTo)> GetNextNodes(ExclusiveResourceTask current)
                {
                    if (current.IsCompleted)
                        yield break;

                    foreach (var resource in current.ResourceList)
                    {
                        Debug.Assert(resource.DebugInfo != null);
                        var assignedTo = Volatile.Read(ref resource.DebugInfo.AssignedTo);
                        if (assignedTo == null)
                            continue; // 아무도 점유하지 않은 resource는 순환이 감지되지 않는다.

                        if (assignedTo.IsCompleted)
                            continue; // 점유하던 task가 종료되었으니 곧 점유가 해제될 예정이다.

                        if (assignedTo != current)
                            yield return (resource, assignedTo); // 이 resource를 다른 task가 점유했다면 그것이 다음 node이다.
                    }

                    // 이 task가 현재 실행중이라면,
                    // 그 invoker가 점유 시도하는 resource들을 살펴서
                    // 교차 데드락 여부를 확인해야 한다.
                    Debug.Assert(current.DebugInfo != null);
                    var invoker = current.DebugInfo.Invoker;
                    if (invoker != null)
                    {
                        foreach (var otherTask in invoker.AwaitingTasks.Values)
                            foreach (var e in GetNextNodes(otherTask))
                                yield return e;
                    }
                }

                static bool HasResolvedState(IReadOnlyList<object> footprints)
                {
                    for (var i = 0; i < footprints.Count; i++)
                    {
                        switch (footprints[i])
                        {
                            case ExclusiveResource resource:
                                Debug.Assert(resource.DebugInfo != null);
                                var next = i + 1;
                                if (next < footprints.Count)
                                {
                                    var expectedNext = resource.DebugInfo.AssignedTo;
                                    var actualNext = footprints[next];
                                    if (ReferenceEquals(expectedNext, actualNext) == false)
                                        return true;
                                }
                                continue;

                            case ExclusiveResourceTask task:
                                if (task.IsCompleted)
                                    return true;
                                continue;
                        }

                        Debug.Fail($"unexpected type {footprints[i].GetType()}");
                    }

                    return false;
                }

                static bool AddFootprint(List<object> footprints, object node)
                {
                    var detectedCircularRef = footprints.Contains(node);
                    footprints.Add(node);
                    return detectedCircularRef == false;
                }
            }
        }

        public class DeadlockDetectedException : Exception
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

        public class WaitingTimeoutException : TimeoutException
        {
            public WaitingTimeoutException(Task? previousTask)
                : base(previousTask == null ? null : $"PreviousTask.Id is {previousTask.Id}")
            {
                Debug.Assert((previousTask != null) == (DebugLevel >= 1));
                this.PreviousTask = previousTask;
            }

            public Task? PreviousTask { get; }
        }
    }

}

