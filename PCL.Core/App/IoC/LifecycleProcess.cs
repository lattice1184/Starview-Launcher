using System;
using System.Diagnostics;

namespace PCL.Core.App.IoC;

partial class Lifecycle
{
    private static void _RunCurrentExecutable(string? arguments)
    {
        var fileName = Environment.ProcessPath!;
        if (arguments is null) Process.Start(fileName);
        else Process.Start(fileName, arguments);
    }

    private static void _KillCurrentProcess()
    {
#if WINDOWS
        var psi = new ProcessStartInfo
        {
            FileName = "taskkill.exe",
            Arguments = $"/f /t /pid {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
#else
        // Linux/其他平台：直接强制结束当前进程
        Process.GetCurrentProcess().Kill();
#endif
    }
}
