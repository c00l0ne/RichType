using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Kazrich.App;
using Kazrich.Core;

internal static class ControllerLifecycleChecks
{
    internal static async Task Run()
    {
        using var host = new Form();
        _ = host.Handle;
        var before = SpellingThreads();
        var rejected = 0;
        for (var index = 0; index < 32; index++)
        {
            var settings = index % 2 == 0 ? new Settings { Enabled = false }
                : new Settings { Backend = "OpenAI", Endpoint = "invalid", Enabled = false };
            try { using var unexpected = new CorrectionController(host, settings); }
            catch (ArgumentException) { rejected++; }
        }
        await Task.Delay(500); // Let started workers publish their native thread names.
        var deadline = Environment.TickCount64 + 5000;
        while (SpellingThreads() > before && Environment.TickCount64 < deadline) await Task.Delay(50);
        Check(rejected == 32, "invalid controller construction consistently reports an argument error");
        var after = SpellingThreads();
        Check(after == before, $"failed controller construction leaves no spelling worker threads ({before} -> {after})");
        var valid = new Settings { Backend = "OpenAI", Enabled = false };
        var controlledBefore = SpellingThreads();
        using (var controller = new CorrectionController(host, valid))
        {
            deadline = Environment.TickCount64 + 5000;
            while (SpellingThreads() == controlledBefore && Environment.TickCount64 < deadline) await Task.Delay(50);
            Check(SpellingThreads() == controlledBefore + 1, "thread instrumentation detects the active spelling worker");
            rejected = 0;
            try { controller.Configure(valid with { Endpoint = "invalid" }); }
            catch (ArgumentException) { rejected++; }
            Check(rejected == 1 && controller.Settings == valid, "a rejected configuration preserves the previous controller settings");
            var changed = valid with { DelayMs = 350 };
            controller.Configure(changed);
            Check(controller.Settings == changed, "a valid configuration still applies after an earlier rejected change");
        }
        deadline = Environment.TickCount64 + 5000;
        while (SpellingThreads() > controlledBefore && Environment.TickCount64 < deadline) await Task.Delay(50);
        Check(SpellingThreads() == controlledBefore, "controller disposal closes its active spelling worker");
    }

    private static void Check(bool passed, string name)
    {
        Console.WriteLine((passed ? "PASS: " : "FAIL: ") + name);
        if (!passed) Environment.ExitCode = 1;
    }

    private static int SpellingThreads()
    {
        using var process = Process.GetCurrentProcess();
        var count = 0;
        foreach (ProcessThread thread in process.Threads)
        {
            using (thread)
            {
                var handle = OpenThread(0x0800, false, (uint)thread.Id);
                if (handle == 0) continue;
                try
                {
                    var result = GetThreadDescription(handle, out var description);
                    // HRESULT success is not limited to zero.
                    if (result < 0) continue;
                    try
                    {
                        var name = Marshal.PtrToStringUni(description);
                        if (name == "Kazrich spelling") count++;
                    }
                    finally { LocalFree(description); }
                }
                finally { CloseHandle(handle); }
            }
        }
        return count;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(nint thread, out nint description);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
