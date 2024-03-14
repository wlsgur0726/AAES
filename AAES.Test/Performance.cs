using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace AAES.Test
{
    [CollectionDefinition(nameof(Performance))]
    public class Performance
    {
        private readonly ITestOutputHelper output;

        public Performance(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData(1000)]
        [InlineData(1_000_000)]
        public async Task QueueOverhead(int testCount)
        {
            var resource = new AAESResource();
            var samples = new List<TimeSpan>(testCount);

            // 동기식 호출 방지 위해 재귀적으로 실행
            Test(testCount);
            void Test(int remainCount)
            {
                var startedAt = DateTime.Now;
                resource.Access.Then(() =>
                {
                    samples.Add(DateTime.Now - startedAt);
                    if (--remainCount > 0)
                        Test(remainCount);
                    return default;
                });
            }

            while (samples.Count < testCount)
                await Task.Delay(100);

            samples.Sort();
            this.output.WriteLine(new StringBuilder()
                .AppendLine($"min : {PrintMicroseconds(samples.First())}")
                .AppendLine($"max : {PrintMicroseconds(samples.Last())}")
                .AppendLine($"p50 : {PrintMicroseconds(samples[(int)(samples.Count * 0.5)])}")
                .AppendLine($"p99 : {PrintMicroseconds(samples[(int)(samples.Count * 0.99)])}")
                .AppendLine($"avg : {PrintMicroseconds(TimeSpan.FromTicks((long)samples.Average(e => (decimal)e.Ticks)))}")
                .ToString());

            static string PrintMicroseconds(TimeSpan timeSpan)
                => (timeSpan.TotalMilliseconds * 1000).ToString("#,##0.000").PadLeft(10) + " microseconds";
        }

        [Theory]
        //          0       1       2       3       4       5       6       7
        [InlineData("A",    0.1,    true,   10.0,   10,     1,      1,      1)]
        [InlineData("B",    0.1,    true,   10.0,   100,    1,      1,      1)]
        [InlineData("C",    0.1,    true,   10.0,   1000,   1,      1,      1)]
        [InlineData("D",    0.1,    true,   10.0,   100,    100,    1,      1)]
        [InlineData("E",    0.1,    true,   10.0,   100,    200,    1,      1)]
        [InlineData("F",    0.1,    true,   10.0,   100,    1000,   1,      1)]
        [InlineData("G",    0.1,    true,   10.0,   100,    1000,   100,    150)]
        [InlineData("H",    0.1,    true,   10.0,   100,    1000,   500,    550)]
        [InlineData("I",    0.1,    true,   10.0,   100,    1000,   900,    950)]
        public async Task Counter(
            /*0*/ string caseName,
            /*1*/ double taskCostMs,
            /*2*/ bool isCpuBoundTask,
            /*3*/ double publishTermMs,
            /*4*/ int publisherCount,
            /*5*/ int resourceCount,
            /*6*/ int multiAccessMin,
            /*7*/ int multiAccessMax)
        {
            if (resourceCount < multiAccessMin)
                throw new ArgumentOutOfRangeException(nameof(multiAccessMin));

            if (resourceCount < multiAccessMax)
                throw new ArgumentOutOfRangeException(nameof(multiAccessMax));

            if (multiAccessMax < multiAccessMin)
                throw new ArgumentOutOfRangeException(nameof(multiAccessMin));

            long count = 0;
            using var cts = new CancellationTokenSource();

            var taskCost = TimeSpan.FromMilliseconds(taskCostMs);
            var taskFactory = isCpuBoundTask
                ? new Func<ValueTask>(() =>
                {
                    for (var endedAt = DateTime.Now + taskCost; DateTime.Now < endedAt;) ;
                    return default;
                })
                : new Func<ValueTask>(() =>
                {
                    return new(Task.Delay(taskCost));
                });

            var resources = new AAESResource[resourceCount];
            for (var i = 0; i < resourceCount; ++i)
                resources[i] = new();

            var shuffledResourcesMap = Enumerable
                .Range(1, Math.Max(100, resourceCount))
                .Select(i =>
                {
                    var random = new Random(i);
                    return resources.OrderBy(e => random.Next()).ToArray();
                })
                .ToArray();

            var publishers = new Task[publisherCount];
            var startedAt = DateTime.Now;
            for (var i = 0; i < publisherCount; ++i)
            {
                publishers[i] = Task.Run(async () =>
                {
                    var random = new Random();
                    var targets = new List<AAESResource>(multiAccessMax);

                    for (var nextPublishAt = DateTime.Now;
                        cts.IsCancellationRequested == false;
                        await DelayUntil(nextPublishAt))
                    {
                        nextPublishAt += TimeSpan.FromMilliseconds(publishTermMs);

                        var shuffledResources = shuffledResourcesMap[random.Next(shuffledResourcesMap.Length)];
                        var index = random.Next(shuffledResources.Length);

                        targets.Clear();
                        var targetCount = random.Next(multiAccessMin, multiAccessMax + 1);
                        for (var i = 0; i < targetCount; ++i)
                        {
                            index = (index + 1) % shuffledResources.Length;
                            targets.Add(shuffledResources[index]);
                        }

                        AAESResource.AccessTo(targets)
                            .WithCancellationToken(cts.Token)
                            .Then(() =>
                            {
                                Interlocked.Increment(ref count);
                                return taskFactory.Invoke();
                            });
                    }

                    static async ValueTask DelayUntil(DateTime dateTime)
                    {
                        var delay = dateTime - DateTime.Now;
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay);
                    }
                });
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
            var elapsed = DateTime.Now - startedAt;

            var countPerSecPerPub = Volatile.Read(ref count) / elapsed.TotalSeconds / publisherCount;
            this.output.WriteLine($"{caseName}: {countPerSecPerPub.ToString("#,##0.000")} count/sec/pub");

            cts.Cancel();
            await Task.WhenAll(publishers);
            await Task.WhenAll(resources.Select(e => e.Access.ThenAsync(() => default).AsTask()));
        }
    }
}
