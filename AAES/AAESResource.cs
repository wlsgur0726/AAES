using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AAES
{
    public sealed class AAESResource
    {
        private static long LastId;
        public long Id { get; } = Interlocked.Increment(ref LastId);
        public DebugInformation? DebugInfo { get; }

        private static readonly Task Locked = new(() => throw new InvalidOperationException());
        private Task? lastTask;

        public AAESResource(
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            if (AAESDebug.RequiredDebugInfo)
            {
                this.DebugInfo = new(new()
                {
                    FilePath = callerFilePath,
                    LineNumber = callerLineNumber,
                });
            }
        }

        public override string ToString() => this.DebugInfo == null
            ? $"{nameof(AAESResource)}({this.Id})"
            : $"{nameof(AAESResource)}:{this.DebugInfo.Caller}({this.Id})";

        public AAESTaskBuilder Access => AccessTo(this);
        public static AAESTaskBuilder AccessTo(params AAESResource[] resources) => new(resources);
        public static AAESTaskBuilder AccessTo(IEnumerable<AAESResource> resources) => new(resources);

        /// <summary>
        /// 모든 resource들의 <see cref="lastTask"/>를 원자적으로 교체한다.
        /// </summary>
        /// <remarks>
        /// 원자적이게 하지 않으면 아래 예시처럼 데드락 발생 가능.
        /// <code>
        /// ex) t1, t2가 동시에 r1, r2를 점유 시도
        ///     event\state         r1.lastTask     r2.lastTask     t1.previousTask     t2.previousTask
        ///     ----------------------------------------------------------------------------------------
        ///     example state       t0              t0              unknown             unknown
        ///     [t1] exchange r1    t1              t0              t0                  unknown
        ///     [t2] exchange r1    t2              t0              t0                  t1
        ///     [t2] exchange r2    t2              t2              t0                  t1, t0
        ///     [t1] exchange r2    t2              t1              t0, t2              t1, t0
        /// </code>
        /// </remarks>
        internal static void ExchangeLastTask(
            IReadOnlyList<AAESResource> resourceList,
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

        internal static void UnlinkLastTask(IReadOnlyList<AAESResource> resourceList, Task finishedTask)
        {
            foreach (var resource in resourceList)
            {
                if (resource.lastTask == finishedTask)
                {
                    var exchanged = Interlocked.CompareExchange(ref resource.lastTask, null, finishedTask);
                    Debug.Assert(exchanged != null);
                }
            }
        }

        public sealed class DebugInformation
        {
            public readonly AAESDebug.Caller Caller;
            internal AAESTask? Holder;

            internal DebugInformation(AAESDebug.Caller caller)
            {
                this.Caller = caller;
            }
        }
    }
}

