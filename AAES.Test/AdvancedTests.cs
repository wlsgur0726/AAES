using AAES.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace AAES.Test
{
    public class AdvancedTests
    {
        private readonly ITestOutputHelper output;
        public AdvancedTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// 셀프 데드락 사례와 <see cref="AAESDebug.DeadlockDetector"/>의 감지 기능 테스트
        /// </summary>
        [Fact]
        public async Task SelfDeadlock()
        {
            var resource = new AAESResource();

            var task = resource.Access.ThenAsync(async () =>
            {
                this.output.WriteLine("a");

                /// 동일한 resource를 점유하는 innerTask 생성
                var innerTask = resource.Access.ThenAsync(async () =>
                {
                    await Task.Delay(1);
                    this.output.WriteLine("b");
                });

                /// task가 innerTask를 await하면 데드락. innerTask는 task가 종료되기 전까지 실행되지 않는다.
                /// <see cref="AAESDebug.Level"/>이 활성화 되어있으면 즉시 감지되어야 한다.
                /// <see cref="AAESDebug.Level"/> 비활성에 타임아웃 처리도 하지 않는다면 관련 task들은 영원히 종료되지 않는다.
                await Assert.ThrowsAsync(
                    AAESDebug.EnableDeadlockDetector
                        ? typeof(DeadlockDetectedException)
                        : typeof(TimeoutException),
                    () => innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));

                this.output.WriteLine("c");
            });

            await task.AsTask().WaitAsync(TimeSpan.FromMilliseconds(1000));

            // wait for flush ...
            await resource.Access.ThenAsync(() => default);
        }

        /// <summary>
        /// 교차 데드락 사례와 <see cref="AAESDebug.DeadlockDetector"/>의 감지 기능 테스트
        /// </summary>
        [Fact]
        public async Task CrossDeadlock()
        {
            var r1 = new AAESResource();
            var r2 = new AAESResource();

            var t1 = r1.Access.ThenAsync(async () =>
            {
                this.output.WriteLine("[t1] held r1");

                /// t1의 r2점유 시도 전에 t2가 r2를 점유한 상태를 연출하기 위한 딜레이
                await Task.Delay(100);

                var innerTask = r2.Access.ThenAsync(() =>
                {
                    /// 데드락 여부와 무관하게 예약은 무조건 이뤄지므로 데드락이 해결되면 이 함수도 실행된다.
                    /// 본 예제는 t2가 exception으로 인해 종료되고, 그로 인해 r2가 relelase된 후 실행된다.
                    this.output.WriteLine("[t1] held r2");
                    return default;
                });
                /// t2보다 먼저 await를 시도하므로 순환을 감지하지 못한다.
                /// (t2가 미래에 무엇을 await할지 예측 불가)
                await innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));

                this.output.WriteLine("[t1] release r1");
            });

            var t2 = r2.Access.ThenAsync(async () =>
            {
                this.output.WriteLine("[t2] held r2");

                /// t1의 innerTask보다 늦게 시도하기 위한 딜레이.
                await Task.Delay(200);

                var innerTask = r1.Access.ThenAsync(() =>
                {
                    /// 데드락 여부와 무관하게 예약은 무조건 이뤄지므로 데드락이 해결되면 이 함수도 실행된다.
                    /// t2의 exception으로 r2 release, t1의 innerTask 실행 완료 후 r1 release 후 실행된다.
                    this.output.WriteLine("[t2] held r1");
                    return default;
                });
                /// r1을 점유한 t1이 r2를 await 중인 상태에서 시도하므로 데드락.
                /// <see cref="AAESDebug.Level"/>이 활성화 되어있으면 즉시 감지되어야 한다.
                /// <see cref="AAESDebug.Level"/> 비활성에 타임아웃 처리도 하지 않는다면 관련 task들은 영원히 종료되지 않는다.
                await Assert.ThrowsAsync(
                    AAESDebug.EnableDeadlockDetector
                        ? typeof(DeadlockDetectedException)
                        : typeof(TimeoutException),
                    () => innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(100)));

                this.output.WriteLine("[t2] release r2");
            });

            await Task.WhenAll(t1.AsTask(), t2.AsTask()).WaitAsync(TimeSpan.FromMilliseconds(1000));

            // wait for flush ...
            await AAESResource.AccessTo(r1, r2).ThenAsync(() => default);
        }

        /// <summary>
        /// <see cref="AAESDebug.DeadlockDetector"/>의 오진 여부를 확인하기 위한 테스트
        /// </summary>
        [Fact]
        public async Task IsNotDeadlock()
        {
            var resources = new[] {
                new AAESResource(),
                new AAESResource(),
            };

            var t01 = resources[0].Access.ThenAsync(async () =>
            {
                await Task.Delay(100);
                this.output.WriteLine("t01");
            });

            var t02 = resources[1].Access.ThenAsync(async () =>
            {
                await Task.Delay(100);
                this.output.WriteLine("t02");
            });

            var t11 = resources[0].Access.ThenAsync(async () =>
            {
                await t02;
                await Task.Delay(100);
                this.output.WriteLine("t11");
            });

            var t12 = resources[1].Access.ThenAsync(async () =>
            {
                await t01;
                await Task.Delay(100);
                this.output.WriteLine("t12");
            });

            var tasks = new[] { t01, t02, t11, t12 };

            await Task.WhenAll(
                Task.WhenAll(tasks.Select(t => t.AsTask())),
                Task.Run(async () =>
                {
                    await Task.Delay(150);
                    await Task.WhenAll(tasks.Select(t => t.AsTask()));
                }))
                .WaitAsync(TimeSpan.FromMilliseconds(1000));
        }

        /// <summary>
        /// <see cref="AAESDebug.EnsureHeldByCurrentInvoker"/> 기능 테스트
        /// </summary>
        [Fact]
        public async Task EnsureHeldByCurrentInvoker()
        {
            if (AAESDebug.Disabled)
                return;

            var r1 = new AAESResource();
            var r2 = new AAESResource();

            /// 모두 점유중이 아니므로 exception 발생
            Assert.Throws<NotHeldResourceException>(() => r1.EnsureHeldByCurrentInvoker());
            Assert.Throws<NotHeldResourceException>(() => r2.EnsureHeldByCurrentInvoker());

            await r1.Access.ThenAsync(async () =>
            {
                /// r1 점유중이므로 exception 발생하지 않음
                r1.EnsureHeldByCurrentInvoker();
                /// r2 점유중이 아니므로 exception 발생
                Assert.Throws<NotHeldResourceException>(() => r2.EnsureHeldByCurrentInvoker());

                var innerTask = r2.Access.ThenAsync(async () =>
                {
                    /// r2 점유중이므로 exception 발생하지 않음
                    r2.EnsureHeldByCurrentInvoker();

                    /// 이 task의 awaiter가 점유한 resource인지 확인하는 동작은 <see cref="AAESDebug.Level"/>에 따라 다름.
                    if (AAESDebug.StrictHeldChecking)
                    {
                        /// awaiter가 언제 종료될지 예상 불가능하므로 보수적인 판정.
                        Assert.Throws<NotHeldResourceException>(() => r1.EnsureHeldByCurrentInvoker());
                    }
                    else
                    {
                        /// 이 task가 종료되기 전에 awaiter가 먼저 종료되는 일은 없을 것이란 가정으로 너그럽게 판정.
                        r1.EnsureHeldByCurrentInvoker();
                    }

                    /// 이 task의 awaiter가 먼저 종료되는 상황을 연출
                    await Task.Delay(TimeSpan.FromMilliseconds(100));

                    Assert.Throws<NotHeldResourceException>(() => r1.EnsureHeldByCurrentInvoker());
                    r2.EnsureHeldByCurrentInvoker();
                });

                /// innerTask의 완료를 기다리지 못하고 종료
                await Assert.ThrowsAsync<TimeoutException>(() => innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(50)));

                r1.EnsureHeldByCurrentInvoker();
                Assert.Throws<NotHeldResourceException>(() => r2.EnsureHeldByCurrentInvoker());
            });

            Assert.Throws<NotHeldResourceException>(() => r1.EnsureHeldByCurrentInvoker());
            Assert.Throws<NotHeldResourceException>(() => r2.EnsureHeldByCurrentInvoker());

            // wait for flush ...
            await r1.Access.ThenAsync(() => default);
            await r2.Access.ThenAsync(() => default);
        }

        [Theory]
        [InlineData(1, 4, 4, 10000)]
        [InlineData(2, 10, 20, 10000)]
        [InlineData(3, 1000, 100, 100)]
        public async Task MultipleResourceAccess(int randomSeed, int resourceCount, int publisherCount, int publishCount)
        {
            var resources = Enumerable.Range(0, resourceCount)
                .Select(_ => new AAESResource())
                .ToList();

            var expectedCounter = new long[resourceCount];
            {
                var random = new Random(randomSeed);
                for (var i = 0; i < publishCount; ++i)
                {
                    var indexList = SelectResourceIndex(random);
                    foreach (var index in indexList)
                        expectedCounter[index] += publisherCount;
                }
            }

            var actualCounter = new long[resourceCount];
            var publisherList = Enumerable.Range(0, publisherCount)
                .Select(_ => new Thread(() =>
                {
                    var random = new Random(randomSeed);
                    for (var i = 0; i < publishCount; ++i)
                    {
                        var indexList = SelectResourceIndex(random);
                        AAESResource.AccessTo(indexList.Select(i => resources[i])).Then(() =>
                        {
                            foreach (var index in indexList)
                                actualCounter[index] += 1;
                            return default;
                        });
                    }
                }))
                .ToList();

            publisherList.ForEach(e => e.Start());
            publisherList.ForEach(e => e.Join());

            // wait for flush ...
            await AAESResource.AccessTo(resources).ThenAsync(() => default);

            Assert.Equal(expectedCounter, actualCounter);

            List<int> SelectResourceIndex(Random random)
            {
                var count = 1 + random.Next(resourceCount);
                var indexList = Enumerable.Range(0, resourceCount).ToList();
                var selectedList = new List<int>(count);
                for (var i = 0; i < count; ++i)
                {
                    var index = random.Next(indexList.Count);
                    indexList.RemoveAt(index);
                    selectedList.Add(index);
                }
                return selectedList;
            }
        }

        [Fact]
        public async Task UnhandledExceptionHandelr()
        {
            var expectedMessage = $"test {nameof(UnhandledExceptionHandelr)} for Forget method. {DateTime.UtcNow.Ticks}";
            var tcs = new TaskCompletionSource<string?>();
            var handler = new Func<Exception, bool>(e =>
            {
                var exception = e as InvalidOperationException;
                tcs.TrySetResult(exception?.Message);
                return true;
            });

            try
            {
                AAESTask.UnhandledExceptionHandler += handler;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    new AAESResource().Access.Then(() => throw new InvalidOperationException(expectedMessage));
                }, null);

                var message = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(expectedMessage, message);
            }
            finally
            {
                AAESTask.UnhandledExceptionHandler -= handler;
            }
        }

#pragma warning disable xUnit1013
        //[Fact]
        public static async Task UnhandledExceptionAppDomain()
#pragma warning restore xUnit1013 
        {
            var expectedMessage = $"test {nameof(UnhandledExceptionAppDomain)} for Forget method. {DateTime.UtcNow.Ticks}";
            var tcs = new TaskCompletionSource<string?>();
            var handler = new UnhandledExceptionEventHandler((sender, e) =>
            {
                var exception = e.ExceptionObject as InvalidOperationException;
                tcs.TrySetResult(exception?.Message);
            });

            try
            {
                AppDomain.CurrentDomain.UnhandledException += handler;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    new AAESResource().Access.Then(() => throw new InvalidOperationException(expectedMessage));
                }, null);

                var message = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(expectedMessage, message);
            }
            finally
            {
                AppDomain.CurrentDomain.UnhandledException -= handler;
            }
        }
    }

}
