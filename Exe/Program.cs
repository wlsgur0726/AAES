﻿using AAES.Test;
using System;
using System.Threading.Tasks;

try
{
    Console.WriteLine("start");
    await AdvancedTests.EnsureHeldByCurrentInvoker();
}
finally
{
    Console.WriteLine("done");
    await Task.Delay(1000);
}