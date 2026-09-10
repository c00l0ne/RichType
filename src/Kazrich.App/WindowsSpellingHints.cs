using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Kazrich.Core;

namespace Kazrich.App;

/// <summary>Read-only access to installed Windows spelling providers, on one background COM thread.</summary>
internal sealed class WindowsSpellingHints : ISpellingHints, IDisposable
{
    private sealed record Request(string Word, CancellationToken Token, TaskCompletionSource<SpellingEvidence?> Completion);
    private readonly BlockingCollection<Request> requests = new(64);
    private readonly object lifecycle = new();
    private bool disposed;

    internal WindowsSpellingHints()
    {
        var worker = new Thread(Run) { IsBackground = true, Name = "Kazrich spelling" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public Task<SpellingEvidence?> CheckAsync(string word, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (word.Length is < 1 or > 32 || !word.All(c => CorrectionRules.IsLetter(c) || c == '-') ||
            word.Any(char.IsAsciiLetter) && !word.All(c => char.IsAsciiLetter(c) || c == '-') ||
            Enumerable.Range(0, word.Length).Any(index => word[index] == '-' && !TextBoundary.IsInternalJoiner(word, index)))
            return Task.FromResult<SpellingEvidence?>(null);
        var completion = new TaskCompletionSource<SpellingEvidence?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle)
        {
            if (disposed || !requests.TryAdd(new Request(word, cancellationToken, completion)))
                return Task.FromResult<SpellingEvidence?>(null);
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void Run()
    {
        ISpellCheckerFactory? factory = null;
        var checkers = new Dictionary<string, ISpellChecker?>();
        var actualLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var englishLanguages = new[] { "en-US" };
        var cache = new Dictionary<string, SpellingEvidence?>(StringComparer.Ordinal);
        var order = new Queue<string>();
        try
        {
            try
            {
                factory = (ISpellCheckerFactory)new SpellCheckerFactory();
                englishLanguages = EnglishLanguages(factory);
            }
            catch (Exception error) when (Unavailable(error)) { }
            foreach (var request in requests.GetConsumingEnumerable())
            {
                if (request.Token.IsCancellationRequested)
                { request.Completion.TrySetCanceled(request.Token); continue; }
                SpellingEvidence? evidence = null;
                if (!cache.TryGetValue(request.Word, out evidence))
                {
                    var suggestions = new List<string>();
                    var checkedWord = false;
                    foreach (var language in request.Word.Any(char.IsAsciiLetter) ? englishLanguages : new[] { "ru-RU" })
                    {
                        try
                        {
                            if (factory != null && !checkers.ContainsKey(language))
                            {
                                ISpellChecker? created = null;
                                try
                                {
                                    created = factory.IsSupported(language) ? factory.CreateSpellChecker(language) : null;
                                    // Windows can silently fall back to another dialect. Do not
                                    // query or retain the same provider more than once.
                                    if (created != null && !actualLanguages.Add(created.GetLanguageTag()))
                                    { Release(created); created = null; }
                                    checkers[language] = created;
                                    created = null; // The worker now owns the stored checker.
                                }
                                finally { Release(created); }
                            }
                            if (checkers.TryGetValue(language, out var checker) && checker != null)
                            {
                                var current = Check(checker, request.Word);
                                checkedWord = true;
                                if (!current.HasError) { evidence = current; break; }
                                suggestions.AddRange(current.Suggestions);
                            }
                        }
                        catch (Exception error) when (Unavailable(error)) { }
                    }
                    if (evidence == null && checkedWord)
                        evidence = new(true, suggestions.Where(value => value.Length is >= 1 and <= 32).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => CorrectionRules.DamerauDistance(request.Word.ToLowerInvariant(), value.ToLowerInvariant()))
                            .Take(24).ToArray());
                    if (cache.Count >= 512) cache.Remove(order.Dequeue());
                    cache[request.Word] = evidence;
                    order.Enqueue(request.Word);
                }
                request.Completion.TrySetResult(evidence);
            }
        }
        finally
        {
            foreach (var checker in checkers.Values) Release(checker);
            Release(factory);
            while (requests.TryTake(out var request)) request.Completion.TrySetResult(null);
            requests.Dispose();
        }
    }

    private static string[] EnglishLanguages(ISpellCheckerFactory factory)
    {
        var supported = factory.GetSupportedLanguages();
        try
        {
            var languages = new List<string> { "en-US" };
            var buffer = new string[1];
            while (true)
            {
                var next = supported.Next(1, buffer, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(next);
                if (next != 0) break;
                if (buffer[0].StartsWith("en-", StringComparison.OrdinalIgnoreCase)) languages.Add(buffer[0]);
            }
            return languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Release(supported); }
    }

    private static SpellingEvidence Check(ISpellChecker checker, string word)
    {
        var errors = checker.Check(word);
        ISpellingError? error = null;
        IEnumString? suggestions = null;
        try
        {
            var result = errors.Next(out error);
            Marshal.ThrowExceptionForHR(result);
            if (result == 1 || error == null) return new(false, Array.Empty<string>());
            var values = new List<string>();
            if (error.GetCorrectiveAction() == 2 && error.GetReplacement() is { Length: > 0 } replacement)
                values.Add(replacement);
            suggestions = checker.Suggest(word);
            var buffer = new string[1];
            for (var index = 0; index < 12; index++)
            {
                var next = suggestions.Next(1, buffer, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(next);
                if (next != 0) break;
                if (!string.IsNullOrWhiteSpace(buffer[0])) values.Add(buffer[0]);
            }
            return new(true, values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        finally { Release(suggestions); Release(error); Release(errors); }
    }

    private static bool Unavailable(Exception error) => error is COMException or InvalidCastException or
        TypeLoadException or NotSupportedException or System.IO.FileNotFoundException;

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            requests.CompleteAdding();
        }
    }

    // Vtable order and IDs are defined by the Windows SDK spellcheck.h.
    // Only read operations are exposed; dictionaries are never modified.
    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactory { }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString GetSupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetLanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out ISpellingError? value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint GetStartIndex();
        uint GetLength();
        uint GetCorrectiveAction();
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetReplacement();
    }
}
