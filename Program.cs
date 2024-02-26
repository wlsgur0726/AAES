Console.WriteLine("start");
try
{
    await Dev.Tests.UnhandledException();
}
finally
{
    Console.WriteLine("done");
    await Task.Delay(1000);
}