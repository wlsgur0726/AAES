using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.Design;
using System.Diagnostics;
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

            for (var i = 0; i < 3; ++i)
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
        public static volatile bool UseDebugInformation
#if DEBUG
            = true;
#else
            = false;
#endif

        public static volatile bool UseAccessorInformation
#if DEBUG
            = true;
#else
            = false;
#endif

        private TaskCompletionSource<object?>? lastTcs;

        public DebugInformation? DebugInfo { get; }
        internal AccessorInformation? AccessorInfo { get; }

        public ExclusiveResource(
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            this.DebugInfo = UseDebugInformation
                ? new()
                {
                    FilePath = filePath,
                    LineNumber = lineNumber,
                }
                : null;

            this.AccessorInfo = UseAccessorInformation
                ? new()
                : null;
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

            var tcs = new TaskCompletionSource<object?>();

            var resourceList = resources
                .Where(e => e != null)
                .Distinct()
                .ToList();

            var prevTask = Task.WhenAll(resourceList
                .Select(resource =>
                {
                    resource.AccessorInfo?.Request(tcs.Task.Id);
                    return Interlocked.Exchange(ref resource.lastTcs, tcs)?.Task;
                })
                .Where(task => task != null)
                .Cast<Task>());

            if (prevTask.IsCompleted == false)
                prevTask.ContinueWith(_ => InvokeAsync(taskFactory, tcs, cancellationToken, resourceList));
            else
                InvokeAsync(taskFactory, tcs, cancellationToken, resourceList);

            return new(tcs, resourceList);
        }

        private static async void InvokeAsync(
            Func<CancellationToken, ValueTask> taskFactory,
            TaskCompletionSource<object?> tcs,
            CancellationToken cancellationToken,
            IReadOnlyList<ExclusiveResource> resourceList)
        {
            try
            {
                foreach (var resource in resourceList)
                    resource.AccessorInfo?.Assign(tcs.Task.Id);

                if (cancellationToken.IsCancellationRequested == false)
                    await taskFactory.Invoke(cancellationToken);

                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                foreach (var resource in resourceList)
                {
                    resource.AccessorInfo?.Finish(tcs.Task.Id);
                    Interlocked.CompareExchange(ref resource.lastTcs, null, tcs);
                }
            }
        }

        public sealed class DebugInformation
        {
            private static int LastId;
            public int Id { get; } = Interlocked.Increment(ref LastId);
            public string FilePath { get; init; } = "";
            public int LineNumber { get; init; }
            public override string ToString() => $"{Path.GetFileName(this.FilePath)}:{this.LineNumber}({this.Id})";
        }

        internal sealed class AccessorInformation
        {
            private static readonly AsyncLocal<int> AsyncLocalId = new();
            private static int LastAsyncLocalId;

            private readonly Dictionary<int, TaskState> taskMap = new();

            public static int GetAsyncLocalId()
            {
                if (AsyncLocalId.Value == 0)
                {
                    lock (AsyncLocalId)
                    {
                        if (AsyncLocalId.Value == 0)
                            AsyncLocalId.Value = ++LastAsyncLocalId;
                    }
                }

                return AsyncLocalId.Value;
            }

            public void Request(int taskId)
            {
                var asyncLocalId = GetAsyncLocalId();
                lock (this.taskMap)
                {
                    Debug.Assert(this.taskMap.ContainsKey(taskId) == false);
                    this.taskMap[taskId] = new() { AsyncLocalId = asyncLocalId };
                }
            }

            public void Assign(int taskId)
            {
                lock (this.taskMap)
                {
                    if (this.taskMap.TryGetValue(taskId, out var state) == false)
                    {
                        Debug.Fail($"{nameof(TaskState)} not found. id:{taskId}");
                        return;
                    }

                    state.AllocationState = AllocationState.Assigned;
                }
            }

            public void Finish(int taskId)
            {
                lock (this.taskMap)
                {
                    var removed = this.taskMap.Remove(taskId);
                    Debug.Assert(removed);
                }
            }

            public bool BeginAwait(ExclusiveResourceTask task)
            {
                var asyncLocalId = GetAsyncLocalId();
                lock (this.taskMap)
                {
                    if (this.taskMap.TryGetValue(task.Id, out var state) == false)
                        return task.IsCompleted;

                    var isSelfDeadlock = this.taskMap.Any(kv
                        => kv.Key != task.Id
                        && kv.Value.AllocationState == AllocationState.Assigned
                        && kv.Value.AsyncLocalId == asyncLocalId);
                    if (isSelfDeadlock)
                        return task.IsCompleted;

                    if (state.Awaiters.Contains(asyncLocalId))
                        return false;

                    state.Awaiters.Add(asyncLocalId);
                    return true;
                }
            }

            public void EndAwait(ExclusiveResourceTask task)
            {
                var asyncLocalId = GetAsyncLocalId();
                lock (this.taskMap)
                {
                    if (this.taskMap.TryGetValue(task.Id, out var state))
                        state.Awaiters.Remove(asyncLocalId);
                    else
                        Debug.Assert(task.IsCompleted);
                }
            }

            private sealed class TaskState
            {
                public int AsyncLocalId { get; init; }
                public AllocationState AllocationState { get; set; }
                public List<int> Awaiters { get; } = new();
            }

            private enum AllocationState
            {
                Requested,
                Assigned,
            }
        }
    }

    public readonly struct ExclusiveResourceTask : IEquatable<ExclusiveResourceTask>
    {
        private readonly TaskCompletionSource<object?> tcs;
        private readonly IReadOnlyList<ExclusiveResource> resourceList;

        internal ExclusiveResourceTask(TaskCompletionSource<object?> tcs, IReadOnlyList<ExclusiveResource> resourceList)
        {
            this.tcs = tcs;
            this.resourceList = resourceList;
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

                if (task.resourceList != null && task.tcs != null && ExclusiveResource.UseAccessorInformation)
                {
                    var deadlockList = task.resourceList
                        .Select(resource =>
                        {
                            Debug.Assert(resource.AccessorInfo != null);
                            return (resource, result: resource.AccessorInfo.BeginAwait(task));
                        })
                        .Where(e => e.result == false)
                        .Select(e => e.resource)
                        .ToList();

                    if (deadlockList.Count != 0)
                    {
                        foreach (var resource in task.resourceList.Where(e => deadlockList.Contains(e) == false))
                        {
                            Debug.Assert(resource.AccessorInfo != null);
                            resource.AccessorInfo.EndAwait(task);
                        }

                        throw new DeadlockDetectedException(deadlockList);
                    }

                    foreach (var resource in task.resourceList)
                    {
                        Debug.Assert(resource.AccessorInfo != null);
                        ExclusiveResource.AccessorInformation.GetAsyncLocalId();
                    }

                    task.tcs.Task.ContinueWith(_ =>
                    {
                        foreach (var resource in task.resourceList)
                        {
                            Debug.Assert(resource.AccessorInfo != null);
                            resource.AccessorInfo.EndAwait(task);
                        }
                    });
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
            {
                this.ResourceList = deadlockResourceList;
            }

            public IReadOnlyList<ExclusiveResource> ResourceList { get; }
        }
    }
}

