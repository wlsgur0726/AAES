using AAES.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
            var invoker = Invoker.Current;
            if (invoker == null)
            {
                Debug.Assert(Disabled);
                return;
            }

            if (invoker.Held(resource) == false)
                throw new NotHeldResourceException(resource);
        }

        internal sealed class Invoker
        {
            private static readonly AsyncLocal<Invoker> asyncLocal = new();
            private static int lastId;

            public static Invoker Root { get; } = new();
            public static Invoker? Current => RequiredDebugInfo
                ? (asyncLocal.Value ?? Root)
                : null;

            public int Id { get; } = Interlocked.Increment(ref lastId);
            public AAESTask? Task { get; private set; }
            public ConcurrentDictionary<int, AAESTask> ChildTasks { get; } = new();
            public ConcurrentDictionary<int, AAESTask> AwaitingTasks { get; } = new();

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

            public void BeginAwait(AAESTask task)
            {
                Debug.Assert(task.DebugInfo != null);

                var added = true;
                added &= this.AwaitingTasks.TryAdd(task.Id, task);
                added &= task.DebugInfo.Awaiters.TryAdd(this.Id, this);
                Debug.Assert(added);
            }

            public void EndAwait(AAESTask task)
            {
                Debug.Assert(task.DebugInfo != null);

                var removed = true;
                removed &= task.DebugInfo.Awaiters.TryRemove(new(this.Id, this));
                removed &= this.AwaitingTasks.TryRemove(new(task.Id, task));
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

                    foreach (var otherInvoker in task.DebugInfo.Awaiters.Values)
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
                if (task.DebugInfo == null)
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
                if (task.DebugInfo == null)
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
                    var removed = task.DebugInfo.Creator.ChildTasks.TryRemove(new(task.Id, task));
                    Debug.Assert(removed);
                }
            }

            public static void CheckDeadlock(AAESTask task)
            {
                if (task.IsCompleted)
                    return;

                var invoker = Invoker.Current;
                Debug.Assert(invoker != null);
                Debug.Assert(task.DebugInfo != null);

                var needRollback = true;
                try
                {
                    invoker.BeginAwait(task);
                    while (true)
                    {
                        var deadlockList = TraversialRAG(task, new());
                        if (deadlockList == null)
                            break;

                        if (HasResolvedState(deadlockList) == false)
                            throw new DeadlockDetectedException(deadlockList);
                    }

                    needRollback = false;
                    task.InternalTask.ContinueWith(_ => invoker.EndAwait(task));
                }
                finally
                {
                    if (needRollback)
                        invoker.EndAwait(task);
                }
            }

            static IReadOnlyList<object>? TraversialRAG(
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
                    foreach (var otherTask in invoker.AwaitingTasks.Values)
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
    }
}
