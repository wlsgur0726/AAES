using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AAES
{
    public ref struct AAESTaskBuilder
    {
        private IEnumerable<AAESResource> resources;
        private CancellationToken cancellationToken;
        private TimeSpan? waitingTimeout;

        internal AAESTaskBuilder(IEnumerable<AAESResource> resources)
        {
            this.resources = resources;
            this.cancellationToken = default;
            this.waitingTimeout = null;
        }

        /// <summary>
        /// <inheritdoc cref="Build" path="/param[@name='cancellationToken']"/>
        /// </summary>
        public AAESTaskBuilder WithCancellationToken(CancellationToken token)
        {
            this.cancellationToken = token;
            return this;
        }

        /// <summary>
        /// <inheritdoc cref="Build" path="/param[@name='waitingTimeout']"/>
        /// </summary>
        public AAESTaskBuilder WithWaitingTimeout(TimeSpan timeout)
        {
            this.waitingTimeout = timeout;
            return this;
        }

        /// <inheritdoc cref="Build"/>
        public void Then(
            Func<ValueTask> taskFactory,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            this.ThenAsync(taskFactory, callerFilePath, callerLineNumber)
                .Forget(this.cancellationToken);
        }

        /// <inheritdoc cref="Build"/>
        public AAESTask ThenAsync(
            Func<ValueTask> taskFactory,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory));

            return this.ThenAsync(Wrapper, callerFilePath, callerLineNumber);

            async ValueTask<object?> Wrapper()
            {
                await taskFactory.Invoke().ConfigureAwait(false);
                return null;
            }
        }

        /// <inheritdoc cref="Build"/>
        public AAESTask<TResult?> ThenAsync<TResult>(
            Func<ValueTask<TResult?>> taskFactory,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            return Build(
                resources: this.resources,
                waitingTimeout: this.waitingTimeout,
                cancellationToken: this.cancellationToken,
                taskFactory: taskFactory,
                callerFilePath: callerFilePath,
                callerLineNumber: callerLineNumber);
        }

        /// <summary>
        /// 지정한 <see cref="AAESResource"/>들의 점유를 기다리고, 점유 완료한 상태에서 수행할 task를 생성합니다.
        /// </summary>
        /// 
        /// <typeparam name="TResult"><paramref name="taskFactory"/>의 결과</typeparam>
        /// 
        /// <param name="resources">점유 대상들</param>
        /// 
        /// <param name="cancellationToken">
        /// 대기 중 취소를 위한 <see cref="CancellationToken"/>을 지정.
        /// 대기 중에 취소가 발생하면 <see cref="Exceptions.WaitingCanceledException"/>이 발생하고,
        /// <paramref name="taskFactory"/>는 실행되지 않습니다.
        /// </param>
        /// 
        /// <param name="waitingTimeout">
        /// 지정된 시간동안 대기가 끝나지 않으면
        /// <see cref="Exceptions.WaitingTimeoutException"/>이 발생하고,
        /// <paramref name="taskFactory"/>는 실행되지 않습니다.
        /// </param>
        /// 
        /// <param name="taskFactory">점유 완료 후 수행할 task</param>
        /// 
        /// <returns><paramref name="taskFactory"/>의 결과를 await 가능한 객체</returns>
        /// 
        /// <exception cref="ArgumentNullException">
        /// <paramref name="resources"/> or <paramref name="taskFactory"/> is null
        /// </exception>
        /// 
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="waitingTimeout"/> 값이 <see cref="int.MaxValue"/>밀리초보다 큰 경우.
        /// <inheritdoc cref="Task.Delay(TimeSpan)" path="/exception"/>
        /// </exception>
        public static AAESTask<TResult?> Build<TResult>(
            IEnumerable<AAESResource> resources,
            TimeSpan? waitingTimeout,
            CancellationToken cancellationToken,
            Func<ValueTask<TResult?>> taskFactory,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            if (taskFactory == null)
                throw new ArgumentNullException(nameof(taskFactory));

            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            var waitingCancellationOptions = WaitingCancellationOptions.CreateIfNeed(
                waitingTimeout,
                cancellationToken);

            var resourceList = resources
                .Where(e => e != null)
                .Distinct()
                /// for <see cref="AAESResource.Locked"/>
                .OrderBy(e => e.Id)
                .ToList();

            var tcs = new TaskCompletionSource<TResult?>();
            AAESResource.ExchangeLastTask(resourceList, tcs.Task, out var previousTask);

            var invoker = AAESDebug.Invoker.Current;
            var task = new AAESTask<TResult?>(
                tcs,
                resourceList,
                waitingCancellationOptions,
                previousTask,
                new()
                {
                    FilePath = callerFilePath,
                    LineNumber = callerLineNumber,
                },
                invoker);

            if (AAESDebug.CaptureChildTask)
            {
                Debug.Assert(invoker != null);
                var added = invoker.ChildTasks.TryAdd(task.Id, task);
                Debug.Assert(added);
            }

            if (previousTask.IsCompleted)
            {
                Invoke(taskFactory, task);
            }
            else
            {
                var taskContOpts = TaskContinuationOptions.DenyChildAttach;
                #if !DEBUG // 이 옵션을 사용하면 call-stack이 너무 깊어져 디버깅이 불편해지는 경우가 생김
                taskContOpts |= TaskContinuationOptions.ExecuteSynchronously;
                #endif
                previousTask.ContinueWith(_ => Invoke(taskFactory, task), taskContOpts);
            }

            return task;
        }

        private static async void Invoke<TResult>(
            Func<ValueTask<TResult?>> taskFactory,
            AAESTask<TResult?> task)
        {
            TResult? result = default;
            Exception? exception = null;
            try
            {
                AAESDebug.DeadlockDetector.BeginTask(task);

                task.WaitingCancellationOptions?.ThrowIfCanceled();
                result = await taskFactory.Invoke().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                AAESDebug.DeadlockDetector.EndTask(task);

                AAESResource.UnlinkLastTask(task.ResourceList, task.InternalTask);

                if (exception == null)
                    task.Tcs.TrySetResult(result);
                else
                    task.Tcs.TrySetException(exception);
            }
        }

    }
}
