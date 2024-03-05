using AAES;
using AAES.Exceptions;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Resources;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AAES.Test
{
    public static class AdvancedTests
    {
        [Theory]
        [InlineData(3, 5, 100)]
        [InlineData(100, 100, 1000)]
        public static async Task AccessToMultipleResources(int resourceCount, int accessorCount, int testTimeMs)
        {
            var testTime = TimeSpan.FromMilliseconds(testTimeMs);

            var resources = Enumerable
                .Range(0, resourceCount)
                .Select(_ => new AAESResource())
                .ToList();

            var startedAt = DateTime.UtcNow;
            await Task.WhenAll(Enumerable
                .Range(0, accessorCount)
                .Select(_ => Task.Run(async () =>
                {
                    var random = new Random();
                    var shuffledResources = resources.ToList();
                    AAESTask lastTask;
                    do
                    {
                        for (var i = 0; i < shuffledResources.Count; i++)
                        {
                            var swapIdx = random.Next(shuffledResources.Count);
                            var temp = shuffledResources[i];
                            shuffledResources[i] = shuffledResources[swapIdx];
                            shuffledResources[swapIdx] = temp;
                        }

                        lastTask = AAESResource
                            .AccessTo(shuffledResources)
                            .ThenAsync(() => default);

                    } while (DateTime.UtcNow - startedAt < testTime);

                    await lastTask;
                })))
                .WaitAsync(testTime + TimeSpan.FromSeconds(3));
        }

        [Fact]
        public static async Task SelfDeadlock()
        {
            var resources = new[] {
                new AAESResource(),
                new AAESResource(),
            };

            var task = AAESResource.AccessTo(resources).ThenAsync(async () =>
            {
                Console.WriteLine("111");
                var innerTask = resources[1].Access.ThenAsync(async () =>
                {
                    await Task.Delay(1);
                    Console.WriteLine("222");
                });
                await Assert.ThrowsAsync(
                    AAESDebug.DebugLevel >= 1
                        ? typeof(DeadlockDetectedException)
                        : typeof(TimeoutException),
                    () => innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
                Console.WriteLine("333");
            });

            await task.AsTask().WaitAsync(TimeSpan.FromMilliseconds(1000));
        }

        [Fact]
        public static async Task CrossDeadlock()
        {
            var resources = new[] {
                new AAESResource(),
                new AAESResource(),
            };

            for (var i = 0; i < 5; ++i)
            {
                var a = i % 2;
                var b = 1 - (i % 2);

                var calledInnerTasks = new[] {
                    new TaskCompletionSource<object?>(),
                    new TaskCompletionSource<object?>(),
                };

                var task1 = resources[a].Access.ThenAsync(async () =>
                {
                    Console.WriteLine("a");

                    // task2가 먼저 a를 점유한 상태에서 시도하기 위한 딜레이
                    await Task.Delay(100);

                    // task2보다 먼저 await를 시도하므로 순환을 감지하지 못한다.
                    // (task2가 미래에 무엇을 await할지 예측 불가)
                    var innerTask = resources[b].Access.ThenAsync(() =>
                    {
                        // task2가 exception으로 인해 종료되면 실행된다.
                        Console.WriteLine("and b");
                        calledInnerTasks[b].SetResult(null);
                        return default;
                    });
                    await innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(500));

                    Console.WriteLine("a fin");
                });

                var task2 = resources[b].Access.ThenAsync(async () =>
                {
                    Console.WriteLine("b");

                    // 데드락 감지기능 테스트를 위해 task1의 innerTask보다 늦게 시도하기 위한 딜레이
                    await Task.Delay(200);

                    // task1이 await 중인 상태에서 시도하므로 데드락.
                    // DebugLevel이 활성화 되어있으면 즉시 감지되어야 한다.
                    // DebugLevel 비활성에 타임아웃 처리도 하지 않는다면 관련 task들은 영원히 종료되지 않는다.
                    var innerTask = resources[a].Access.ThenAsync(() =>
                    {
                        // 데드락 여부와 무관하게 예약은 무조건 이뤄지므로 데드락이 해결되면 이 함수도 실행된다.
                        // 본 예제는 task2가 exception으로 인해 종료되고, 그로 인해 task1이 종료된 후 점유 성공한다.
                        Console.WriteLine("and a");
                        calledInnerTasks[a].SetResult(null);
                        return default;
                    });
                    await Assert.ThrowsAsync(
                        AAESDebug.DebugLevel >= 1
                            ? typeof(DeadlockDetectedException)
                            : typeof(TimeoutException),
                        () => innerTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(100)));

                    Console.WriteLine("b fin");
                });

                await Task.WhenAll(task1.AsTask(), task2.AsTask()).WaitAsync(TimeSpan.FromMilliseconds(1000));
                await Task.WhenAll(calledInnerTasks.Select(e => e.Task)).WaitAsync(TimeSpan.FromMilliseconds(100));
                Console.WriteLine("-----");
            }
        }

        [Fact]
        public static async Task Join()
        {
            var resources = new[] {
                new AAESResource(),
                new AAESResource(),
            };

            var t01 = resources[0].Access.ThenAsync(async () =>
            {
                await Task.Delay(100);
                Console.WriteLine("t01");
            });

            var t11 = resources[1].Access.ThenAsync(async () =>
            {
                await Task.Delay(100);
                Console.WriteLine("t11");
            });

            var t02 = resources[0].Access.ThenAsync(async () =>
            {
                //await Task.Delay(100);
                await t11;
                Console.WriteLine("t02");
            });

            var t12 = resources[1].Access.ThenAsync(async () =>
            {
                //await Task.Delay(100);
                await t01;
                Console.WriteLine("t12");
            });

            //await Task.Delay(200);

            await Task.WhenAll(
                t01.AsTask(),
                t02.AsTask(),
                t11.AsTask(),
                t12.AsTask());
        }

        [Fact]
        public static async Task EnsureHeldByCurrentInvoker()
        {
            if (AAESDebug.DebugLevel <= 0)
                return;

            var resources = new[] {
                new AAESResource(),
                new AAESResource(),
            };

            Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[0]));
            Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[1]));
            await resources[0].Access.ThenAsync(async () =>
            {
                AAESDebug.EnsureHeldByCurrentInvoker(resources[0]);
                Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[1]));

                await resources[1].Access.ThenAsync(() =>
                {
                    AAESDebug.EnsureHeldByCurrentInvoker(resources[0]);
                    AAESDebug.EnsureHeldByCurrentInvoker(resources[1]);
                    return default;
                });

                AAESDebug.EnsureHeldByCurrentInvoker(resources[0]);
                Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[1]));

                resources[1].Access.Then(async () =>
                {
                    await Task.Delay(100);
                    Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[0]));
                    AAESDebug.EnsureHeldByCurrentInvoker(resources[1]);
                });
            });

            Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[0]));
            Assert.Throws<NotHeldResourceException>(() => AAESDebug.EnsureHeldByCurrentInvoker(resources[1]));

            // wait for flush ...
            await resources[0].Access.ThenAsync(() => default);
            await resources[1].Access.ThenAsync(() => default);
        }

        [Fact]
        public static async Task UnhandledExceptionHandelr()
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
                AAESDebug.UnhandledExceptionHandler += handler;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    new AAESResource().Access.Then(() => throw new InvalidOperationException(expectedMessage));
                }, null);

                var message = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(expectedMessage, message);
            }
            finally
            {
                AAESDebug.UnhandledExceptionHandler -= handler;
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
