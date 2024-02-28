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
            resource.Access.Then(async _ =>
            {
                await Task.Yield();
                Console.WriteLine("do somthing");
            });
        }

        [Fact]
        public static async Task AwaitableAccess()
        {
            var resource = new ExclusiveResource();
            var result = await resource.Access.ThenAsync(async _ =>
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
            resource.Access.Then(async _ =>
            {
                await Task.Delay(500);
                tcs.SetResult(null);
            });
            Assert.False(tcs.Task.IsCompleted);

            var footprints = await resource.Access.ThenAsync(async _ =>
            {
                var footprints = new List<int>();

                footprints.Add(1);

                resource.Access.Then(_ =>
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
            await resource.Access.ThenAsync(_ => default);

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
                resource.Access.Then(_ =>
                {
                    actualList.Add(number);
                    return default;
                });
            }

            // wait for flush ...
            await resource.Access.ThenAsync(_ => default);

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
                        resource.Access.Then(_ =>
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
            await resource.Access.ThenAsync(_ => default);

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
                .Then(async _ =>
                {
                    await Task.Yield();
                    Console.WriteLine("do somthing");
                });
        }
    }
}
