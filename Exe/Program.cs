using AAES.Test;
using System;
using System.Threading.Tasks;
using Xunit.Abstractions;

try
{
    Console.WriteLine("start");
    //await (new AdvancedTests(new ConsoleOutputHelper()).IsNotDeadlock());
    //await (new Performance(new ConsoleOutputHelper()).QueueOverhead(1_000_000));
    await (new Performance(new ConsoleOutputHelper()).Counter("H", 0.1, true, 10.0, 100, 1000, 500, 550));
}
finally
{
    Console.WriteLine("done");
    await Task.Delay(1000);
}

class ConsoleOutputHelper : ITestOutputHelper
{
    public void WriteLine(string message)
    {
        Console.WriteLine(message);
    }

    public void WriteLine(string format, params object[] args)
    {
        Console.WriteLine(format, args);
    }
}
