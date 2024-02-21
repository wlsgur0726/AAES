using Microsoft.VisualBasic;
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
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Dev
{
    public static class Test
    {
        public static async Task Self()
        {
            var resources = new[] {
                new ExclusiveResource(),
                new ExclusiveResource(),
            };

            var task1 = ExclusiveResource.Enqueue(
                resources,
                default,
                async _ =>
                {
                    Console.WriteLine("111");
                    var task2 = resources[1].Enqueue(default, async _ =>
                    {
                        await Task.Delay(1);
                        Console.WriteLine("222");
                    });
                    await task2;
                    Console.WriteLine("333");
                });
            await Task.Delay(1000);
            task1.AsTask().ContinueWith(t =>
            {
                if (t.Exception != null)
                    Console.Error.WriteLine(t.Exception);
            });
            await Task.Delay(1000);
            //await task1;
        }

        public static async Task Cross()
        {
            var resources = new[] {
                new ExclusiveResource(),
                new ExclusiveResource(),
            };

            for (var i = 0; i < 1; ++i)
            {
                var a = i % 2;
                var b = 1 - (i % 2);
                var tcsList = new[] {
                    new TaskCompletionSource<int>(),
                    new TaskCompletionSource<int>(),
                };
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    resources[a].Enqueue(default, async _ =>
                    {
                        Console.WriteLine("a");
                        await Task.Delay(1000);
                        await resources[b].Enqueue(default, async _ =>
                        {
                            Console.WriteLine("and b");
                            await Task.Delay(1000);
                        });
                    }).AsTask().Wait();
                    tcsList[a].SetResult(0);
                }, null);

                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    resources[b].Enqueue(default, async _ =>
                    {
                        Console.WriteLine("b");
                        await Task.Delay(1000);
                        await resources[a].Enqueue(default, async _ =>
                        {
                            Console.WriteLine("and a");
                            await Task.Delay(1000);
                        });
                    }).AsTask().Wait();
                    tcsList[b].SetResult(0);
                }, null);

                await Task.WhenAll(tcsList.Select(e => e.Task));
            }
        }
    }


    public sealed class ExclusiveResource
    {
        public static volatile bool DebugMode
#if DEBUG
            = true;
#else
            = false;
#endif

        private TaskCompletionSource<object?>? lastTcs;

        public DebugInformation? DebugInfo { get; }
        internal DeadlockDetector.AccessorManager? AccessorManager => this.DebugInfo?.AccessorManager;

        public ExclusiveResource(
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            this.DebugInfo = DebugMode == false
                ? null
                : new()
                {
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    AccessorManager = new(this),
                };
        }

        public override string? ToString()
            => this.DebugInfo?.ToString() ?? base.ToString();

        public ExclusiveResourceTask Enqueue(
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask> taskFactory)
        {
            return Enqueue(new[] { this }, cancellationToken, taskFactory);
        }

        public static ExclusiveResourceTask Enqueue(
            IEnumerable<ExclusiveResource> resources,
            CancellationToken cancellationToken,
            Func<CancellationToken, ValueTask> taskFactory)
        {
            if (taskFactory == null)
                throw new ArgumentException(nameof(taskFactory));

            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            var task = new ExclusiveResourceTask(
                new(),
                resources
                    .Where(e => e != null)
                    .Distinct()
                    .ToList(),
                DeadlockDetector.Accessor.GetCurrent());

            DeadlockDetector.OnCreated(task);

            var prevTask = Task.WhenAll(task.resourceList
                .Select(resource => Interlocked.Exchange(ref resource.lastTcs, task.tcs)?.Task)
                .Where(task => task != null)
                .Cast<Task>());

            if (prevTask.IsCompleted == false)
                prevTask.ContinueWith(_ => InvokeAsync(taskFactory, task, cancellationToken));
            else
                InvokeAsync(taskFactory, task, cancellationToken);

            return task;
        }

        private static async void InvokeAsync(
            Func<CancellationToken, ValueTask> taskFactory,
            ExclusiveResourceTask task,
            CancellationToken cancellationToken)
        {
            Exception? exception = null;
            try
            {
                DeadlockDetector.OnReady(task);

                if (cancellationToken.IsCancellationRequested == false)
                    await taskFactory.Invoke(cancellationToken);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                DeadlockDetector.OnFinish(task);

                foreach (var resource in task.resourceList)
                    Interlocked.CompareExchange(ref resource.lastTcs, null, task.tcs);

                if (exception == null)
                    task.tcs.SetResult(null);
                else
                    task.tcs.SetException(exception);
            }
        }

        public sealed class DebugInformation
        {
            private static int LastId;
            public int Id { get; } = Interlocked.Increment(ref LastId);
            public string FilePath { get; init; } = "";
            public int LineNumber { get; init; }
            internal DeadlockDetector.AccessorManager? AccessorManager { get; init; }
            public override string ToString() => $"{Path.GetFileName(this.FilePath)}:{this.LineNumber}({this.Id})";
        }

        internal static class DeadlockDetector
        {
            internal sealed class Accessor
            {
                private static readonly AsyncLocal<Accessor> AsyncLocal = new();
                private static int lastId;
                public static Accessor? GetCurrent()
                {
                    if (DebugMode == false)
                        return null;

                    if (AsyncLocal.Value == null)
                    {
                        lock (AsyncLocal)
                        {
                            if (AsyncLocal.Value == null)
                                AsyncLocal.Value = new(++lastId);
                        }
                    }

                    return AsyncLocal.Value;
                }

                public readonly int Id;
                public readonly ConcurrentDictionary<int, ExclusiveResourceTask> TaskMap = new();
                private Accessor(int id) => this.Id = id;
                public override string ToString() => this.Id.ToString();
            }

            internal sealed class AccessorManager
            {
                public readonly ExclusiveResource Resource;
                public ExclusiveResourceTaskRef? AssignedTo;
                public AccessorManager(ExclusiveResource resource) => this.Resource = resource;
                public override string? ToString() => this.Resource.ToString();
                internal sealed class ExclusiveResourceTaskRef
                {
                    public ExclusiveResourceTask Task { get; init; }
                }
            }

            public static void OnCreated(ExclusiveResourceTask task)
            {
                var accessor = task.accessor;
                if (accessor == null)
                    return;

                Debug.Assert(DebugMode);

                var added = accessor.TaskMap.TryAdd(task.Id, task);
                Debug.Assert(added);
            }

            public static void OnReady(ExclusiveResourceTask task)
            {
                var accessor = task.accessor;
                if (accessor == null)
                    return;

                Debug.Assert(DebugMode);

                foreach (var resource in task.ResourceList)
                {
                    var am = resource.AccessorManager;
                    Debug.Assert(am != null);

                    var exchanged = Interlocked.CompareExchange(
                        ref am.AssignedTo,
                        new() { Task = task },
                        null);
                    Debug.Assert(exchanged == null);
                }
            }

            public static void OnFinish(ExclusiveResourceTask task)
            {
                var accessor = task.accessor;
                if (accessor == null)
                    return;

                Debug.Assert(DebugMode);

                var removed = accessor.TaskMap.TryRemove(new(task.Id, task));
                Debug.Assert(removed);

                foreach (var resource in task.ResourceList)
                {
                    var am = resource.AccessorManager;
                    Debug.Assert(am != null);

                    var oldRef = am.AssignedTo;
                    if (oldRef == null || oldRef.Task != task)
                    {
                        Debug.Fail($"task mismatch. task:{task}, assigned:{oldRef}");
                        continue;
                    }

                    var exchanged = Interlocked.CompareExchange(ref am.AssignedTo, null, oldRef);
                    Debug.Assert(exchanged == oldRef);
                }
            }

            public static IReadOnlyList<ExclusiveResource> CheckDeadlock(ExclusiveResourceTask task)
            {
                Debug.Assert(DebugMode);
                Debug.Assert(task.accessor != null);
                return TraversialRAG(task, new())
                    ?? Array.Empty<ExclusiveResource>();

                static IReadOnlyList<ExclusiveResource>? TraversialRAG(
                    ExclusiveResourceTask task,
                    List<ExclusiveResource> footprints)
                {
                    var nextNodes = task.ResourceList
                        .Select(resource =>
                        {
                            var am = resource.AccessorManager;
                            Debug.Assert(am != null);
                            return (
                                Resource: am.Resource,
                                AssignedTo: Volatile.Read(ref am.AssignedTo));
                        })
                        .Where(e => e.AssignedTo != null && e.AssignedTo.Task != task)
                        .ToList();

                    foreach (var node in nextNodes)
                    {
                        if (AddFootprint(footprints, node.Resource) == false)
                            return footprints;
                    }

                    foreach (var node in nextNodes)
                    {
                        Debug.Assert(node.AssignedTo != null);

                        var nextAccessor = node.AssignedTo.Task.accessor;
                        Debug.Assert(nextAccessor != null);

                        foreach (var nextTask in nextAccessor.TaskMap.Values)
                        {
                            var dectected = TraversialRAG(nextTask, new(footprints));
                            if (dectected != null)
                                return dectected;
                        }
                    }

                    return null;
                }

                static bool AddFootprint(List<ExclusiveResource> footprints, ExclusiveResource resource)
                {
                    var detectedCircularRef = footprints.Contains(resource);
                    footprints.Add(resource);
                    return detectedCircularRef == false;
                }
            }
        }
    }

    public readonly struct ExclusiveResourceTask : IEquatable<ExclusiveResourceTask>
    {
        internal readonly TaskCompletionSource<object?> tcs;
        internal readonly IReadOnlyList<ExclusiveResource> resourceList;
        internal readonly ExclusiveResource.DeadlockDetector.Accessor? accessor;

        internal ExclusiveResourceTask(
            TaskCompletionSource<object?> tcs,
            IReadOnlyList<ExclusiveResource> resourceList,
            ExclusiveResource.DeadlockDetector.Accessor? accessor)
        {
            this.tcs = tcs;
            this.resourceList = resourceList;
            this.accessor = accessor;
        }

        public int Id => this.tcs?.Task.Id ?? Task.CompletedTask.Id;
        public IReadOnlyList<ExclusiveResource> ResourceList => this.resourceList ?? Array.Empty<ExclusiveResource>();
        public AggregateException? Exception => this.tcs?.Task.Exception;
        public bool IsFaulted => this.tcs?.Task.IsFaulted ?? false;
        public bool IsCompleted => this.tcs?.Task.IsCompleted ?? true;
        public bool IsCanceled => this.tcs?.Task.IsCanceled ?? false;
        public bool IsCompletedSuccessfully => this.tcs?.Task.IsCompletedSuccessfully ?? true;
        public Awaiter GetAwaiter() => new(this);
        public async Task<object?> AsTask() => await this;

        public void Forget()
        {
        }

        public bool Equals(ExclusiveResourceTask other)
            => ReferenceEquals(this.tcs?.Task, other.tcs?.Task);

        public override bool Equals(object? obj)
            => (obj is ExclusiveResourceTask other && Equals(other)) || ReferenceEquals(this.tcs?.Task, obj);

        public override int GetHashCode()
            => this.tcs?.Task.GetHashCode() ?? 0;

        public static bool operator ==(ExclusiveResourceTask left, ExclusiveResourceTask right)
            => left.Equals(right);

        public static bool operator !=(ExclusiveResourceTask left, ExclusiveResourceTask right)
            => left.Equals(right) == false;

        public readonly struct Awaiter : INotifyCompletion, ICriticalNotifyCompletion
        {
            private readonly TaskCompletionSource<object?> tcs;

            internal Awaiter(ExclusiveResourceTask task)
            {
                this.tcs = task.tcs;

                if (task.resourceList != null && task.tcs != null && ExclusiveResource.DebugMode)
                {
                    var deadlockList = ExclusiveResource.DeadlockDetector.CheckDeadlock(task);
                    if (deadlockList.Count != 0)
                        throw new DeadlockDetectedException(deadlockList);
                }
            }

            public bool IsCompleted => this.tcs?.Task.IsCompleted ?? true;
            public object? GetResult() => this.tcs?.Task.GetAwaiter().GetResult();
            public void OnCompleted(Action continuation) => (this.tcs?.Task ?? Task.CompletedTask).GetAwaiter().OnCompleted(continuation);
            public void UnsafeOnCompleted(Action continuation) => (this.tcs?.Task ?? Task.CompletedTask).GetAwaiter().UnsafeOnCompleted(continuation);
        }

        public sealed class DeadlockDetectedException : Exception
        {
            public DeadlockDetectedException(IReadOnlyList<ExclusiveResource> deadlockResourceList)
                : base($"deadlock detected. {string.Join(" -> ", deadlockResourceList)}")
            {
                this.ResourceList = deadlockResourceList;
            }

            public IReadOnlyList<ExclusiveResource> ResourceList { get; }
        }
    }
}

