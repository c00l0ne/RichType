using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Kazrich.App;
using Kazrich.Core;

internal static class ManagedLifecycleChecks
{
    internal static async Task Run()
    {
        var settings = new Settings { CpuOnly = true, CpuThreads = 2 };
        var processField = typeof(ManagedModel).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            Console.WriteLine("PASS: " + message); count++;
        }
        bool Exited(int processId)
        {
            try { using var child = Process.GetProcessById(processId); return child.HasExited; }
            catch (ArgumentException) { return true; }
        }
        async Task CheckExited(int processId, string message)
        {
            var until = Environment.TickCount64 + 5000;
            while (!Exited(processId) && Environment.TickCount64 < until) await Task.Delay(25);
            Check(Exited(processId), message);
        }
        foreach (var operation in new[] { "cancel", "dispose", "stop" })
        {
            using var managed = new ManagedModel();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Check(managed.IsInstalled(settings.Model, true), operation + ": uses installed CPU weights without downloading");
            var startup = managed.PrepareAndStartAsync(settings, false, new Progress<string>(), cancellation.Token);
            var child = (Process?)processField.GetValue(managed);
            var processId = child?.Id ?? 0;
            try
            {
                Check(!startup.IsCompleted && processId != 0, operation + ": the test reached an actual loading process");
                Check(!managed.IsRunning(settings), operation + ": a loading process is not advertised as a ready model");
                if (operation == "cancel") cancellation.Cancel();
                else if (operation == "dispose") managed.Dispose();
                else managed.Stop();
                Exception? failure = null;
                try { await startup; } catch (Exception error) { failure = error; }
                Check(failure is OperationCanceledException or InvalidOperationException && failure is not NullReferenceException,
                    operation + ": interrupted startup returns a controlled exception: " + failure?.GetType().Name);
                Check(!managed.IsRunning(settings), operation + ": the interrupted instance remains unready");
                await CheckExited(processId, operation + ": no loading process is orphaned");
            }
            finally
            {
                cancellation.Cancel();
                try { await startup; } catch { }
                managed.Dispose();
            }
        }
        using (var managed = new ManagedModel())
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            var startup = managed.PrepareAndStartAsync(settings, false, new Progress<string>(), cancellation.Token);
            using var queuedCancellation = new CancellationTokenSource(20);
            var queued = managed.PrepareAndStartAsync(settings with { CpuThreads = 1 }, false, new Progress<string>(), queuedCancellation.Token);
            var cancelled = false;
            try { await queued; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "a queued startup can be cancelled while another model is loading");
            await startup;
            Check(managed.IsRunning(settings), "cancelling a queued startup preserves the active model and its settings");
            var child = (Process)processField.GetValue(managed)!;
            var originalId = child.Id;
            child.Kill(); await child.WaitForExitAsync(cancellation.Token);
            Check(!managed.IsRunning(settings), "an unexpectedly exited process is immediately unready");
            await managed.PrepareAndStartAsync(settings, false, new Progress<string>(), cancellation.Token);
            Check(managed.IsRunning(settings) && ((Process)processField.GetValue(managed)!).Id != originalId,
                "a failed model process can be restarted with the same settings");
            var restartedId = ((Process)processField.GetValue(managed)!).Id;
            managed.Dispose(); managed.Dispose();
            await CheckExited(restartedId, "repeated disposal closes the restarted process");
        }
        Console.WriteLine($"MANAGED LIFECYCLE: {count} checks passed.");
    }
}
