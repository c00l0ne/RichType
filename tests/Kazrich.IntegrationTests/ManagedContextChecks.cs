using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kazrich.App;
using Kazrich.Core;

internal static class ManagedContextChecks
{
    internal static async Task Run()
    {
        var settings = new Settings { CpuOnly = true, CpuThreads = 2, FixKeyboardLayout = false, FixHyphens = false, TimeoutSeconds = 90 };
        using var managed = new ManagedModel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var progress = new Progress<string>(Console.WriteLine);
        var processField = typeof(ManagedModel).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int ProcessId() => ((Process)processField.GetValue(managed)!).Id;
        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException(name);
            Console.WriteLine("PASS: " + name);
        }
        async Task<int> ActualContext(Settings current)
        {
            using var http = new HttpClient();
            var props = await http.GetFromJsonAsync<JsonElement>(managed.ConnectionSettings(current).Endpoint + "/props", deadline.Token);
            return props.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
        }
        Check(managed.IsInstalled(settings.Model, cpuOnly: true), "context checks use the already installed CPU model without downloading");
        await managed.PrepareAndStartAsync(settings, false, progress, deadline.Token);
        Check(await ActualContext(settings) == settings.RequiredContextTokens(), "managed server uses the configured default context");
        var baselineProcess = ProcessId();
        var longSettings = settings with { SpellingInstructions = string.Concat(Enumerable.Repeat("Сохраняй исходное слово без изменений. ", 150)) };
        longSettings.Validate();
        Check(!managed.IsRunning(longSettings), "a longer instruction requests a model restart");
        await managed.PrepareAndStartAsync(longSettings, false, progress, deadline.Token);
        Check(ProcessId() != baselineProcess && await ActualContext(longSettings) == longSettings.RequiredContextTokens(),
            "the managed server restarts with enough context for a long instruction");
        using (var usage = new UsageHandler())
        using (var client = new ModelClient(managed.ConnectionSettings(longSettings), usage))
        {
            var watch = Stopwatch.StartNew();
            var result = await client.CorrectAsync("привет", deadline.Token);
            Check(usage.PromptTokens > 1024 && (result.Replacement ?? result.Original) == "привет",
                "the full 5850-character instruction is processed without changing the correct word");
            Console.WriteLine("Processed prompt tokens: " + usage.PromptTokens);
            Console.WriteLine("Long instruction CPU response: " + watch.ElapsedMilliseconds + " ms");
        }
        var englishSettings = settings with { EnglishSpellingInstructions = longSettings.SpellingInstructions };
        var beforeEnglish = ProcessId();
        await managed.PrepareAndStartAsync(englishSettings, false, progress, deadline.Token);
        Check(ProcessId() == beforeEnglish && await ActualContext(englishSettings) == englishSettings.RequiredContextTokens(),
            "a long English instruction uses the same sufficient managed context without an unnecessary restart");
        using (var usage = new UsageHandler())
        using (var client = new ModelClient(managed.ConnectionSettings(englishSettings), usage))
        {
            var result = await client.CorrectAsync("hello", deadline.Token);
            Check(usage.PromptTokens > 1024 && (result.Replacement ?? result.Original) == "hello",
                "the full custom English instruction is processed without truncation");
        }
        var sameSizeSettings = longSettings with { SpellingInstructions = longSettings.SpellingInstructions.Replace("слово", "текст") };
        var expandedProcess = ProcessId();
        await managed.PrepareAndStartAsync(sameSizeSettings, false, progress, deadline.Token);
        Check(ProcessId() == expandedProcess, "editing an instruction within the same context size keeps the server running");
        Check(!managed.IsRunning(settings), "restoring shorter instructions releases the larger context on restart");
        await managed.PrepareAndStartAsync(settings, false, progress, deadline.Token);
        Check(ProcessId() != expandedProcess && await ActualContext(settings) == settings.RequiredContextTokens(), "restoring defaults reduces the managed context again");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await managed.PrepareAndStartAsync(longSettings, false, progress, canceled.Token); throw new InvalidOperationException("Canceled startup completed."); }
        catch (OperationCanceledException) { Check(managed.IsRunning(settings), "an already canceled restart preserves the current server"); }
        managed.Stop();
        Check(!managed.IsRunning(settings) && !managed.IsRunning(longSettings), "stopping the isolated context test leaves no managed test server");
    }

    private sealed class UsageHandler : DelegatingHandler
    {
        internal int PromptTokens;
        internal UsageHandler() : base(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (body.RootElement.TryGetProperty("usage", out var usage)) PromptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            return response;
        }
    }
}
