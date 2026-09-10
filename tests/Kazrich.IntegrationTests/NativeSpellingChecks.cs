using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kazrich.App;
using Kazrich.Core;

internal static class NativeSpellingChecks
{
    internal static async Task Run()
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name); passed++;
        }
        using (var provider = new WindowsSpellingHints())
        {
            foreach (var (correct, typo, expected) in new[] { ("доводим", "доволдим", "доводим"), ("hello", "helloo", "hello") })
            {
                var accepted = await provider.CheckAsync(correct, CancellationToken.None);
                if (accepted == null) { Console.WriteLine("SKIP: Windows spelling language unavailable for " + correct); continue; }
                Check(!accepted.HasError, "installed spelling provider accepts " + correct);
                var evidence = await provider.CheckAsync(typo, CancellationToken.None);
                Check(evidence?.HasError == true && evidence.Suggestions.Contains(expected), "installed spelling provider supplies correction for " + typo);
            }
            foreach (var excluded in new[] { "", "abc_123", "user@example.com", "hello мир", "приvет", new string('a', 33), "🙂",
                "-hello", "hello-", "hello--world", "hello-мир", "bp-pf123" })
                Check(await provider.CheckAsync(excluded, CancellationToken.None) == null, "native provider excludes non-word input: " + excluded);
            foreach (var compound in new[] { "из-за", "из-под", "по-моему", "well-known", "self-contained" })
                Check((await provider.CheckAsync(compound, CancellationToken.None))?.HasError == false,
                    "installed spelling provider reads a complete hyphenated word: " + compound);
            foreach (var correct in new[] { "colour", "color", "centre", "center", "licence", "license", "organise", "organize" })
                Check((await provider.CheckAsync(correct, CancellationToken.None))?.HasError == false,
                    "installed English dialects preserve accepted regional spelling: " + correct);
            foreach (var (typo, corrected) in new[] { ("coluor", "colour"), ("faovurite", "favourite"),
                ("cenrte", "centre"), ("orgnaise", "organise"), ("realsie", "realise") })
                Check((await provider.CheckAsync(typo, CancellationToken.None))?.Suggestions.Contains(corrected, StringComparer.OrdinalIgnoreCase) == true,
                    "installed English dialects combine regional correction candidates: " + typo);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var observed = false;
            try { await provider.CheckAsync("привет", canceled.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed, "native request observes pre-cancellation");
            var requests = Enumerable.Range(0, 256).Select(index => provider.CheckAsync(index % 2 == 0 ? "привет" : "hello", CancellationToken.None)).ToArray();
            var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
            Check(results.All(result => result == null || !result.HasError), "concurrent native requests return valid evidence or bounded abstention");
            Check((await provider.CheckAsync("привет", CancellationToken.None))?.HasError != true, "native worker remains usable after queue pressure");
        }
        for (var cycle = 0; cycle < 12; cycle++)
        {
            var provider = new WindowsSpellingHints();
            using var cancellation = new CancellationTokenSource();
            var requests = new List<Task<SpellingEvidence?>>();
            for (var index = 0; index < 32; index++) requests.Add(provider.CheckAsync("првиет", cancellation.Token));
            cancellation.Cancel(); provider.Dispose(); provider.Dispose();
            try { await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Check(await provider.CheckAsync("привет", CancellationToken.None) == null, "dispose/cancel cycle leaves no live request contract: " + cycle);
        }
        Console.WriteLine($"NATIVE SPELLING: {passed} checks passed.");
    }
}
