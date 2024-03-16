using AAES.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AAES
{
    public static class AAESDebug
    {
        public static readonly int Level;

        public static bool Disabled => Level <= 0;
        public static bool EnableDeadlockDetector => Level >= 1;
        public static bool RequiredDebugInfo => EnableDeadlockDetector;
        public static bool StrictHeldChecking => Level >= 2;
        public static bool CaptureChildTask => Level >= 2;

        static AAESDebug()
        {
            var lvStr = Environment.GetEnvironmentVariable("AAES_DEBUG_LEVEL");
            if (int.TryParse(lvStr, out var level))
                Level = level;
            else
#if DEBUG
                Level = int.MaxValue;
#else
                Level = 0;
#endif
        }

        public static void EnsureHeldByCurrentInvoker(this AAESResource resource)
        {
            if (Disabled)
                return;

            var invoker = Invoker.Current;
            Debug.Assert(invoker != null);

            if (invoker.Held(resource) == false)
                throw new NotHeldResourceException(resource);
        }

        public readonly struct Caller
        {
            public string? FilePath { get; init; }
            public int LineNumber { get; init; }
            public override string ToString() => this.FilePath == null
                ? string.Empty
                : $"{Path.GetFileName(this.FilePath)}:{this.LineNumber}";
        }

        internal sealed class Invoker
        {
            private static readonly AsyncLocal<Invoker> asyncLocal = new();
            private static int lastId;
            private int lastAwaitId;

            public static Invoker Root { get; } = new();
            public static Invoker? Current => RequiredDebugInfo
                ? (asyncLocal.Value ?? Root)
                : null;

            public int Id { get; } = Interlocked.Increment(ref lastId);
            public AAESTask? Task { get; private set; }
            public ConcurrentDictionary<int, AAESTask> ChildTasks { get; } = new();
            public AwaitingContextContainer AwaitingContexts { get; } = new();

            public static void BeginInvoke(AAESTask task)
            {
                var invoker = asyncLocal.Value = new Invoker();
                Debug.Assert(task.DebugInfo != null);
                Debug.Assert(task.DebugInfo.Invoker == null);

                invoker.Task = task;
                task.DebugInfo.Invoker = invoker;
            }

            public static void EndInvoke(AAESTask task)
            {
                var invoker = asyncLocal.Value;
                Debug.Assert(task == invoker?.Task);
                Debug.Assert(invoker == task.DebugInfo?.Invoker);

                invoker.Task = null;
                task.DebugInfo.Invoker = null;
            }

            public AwaitingContext BeginAwait(AAESTask task)
            {
                Debug.Assert(task.DebugInfo != null);

                var ctx = new AwaitingContext()
                {
                    Invoker = this,
                    AwaitId = Interlocked.Increment(ref this.lastAwaitId),
                    Task = task,
                };

                bool added;
                added = this.AwaitingContexts.TryAdd(ctx);
                Debug.Assert(added);
                added = task.DebugInfo.Awaiters.TryAdd(ctx);
                Debug.Assert(added);

                return ctx;
            }

            public void EndAwait(AwaitingContext ctx)
            {
                Debug.Assert(ctx.Task.DebugInfo != null);

                bool removed;
                removed = ctx.Task.DebugInfo.Awaiters.TryRemove(ctx);
                Debug.Assert(removed);
                removed = this.AwaitingContexts.TryRemove(ctx);
                Debug.Assert(removed);
            }

            public bool Held(AAESResource resource)
            {
                var task = this.Task;
                if (task == null || task.IsCompleted)
                    return false;

                if (task.ResourceList.Contains(resource))
                    return true;

                Debug.Assert(task.DebugInfo != null);

                if (StrictHeldChecking == false)
                {
                    if (task.DebugInfo.Creator.Held(resource))
                        return true;

                    foreach (var otherInvoker in task.DebugInfo.Awaiters.GetInvokes())
                    {
                        if (otherInvoker.Held(resource))
                            return true;
                    }
                }

                return false;
            }

            public override string ToString() => $"{nameof(Invoker)}({this.Id})";
        }

        internal static class DeadlockDetector
        {
            public static void BeginTask(AAESTask task)
            {
                if (EnableDeadlockDetector == false)
                    return;

                Invoker.BeginInvoke(task);

                foreach (var resource in task.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var exchanged = Interlocked.CompareExchange(
                        ref resource.DebugInfo.Holder,
                        task,
                        null);
                    Debug.Assert(exchanged == null);
                }
            }

            public static void EndTask(AAESTask task)
            {
                if (EnableDeadlockDetector == false)
                    return;

                Invoker.EndInvoke(task);

                foreach (var resource in task.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var exchanged = Interlocked.CompareExchange(
                        ref resource.DebugInfo.Holder,
                        null,
                        task);
                    Debug.Assert(exchanged == task);
                }

                if (CaptureChildTask)
                {
                    Debug.Assert(task.DebugInfo != null);
                    var removed = task.DebugInfo.Creator.ChildTasks.TryRemove(new(task.Id, task));
                    Debug.Assert(removed);
                }
            }

            public static void CheckDeadlock(AAESTask task)
            {
                if (EnableDeadlockDetector == false)
                    return;

                if (task.IsCompleted)
                    return;

                var invoker = Invoker.Current;
                Debug.Assert(invoker != null);
                Debug.Assert(task.DebugInfo != null);

                AwaitingContext? rollbackCtx = null;
                try
                {
                    var ctx = invoker.BeginAwait(task);
                    rollbackCtx = ctx;

                    while (true)
                    {
                        var deadlockList = TraversialRAG(task, new());
                        if (deadlockList == null)
                            break;

                        if (HasResolvedState(deadlockList) == false)
                            throw new DeadlockDetectedException(deadlockList);
                    }

                    rollbackCtx = null;
                    task.InternalTask.ContinueWith(_ => invoker.EndAwait(ctx));
                }
                finally
                {
                    if (rollbackCtx.HasValue)
                        invoker.EndAwait(rollbackCtx.Value);
                }
            }

            private static IReadOnlyList<object>? TraversialRAG(
                AAESTask task,
                List<object> footprints)
            {
                foreach (var node in GetNextNodes(task).Distinct())
                {
                    var newFootprints = footprints.ToList();

                    if (AddFootprint(newFootprints, node.Resource) == false)
                        return newFootprints;

                    if (AddFootprint(newFootprints, node.Holder) == false)
                        return newFootprints;

                    var dectected = TraversialRAG(
                        node.Holder,
                        newFootprints);

                    if (dectected != null)
                        return dectected;
                }

                return null;
            }

            private static IEnumerable<(AAESResource Resource, AAESTask Holder)> GetNextNodes(AAESTask current)
            {
                if (current.IsCompleted)
                    yield break;

                foreach (var resource in current.ResourceList)
                {
                    Debug.Assert(resource.DebugInfo != null);
                    var holder = Volatile.Read(ref resource.DebugInfo.Holder);
                    if (holder == null)
                        continue; // 아무도 점유하지 않은 resource는 순환이 감지되지 않는다.

                    if (holder.IsCompleted)
                        continue; // 점유하던 task가 종료되었으니 곧 점유가 해제될 예정이다.

                    if (holder != current)
                        yield return (resource, holder); // 이 resource를 다른 task가 점유했다면 그것이 다음 node이다.
                }

                // 이 task가 현재 실행중이라면,
                // 그 invoker가 점유 시도하는 resource들을 살펴서
                // 교차 데드락 여부를 확인해야 한다.
                Debug.Assert(current.DebugInfo != null);
                var invoker = current.DebugInfo.Invoker;
                if (invoker != null)
                {
                    foreach (var otherTask in invoker.AwaitingContexts.GetTasks())
                        foreach (var e in GetNextNodes(otherTask))
                            yield return e;
                }
            }

            private static bool HasResolvedState(IReadOnlyList<object> footprints)
            {
                for (var i = 0; i < footprints.Count; i++)
                {
                    switch (footprints[i])
                    {
                        case AAESResource resource:
                            Debug.Assert(resource.DebugInfo != null);
                            var next = i + 1;
                            if (next < footprints.Count)
                            {
                                var expectedNext = resource.DebugInfo.Holder;
                                var actualNext = footprints[next];
                                if (ReferenceEquals(expectedNext, actualNext) == false)
                                    return true;
                            }
                            continue;

                        case AAESTask task:
                            if (task.IsCompleted)
                                return true;
                            continue;
                    }

                    Debug.Fail($"unexpected type {footprints[i].GetType()}");
                }

                return false;
            }

            private static bool AddFootprint(List<object> footprints, object node)
            {
                var detectedCircularRef = footprints.Contains(node);
                footprints.Add(node);
                return detectedCircularRef == false;
            }
        }

        internal readonly struct AwaitingContext : IEquatable<AwaitingContext>
        {
            public Invoker Invoker { get; init; }
            public int AwaitId { get; init; }
            public AAESTask Task { get; init; }

            public bool Equals(AwaitingContext other)
                => this.Invoker.Id == other.Invoker.Id && this.AwaitId == other.AwaitId;
            public override bool Equals(object? obj)
                => obj is AwaitingContext other && Equals(other);
            public override int GetHashCode()
                => (((long)this.Invoker.Id << 32) | (long)this.AwaitId).GetHashCode();
            public override string ToString()
                => $"Awaiting({this.Invoker.Id}-{this.AwaitId})";
        }

        internal sealed class AwaitingContextContainer
        {
            private readonly Dictionary<int, HashSet<AwaitingContext>> contextsByTaskId = new();

            public bool TryAdd(AwaitingContext ctx)
            {
                lock (this.contextsByTaskId)
                {
                    if (this.contextsByTaskId.TryGetValue(ctx.Task.Id, out var set) == false)
                        this.contextsByTaskId.Add(ctx.Task.Id, set = new());
                    return set.Add(ctx);
                }
            }

            public bool TryRemove(AwaitingContext ctx)
            {
                lock (this.contextsByTaskId)
                {
                    if (this.contextsByTaskId.TryGetValue(ctx.Task.Id, out var set) == false)
                        return false;

                    var removed = set.Remove(ctx);
                    if (set.Count == 0)
                        this.contextsByTaskId.Remove(ctx.Task.Id);

                    return removed;
                }
            }

            public IReadOnlyCollection<AAESTask> GetTasks()
            {
                var tasks = new List<AAESTask>(this.contextsByTaskId.Count);
                lock (this.contextsByTaskId)
                {
                    foreach (var set in this.contextsByTaskId.Values)
                        tasks.Add(set.First().Task);
                }
                return tasks;
            }

            public IReadOnlyCollection<Invoker> GetInvokes()
            {
                var invokers = new HashSet<Invoker>();
                lock (this.contextsByTaskId)
                {
                    foreach (var set in this.contextsByTaskId.Values)
                        foreach (var ctx in set)
                            invokers.Add(ctx.Invoker);
                }
                return invokers;
            }
        }
    }
}
