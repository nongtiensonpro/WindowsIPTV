using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        Console.WriteLine(""Checking types..."");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach(var asm in assemblies) {
            var type = asm.GetType(""WinRT.Interop.ISystemMediaTransportControlsInterop"", false, true);
            if(type != null) Console.WriteLine($""Found in {asm.FullName}"");
        }
    }
}
