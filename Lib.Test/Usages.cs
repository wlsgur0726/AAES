using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lib.Test
{
    public static class Usages
    {
        [Fact]
        public static void LikeActorModel()
        {
            var resource = new ExclusiveResource();
            resource.Access.Then(async () =>
            {
                await Task.Yield();
                Console.WriteLine("do somthing");
            });
        }

        [Fact]
        public static async Task AwaitableAccess()
        {
            var resource = new ExclusiveResource();
            var result = await resource.Access.ThenAsync(async () =>
            {
                await Task.Delay(100);
                return nameof(AwaitableAccess);
            });
            Assert.Equal(nameof(AwaitableAccess), result);
        }

        [Fact]
        public static async Task AsynchronousAccess()
        {
            var resource = new ExclusiveResource();

            var tcs = new TaskCompletionSource<object?>();
            resource.Access.Then(async () =>
            {
                await Task.Delay(500);
                tcs.SetResult(null);
            });
            Assert.False(tcs.Task.IsCompleted);

            var footprints = await resource.Access.ThenAsync(async () =>
            {
                var footprints = new List<int>();

                footprints.Add(1);

                resource.Access.Then(() =>
                {
                    footprints.Add(2);
                    return default;
                });

                await Task.Delay(100);

                footprints.Add(3);

                return footprints;
            });

            Assert.True(tcs.Task.IsCompleted);

            // wait for flush ...
            await resource.Access.ThenAsync(() => default);

            Assert.Equal(new[] { 1, 3, 2 }, footprints);
        }

        [Theory]
        [InlineData(10000)]
        public static async Task GuaranteedSequentialAccess(int maxNumber)
        {
            var resource = new ExclusiveResource();

            var actualList = new List<int>();
            var expectedList = Enumerable
                .Range(1, maxNumber)
                .ToList();

            foreach (var number in expectedList)
            {
                resource.Access.Then(() =>
                {
                    actualList.Add(number);
                    return default;
                });
            }

            // wait for flush ...
            await resource.Access.ThenAsync(() => default);

            Assert.Equal(expectedList, actualList);
        }

        [Theory]
        [InlineData(32, 10000)]
        [InlineData(1000, 100)]
        public static async Task GuaranteedExclusiveAccess(int threadCount, int accessCount)
        {
            var number = 0;
            var resource = new ExclusiveResource();

            var threads = new List<Thread>();
            for (var i = 0; i < threadCount; i++)
            {
                threads.Add(new Thread(_ =>
                {
                    for (var i = 0; i < accessCount; i++)
                    {
                        resource.Access.Then(() =>
                        {
                            number++;
                            return default;
                        });
                    }
                }));
            }

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            // wait for flush ...
            await resource.Access.ThenAsync(() => default);

            Assert.Equal(threadCount * accessCount, number);
        }

        [Fact]
        public static void MultipleResourceAccess()
        {
            var resource1 = new ExclusiveResource();
            var resource2 = new ExclusiveResource();
            var resource3 = new ExclusiveResource();

            ExclusiveResource
                .AccessTo(new[] { resource1, resource2, resource3 })
                .Then(async () =>
                {
                    await Task.Yield();
                    Console.WriteLine("do somthing");
                });
        }

        [Fact]
        public static async Task WithWaitingTimeout()
        {
            var resource = new ExclusiveResource();

            var t1 = resource.Access.ThenAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                Console.WriteLine("some long task");
            });

            var t2 = resource.Access
                .WithWaitingTimeout(TimeSpan.FromMilliseconds(1))
                .ThenAsync(() =>
                {
                    Assert.Fail($"not called due to {nameof(ExclusiveResource.AccessTrigger.WithWaitingTimeout)}");
                    return default;
                });
            await Assert.ThrowsAsync<ExclusiveResource.WaitingTimeoutException>(async () => await t2);

            Assert.False(t1.IsCompleted);

            // t2의 실패가 확정적이지만 완료는 t1의 완료까지 미뤄진다.
            // t1이 완료되지 않았는데 t3가 시작되는 버그를 막기 위한 정책.
            Assert.False(t2.IsCompleted);

            var t3 = resource.Access.ThenAsync(() =>
            {
                Assert.True(t1.IsCompletedSuccessfully);
                Assert.True(t2.IsCompleted);
                Assert.True(t2.IsFaulted);
                Assert.True(t2.IsCanceled);
                Assert.True(t2.Exception?.InnerException is ExclusiveResource.WaitingTimeoutException);
                return default;
            });
            await t3;
        }

        [Fact]
        public static async Task WithCancellationToken()
        {
            var resource = new ExclusiveResource();

            var t1 = resource.Access.ThenAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                Console.WriteLine("some long task");
            });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var t2 = resource.Access
                .WithCancellationToken(cts.Token)
                .ThenAsync(() =>
                {
                    Assert.Fail($"not called due to {nameof(ExclusiveResource.AccessTrigger.WithCancellationToken)}");
                    return default;
                });
            await Assert.ThrowsAsync<ExclusiveResource.WaitingCanceledException>(async () => await t2);

            Assert.False(t1.IsCompleted);

            // t2의 실패가 확정적이지만 완료는 t1의 완료까지 미뤄진다.
            // t1이 완료되지 않았는데 t3가 시작되는 버그를 막기 위한 정책.
            Assert.False(t2.IsCompleted);

            var t3 = resource.Access.ThenAsync(() =>
            {
                Assert.True(t1.IsCompletedSuccessfully);
                Assert.True(t2.IsCompleted);
                Assert.True(t2.IsFaulted);
                Assert.True(t2.IsCanceled);
                Assert.True(t2.Exception?.InnerException is ExclusiveResource.WaitingCanceledException);
                return default;
            });
            await t3;
        }
    }
}
