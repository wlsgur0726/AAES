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
            resource.Access(async () =>
            {
                await Task.Yield();
                Console.WriteLine("do somthing");
            });
        }

        [Fact]
        public static async Task AwaitableAccess()
        {
            var resource = new ExclusiveResource();
            var result = await resource.AwaitableAccess(async () =>
            {
                await Task.Delay(100);
                return 123;
            });
            Assert.Equal(123, result);
        }

        [Fact]
        public static async Task AsynchronousAccess()
        {
            var resource = new ExclusiveResource();

            var tcs = new TaskCompletionSource<object?>();
            resource.Access(async () =>
            {
                await Task.Delay(500);
                tcs.SetResult(null);
            });
            Assert.False(tcs.Task.IsCompleted);

            var footprints = await resource.AwaitableAccess(async () =>
            {
                var footprints = new List<int>();

                footprints.Add(1);

                resource.Access(() =>
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
            await resource.AwaitableAccess(() => default);

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
                resource.Access(() =>
                {
                    actualList.Add(number);
                    return default;
                });
            }

            // wait for flush ...
            await resource.AwaitableAccess(() => default);

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
                threads.Add(new Thread(() =>
                {
                    for (var i = 0; i < accessCount; i++)
                    {
                        resource.Access(() =>
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
            await resource.AwaitableAccess(() => default);

            Assert.Equal(threadCount * accessCount, number);
        }

        [Fact]
        public static void MultipleResourceAccess()
        {
            var resource1 = new ExclusiveResource();
            var resource2 = new ExclusiveResource();
            var resource3 = new ExclusiveResource();

            ExclusiveResource.Access(
                new[] { resource1, resource2, resource3 },
                async () =>
                {
                    await Task.Yield();
                    Console.WriteLine("do somthing");
                });
        }
    }
}
