using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kazrich.Core;

internal static class NetworkChecks
{
    internal static async Task Run()
    {
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
            passed++;
        }
        async Task<bool> Fails<T>(Func<Task> action) where T : Exception
        {
            try { await action(); return false; }
            catch (T) { return true; }
        }
        foreach (var backend in new[] { "Ollama", "OpenAI" })
        {
            using var server = new FakeServer();
            var settings = new Settings { Backend = backend, Endpoint = server.Endpoint,
                FixKeyboardLayout = false, FixHyphens = false, TimeoutSeconds = 1 };
            foreach (var status in new[] { 404, 429, 500, 503 })
            {
                using var client = new ModelClient(settings);
                server.StatusCode = status;
                Check(await Fails<HttpRequestException>(() => client.CorrectAsync("првиет", CancellationToken.None)),
                    $"{backend}: HTTP {status} rejects a plausible correction in the error body");
                server.StatusCode = 200;
                Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет",
                    $"{backend}: HTTP {status} does not poison retry/cache/gate");
            }
            Check(server.LastPath == (backend == "Ollama" ? "/api/generate" : "/v1/chat/completions"),
                backend + ": real loopback HTTP uses the expected API path");
            foreach (var raw in new[] { "{", "{}", backend == "Ollama" ? "{\"response\":42}" : "{\"choices\":[]}" })
            {
                using var client = new ModelClient(settings);
                server.RawReply = raw;
                Check(await Fails<JsonException>(() => client.CorrectAsync("првиет", CancellationToken.None)),
                    backend + ": malformed protocol response is rejected: " + raw);
                server.RawReply = null;
                Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет",
                    backend + ": a valid reply recovers after a malformed response");
            }
            using (var client = new ModelClient(settings))
            {
                server.TruncateBody = true;
                Check(await Fails<HttpRequestException>(() => client.CorrectAsync("првиет", CancellationToken.None)),
                    backend + ": a disconnected partial HTTP body is rejected");
                server.TruncateBody = false;
                Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет",
                    backend + ": a fresh request succeeds after a partial HTTP body");
            }
            using (var destination = new FakeServer())
            using (var client = new ModelClient(settings))
            {
                server.StatusCode = 307; server.Redirect = destination.Endpoint + "/elsewhere";
                Check(await Fails<HttpRequestException>(() => client.CorrectAsync("првиет", CancellationToken.None)) && destination.Calls == 0,
                    backend + ": HTTP redirects do not forward typed text");
                server.StatusCode = 200; server.Redirect = null;
            }
            using (var client = new ModelClient(settings))
            {
                server.Delay = 1500;
                Check(await Fails<OperationCanceledException>(() => client.CorrectAsync("првиет", CancellationToken.None)),
                    backend + ": the actual HTTP timeout cancels a stalled response");
                server.Delay = 0;
                Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет",
                    backend + ": timeout releases the request gate for retry");
            }
            using (var client = new ModelClient(settings))
            {
                server.Delay = 500;
                using var cancellation = new CancellationTokenSource(100);
                var inFlight = client.CorrectAsync("првиет", cancellation.Token);
                using var queuedCancellation = new CancellationTokenSource(50);
                Check(await Fails<OperationCanceledException>(() => client.CorrectAsync("helllo", queuedCancellation.Token)),
                    backend + ": cancellation releases a request waiting for the word gate");
                Check(await Fails<OperationCanceledException>(async () => await inFlight),
                    backend + ": cancellation rejects an in-flight response");
                server.Delay = 0;
                Check((await client.CorrectAsync("helllo", CancellationToken.None)).Replacement == "hello",
                    backend + ": a cancelled queued word remains correctable");
                await Task.Delay(550);
                Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет",
                    backend + ": a late cancelled response cannot poison the next request");
            }
            foreach (var mode in new[] { "length", "empty" })
            using (var client = new ModelClient(settings))
            {
                var content = mode == "empty" ? "" : "привет";
                var reason = mode == "length" ? "length" : "stop";
                server.RawReply = backend == "Ollama" ? JsonSerializer.Serialize(new { response = content, done_reason = reason }) :
                    JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = reason } } });
                Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == null,
                    backend + ": incomplete generation is not applied: " + mode);
                server.RawReply = null;
                var recovered = await client.CorrectAsync("првиет", CancellationToken.None);
                Check(recovered.Replacement == "привет" && !recovered.Cached,
                    backend + ": a temporary incomplete generation must not persist in cache: " + mode);
            }
        }
        Console.WriteLine($"NETWORK: {passed} checks passed.");
    }
}
