using System.Runtime.CompilerServices;

//AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
//{
//    Console.Error.WriteLine(e.ExceptionObject is Exception exception
//        ? exception
//        : new RuntimeWrappedException(e.ExceptionObject));
//};

//TaskScheduler.UnobservedTaskException += (sender, e) =>
//{
//    e.SetObserved();
//    Console.Error.WriteLine(e.Exception);
//};

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