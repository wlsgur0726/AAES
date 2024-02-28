using Lib.Test;
using System;
using System.Threading.Tasks;

try
{
    Console.WriteLine("start");
    await Tests.UnhandledException();
}
finally
{
    Console.WriteLine("done");
    await Task.Delay(1000);
}