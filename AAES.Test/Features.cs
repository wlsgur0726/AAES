using AAES;
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
    public sealed class Features
    {
        private readonly ITestOutputHelper output;
        public Features(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public async Task LikeActorModel()
        {
            var resource = new AAESResource();
            resource.Access.Then(async () =>
            {
                await Task.Yield();
                this.output.WriteLine("do somthing");
            });

            await Task.Delay(100);
        }

        [Fact]
        public async Task AsynchronousAccess()
        {
            var resource = new AAESResource();

            var tcs1 = new TaskCompletionSource<object?>();
            resource.Access.Then(async () =>
            {
                await Task.Delay(500);
                tcs1.SetResult(null);
            });
            Assert.False(tcs1.Task.IsCompleted);

            var tcs2 = new TaskCompletionSource<object?>();
            var tcs3 = new TaskCompletionSource<object?>();
            var footprints = new List<int>();
            resource.Access.Then(async () =>
            {
                Assert.True(tcs1.Task.IsCompleted);
                footprints.Add(1);

                resource.Access.Then(() =>
                {
                    Assert.True(tcs2.Task.IsCompleted);
                    footprints.Add(2);
                    tcs3.SetResult(null);
                    return default;
                });

                await Task.Delay(100);

                footprints.Add(3);
                tcs2.SetResult(null);
            });

            Assert.False(tcs2.Task.IsCompleted);
            await tcs3.Task;

            Assert.Equal(new[] { 1, 3, 2 }, footprints);
        }

        [Fact]
        public async Task AwaitableAccess()
        {
            var resource = new AAESResource();
            var result = await resource.Access.ThenAsync(async () =>
            {
                await Task.Delay(100);
                return nameof(AwaitableAccess);
            });
            Assert.Equal(nameof(AwaitableAccess), result);
        }

        [Theory]
        [InlineData(32, 10000)]
        [InlineData(1000, 100)]
        public static async Task GuaranteedExclusiveAccess(int threadCount, int accessCount)
        {
            var number = 0;
            var resource = new AAESResource();

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

        [Theory]
        [InlineData(10000)]
        public static async Task GuaranteedSequentialAccess(int maxNumber)
        {
            var resource = new AAESResource();

            var actualList = new List<int>();
            var expectedList = Enumerable.Range(1, maxNumber).ToList();

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

        [Fact]
        public async Task MultipleResourceAccess()
        {
            var r1 = new AAESResource();
            var r2 = new AAESResource();
            var r3 = new AAESResource();

            await AAESResource
                .AccessTo(r1, r2, r3)
                .ThenAsync(() =>
                {
                    this.output.WriteLine("do somthing");
                    return default;
                });
        }

        [Fact]
        public async Task WithWaitingTimeout()
        {
            var resource = new AAESResource();

            var t1 = resource.Access.ThenAsync(async () =>
            {
                this.output.WriteLine("some long task");
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            });

            var t2 = resource.Access
                .WithWaitingTimeout(TimeSpan.FromMilliseconds(1))
                .ThenAsync(() =>
                {
                    Assert.Fail($"not called due to '{nameof(AAESTaskBuilder.WithWaitingTimeout)}'");
                    return default;
                });
            await Assert.ThrowsAsync<WaitingTimeoutException>(async () => await t2);

            Assert.False(t1.IsCompleted);

            /// t2의 실패가 확정적이지만 완료는 t1의 완료까지 미뤄진다.
            /// t1이 완료되지 않았는데 t3가 시작되는 버그를 막기 위한 정책.
            Assert.False(t2.IsCompleted);

            var t3 = resource.Access.ThenAsync(() =>
            {
                Assert.True(t1.IsCompletedSuccessfully);
                Assert.True(t2.IsCompleted);
                Assert.True(t2.IsFaulted);
                Assert.True(t2.IsCanceled);
                Assert.True(t2.Exception?.InnerException is WaitingTimeoutException);
                return default;
            });
            await t3;
        }

        [Fact]
        public async Task WithCancellationToken()
        {
            var resource = new AAESResource();

            var t1 = resource.Access.ThenAsync(async () =>
            {
                this.output.WriteLine("some long task");
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var t2 = resource.Access
                .WithCancellationToken(cts.Token)
                .ThenAsync(() =>
                {
                    Assert.Fail($"not called due to '{nameof(AAESTaskBuilder.WithCancellationToken)}'");
                    return default;
                });
            await Assert.ThrowsAsync<WaitingCanceledException>(async () => await t2);

            Assert.False(t1.IsCompleted);

            /// t2의 실패가 확정적이지만 완료는 t1의 완료까지 미뤄진다.
            /// t1이 완료되지 않았는데 t3가 시작되는 버그를 막기 위한 정책.
            Assert.False(t2.IsCompleted);

            var t3 = resource.Access.ThenAsync(() =>
            {
                Assert.True(t1.IsCompletedSuccessfully);
                Assert.True(t2.IsCompleted);
                Assert.True(t2.IsFaulted);
                Assert.True(t2.IsCanceled);
                Assert.True(t2.Exception?.InnerException is WaitingCanceledException);
                return default;
            });
            await t3;
        }
    }
}
