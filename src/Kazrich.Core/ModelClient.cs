using System.Net.Http.Json;
using System.Text.Json;

namespace Kazrich.Core;

public sealed record CorrectionResult(string Original, string? Replacement, bool Cached);

public sealed class ModelClient : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string?> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> cacheOrder = new();
    private readonly object responseLock = new();
    private readonly Dictionary<(string Instructions, string Input, int Limit), string> responseCache = new();
    private readonly Queue<(string Instructions, string Input, int Limit)> responseOrder = new();
    private readonly ISpellingHints? spellingHints;
    private sealed record TerminalAlternative(string OriginalToken, string LiteralWord, string LiteralTail, string MappedWord, string RemainingTail);
    public Settings Settings { get; }

    public ModelClient(Settings settings, HttpMessageHandler? handler = null, ISpellingHints? spellingHints = null)
    {
        settings.Validate();
        if (settings.Backend == "Managed") throw new ArgumentException("Сначала запустите встроенный локальный движок.");
        Settings = settings;
        this.spellingHints = spellingHints;
        http = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) : new HttpClient(handler);
        http.BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
    }

    public async Task<CorrectionResult> CorrectAsync(string word, CancellationToken cancellationToken)
    {
        var mixed = Settings.FixKeyboardLayout && CorrectionRules.IsMixedCandidate(word, Settings.WordSet());
        if (!mixed && !CorrectionRules.IsCandidate(word, Settings.WordSet())) return new(word, null, false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(word, out var hit)) return new(word, hit, true);
            if (!mixed && spellingHints != null &&
                (await spellingHints.CheckAsync(word, cancellationToken).ConfigureAwait(false))?.HasError == false)
            {
                Remember(word, word, cancellationToken);
                return new(word, word, false);
            }
            if (!mixed && await FindWordSpacingAsync(word, cancellationToken).ConfigureAwait(false) != word)
                return new(word, word, false);
            string? replacement = null;
            if (mixed)
            {
                var mostlyLatin = word.Count(char.IsAsciiLetter) > word.Length / 2;
                foreach (var toLatin in new[] { mostlyLatin, !mostlyLatin })
                {
                    var alternative = KeyboardLayout.Normalize(word, toLatin);
                    if (!alternative.All(CorrectionRules.IsLetter)) continue;
                    // A punctuation-only layout difference may be a real opening
                    // delimiter. Require spelling to accept its mapped word intact.
                    if (!word.Any(char.IsAsciiLetter))
                    {
                        var spelling = await AskSpellingAsync(alternative, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(spelling?.Trim(), alternative, StringComparison.OrdinalIgnoreCase) &&
                            !(spellingHints != null && (await spellingHints.CheckAsync(alternative, cancellationToken).ConfigureAwait(false))?.HasError == false &&
                                (await AskAsync(Settings.WordValidityInstructions, alternative, cancellationToken).ConfigureAwait(false))?.Trim() == "ДА")) continue;
                    }
                    if (await ConfirmLayoutAsync(word, alternative, cancellationToken).ConfigureAwait(false))
                    { replacement = alternative; break; }
                    // Do not normalize away a possible literal delimiter when
                    // there are no Latin letters establishing a mixed fragment.
                    if (!word.Any(char.IsAsciiLetter)) continue;
                    // Mixed fragments can be truncated or mistaken for separators.
                    // Compare the same keystrokes in each complete layout too.
                    if (await ConfirmLayoutAsync(KeyboardLayout.Normalize(word, !toLatin), alternative, cancellationToken).ConfigureAwait(false))
                    { replacement = alternative; break; }
                    alternative = await CheckMappedSpellingAsync(alternative, cancellationToken).ConfigureAwait(false);
                    var choice = await AskAsync(Settings.LayoutInstructions, word + "\n" + alternative, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(choice?.Trim(), alternative, StringComparison.OrdinalIgnoreCase)) continue;
                    var confirm = await AskAsync(Settings.LayoutInstructions, alternative + "\n" + word, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(confirm?.Trim(), alternative, StringComparison.OrdinalIgnoreCase)) continue;
                    replacement = alternative;
                    break;
                }
            }
            string? layoutChoice = null;
            // Resolve layout first so a plausible spelling edit cannot hide wrong keystrokes.
            if (!mixed && Settings.FixKeyboardLayout)
            {
                var alternative = KeyboardLayout.Convert(word);
                if (alternative != word && alternative.All(CorrectionRules.IsLetter))
                {
                    if (await ConfirmLayoutAsync(word, alternative, cancellationToken).ConfigureAwait(false))
                        replacement = alternative;
                    else
                    {
                        alternative = await CheckMappedSpellingAsync(alternative, cancellationToken).ConfigureAwait(false);
                        var choice = await AskAsync(Settings.LayoutInstructions, word + "\n" + alternative, cancellationToken).ConfigureAwait(false);
                        layoutChoice = choice;
                        if (string.Equals(choice?.Trim(), alternative, StringComparison.OrdinalIgnoreCase))
                        {
                            // Require a consistent decision regardless of candidate order.
                            var confirmation = await AskAsync(Settings.LayoutInstructions, alternative + "\n" + word, cancellationToken).ConfigureAwait(false);
                            if (string.Equals(confirmation?.Trim(), alternative, StringComparison.OrdinalIgnoreCase)) replacement = alternative;
                        }
                    }
                }
            }
            if (!mixed && replacement == null)
            {
                // A confirmed layout correction already resolves the original keystrokes.
                var raw = await AskSpellingAsync(word, cancellationToken).ConfigureAwait(false);
                replacement = CorrectionRules.Validate(word, raw) ?? CorrectionRules.Validate(word, layoutChoice);
                var acceptedProposal = replacement != null && replacement != word && spellingHints != null &&
                    (await spellingHints.CheckAsync(replacement, cancellationToken).ConfigureAwait(false))?.HasError == false &&
                    !await HasCloserSpellingHintAsync(word, replacement, cancellationToken).ConfigureAwait(false);
                if (!acceptedProposal)
                {
                    var hinted = await CheckSpellingHintsAsync(word, cancellationToken).ConfigureAwait(false);
                    if (hinted != word) replacement = hinted;
                }
                if (replacement != null && replacement != word && spellingHints != null &&
                    ((await spellingHints.CheckAsync(word, cancellationToken).ConfigureAwait(false))?.HasError == false ||
                        await HasSpellingErrorAsync(replacement, cancellationToken))) replacement = word;
            }
            if (!mixed && Settings.FixKeyboardLayout && (replacement == null || replacement == word))
            {
                var mapped = KeyboardLayout.Convert(word);
                if (mapped != word && mapped.All(CorrectionRules.IsLetter))
                {
                    // Recognize each word independently when pair selection could
                    // not resolve the layout. Never accept an ambiguous or free-form reply.
                    var originalValidity = await AskAsync(Settings.WordValidityInstructions, word, cancellationToken).ConfigureAwait(false);
                    if (originalValidity?.Trim() == "НЕТ")
                    {
                        var mappedValidity = await AskAsync(Settings.WordValidityInstructions, mapped, cancellationToken).ConfigureAwait(false);
                        if (mappedValidity?.Trim() == "ДА")
                        {
                            var spelling = await AskSpellingAsync(mapped, cancellationToken).ConfigureAwait(false);
                            if (string.Equals(spelling?.Trim(), mapped, StringComparison.OrdinalIgnoreCase)) replacement = mapped;
                        }
                        if (replacement == null || replacement == word)
                        {
                            var spelled = await CheckMappedSpellingAsync(mapped, cancellationToken).ConfigureAwait(false);
                            if (spelled != mapped && await ConfirmSpellingAsync(mapped, spelled, cancellationToken).ConfigureAwait(false) &&
                                await IsRecognizedSpellingAsync(spelled, cancellationToken).ConfigureAwait(false)) replacement = spelled;
                        }
                    }
                }
            }
            var rejectedMappedSpelling = mixed && replacement != null && await HasSpellingErrorAsync(replacement, cancellationToken).ConfigureAwait(false);
            if (mixed && word.Any(char.IsAsciiLetter) &&
                (replacement == null || rejectedMappedSpelling || (await AskAsync(Settings.WordValidityInstructions, replacement, cancellationToken).ConfigureAwait(false))?.Trim() == "НЕТ") &&
                (rejectedMappedSpelling || (await AskAsync(Settings.WordValidityInstructions, word, cancellationToken).ConfigureAwait(false))?.Trim() == "НЕТ"))
            {
                // Pair selection can prefer the wrong complete layout. Only an
                // independently recognized, unambiguous normalization can replace it.
                var recognized = new List<string>();
                foreach (var mapped in new[] { KeyboardLayout.Normalize(word, false), KeyboardLayout.Normalize(word, true) }.Distinct())
                {
                    if (!CorrectionRules.IsCandidate(mapped, Settings.WordSet())) continue;
                    if (rejectedMappedSpelling && await HasSpellingErrorAsync(mapped, cancellationToken).ConfigureAwait(false)) continue;
                    if ((await AskAsync(Settings.WordValidityInstructions, mapped, cancellationToken).ConfigureAwait(false))?.Trim() != "ДА") continue;
                    if (string.Equals((await AskSpellingAsync(mapped, cancellationToken).ConfigureAwait(false))?.Trim(), mapped, StringComparison.OrdinalIgnoreCase) ||
                        spellingHints != null && (await spellingHints.CheckAsync(mapped, cancellationToken).ConfigureAwait(false))?.HasError == false)
                        recognized.Add(mapped);
                }
                if (recognized.Count == 1) replacement = recognized[0];
            }
            if (!mixed && replacement != null && replacement != word &&
                replacement.Any(char.IsAsciiLetter) != word.Any(char.IsAsciiLetter) &&
                await HasSpellingErrorAsync(replacement, cancellationToken))
            {
                // A mistaken layout choice can turn an ordinary typo into a
                // second non-word. Prefer a confirmed spelling of the original
                // alphabet when the installed provider accepts that spelling.
                var spelledOriginal = CorrectionRules.Validate(word,
                    await AskSpellingAsync(word, cancellationToken).ConfigureAwait(false));
                var hintedOriginal = await CheckSpellingHintsAsync(word, cancellationToken).ConfigureAwait(false);
                if (hintedOriginal != word) spelledOriginal = hintedOriginal;
                if (spelledOriginal != null && spelledOriginal != word && spellingHints != null &&
                    (await spellingHints.CheckAsync(spelledOriginal, cancellationToken).ConfigureAwait(false))?.HasError == false)
                    replacement = spelledOriginal;
            }
            if (!mixed && replacement != null && replacement != word &&
                replacement.Any(char.IsAsciiLetter) != word.Any(char.IsAsciiLetter))
            {
                var validity = await AskAsync(Settings.WordValidityInstructions, word, cancellationToken).ConfigureAwait(false);
                var originalEvidence = spellingHints == null ? null : await spellingHints.CheckAsync(word, cancellationToken).ConfigureAwait(false);
                var mappedEvidence = spellingHints == null ? null : await spellingHints.CheckAsync(replacement, cancellationToken).ConfigureAwait(false);
                if (!(originalEvidence?.HasError == true && mappedEvidence?.HasError == false) &&
                    (validity?.Trim() == "ДА" || originalEvidence?.HasError == false))
                {
                    var spelling = await AskSpellingAsync(word, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(spelling?.Trim(), word, StringComparison.OrdinalIgnoreCase)) replacement = word;
                }
            }
            Remember(word, replacement, cancellationToken);
            return new(word, replacement, false);
        }
        finally { gate.Release(); }
    }

    private void Remember(string word, string? replacement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A missing proposal can be a temporarily empty/truncated generation.
        // Cache confirmed words and corrections, not an incomplete decision.
        if (replacement == null) return;
        if (cache.Count >= 512) cache.Remove(cacheOrder.Dequeue());
        cache[word] = replacement;
        cacheOrder.Enqueue(word);
    }

    public async Task<string> CorrectPhraseAsync(string text, CancellationToken cancellationToken, string precedingContext = "", string followingContext = "")
    {
        var ignored = Settings.WordSet();
        if (Settings.FixKeyboardLayout && text.Contains('-'))
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\S+").Reverse())
            {
                // Keep an existing compound intact, including letter keys that
                // look like punctuation inside it. Its complete spelling and
                // both layout choices must agree before changing any letters.
                var corrected = await CorrectHyphenatedLayoutAsync(match.Value, ignored, cancellationToken);
                if (corrected != match.Value) text = text[..match.Index] + corrected + text[(match.Index + match.Length)..];
            }
        var separators = await FindSeparatorsAsync(text, ignored, cancellationToken);
        if (separators.Count > 0)
        {
            var result = new System.Text.StringBuilder();
            var start = 0;
            foreach (var separator in separators)
            {
                if (separator > start)
                    result.Append(await CorrectPhraseAsync(text[start..separator], cancellationToken,
                        start == 0 ? precedingContext : "", ""));
                result.Append(text[separator]);
                start = separator + 1;
            }
            if (start < text.Length)
                result.Append(await CorrectPhraseAsync(text[start..], cancellationToken,
                    start == 0 ? precedingContext : "", followingContext));
            return result.ToString();
        }
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(\s+)");
        var punctuation = new string[parts.Length];
        var terminalLayout = new string?[parts.Length];
        var terminalAlternatives = new TerminalAlternative?[parts.Length];
        // Detach sentence punctuation before any layout or spelling stage can
        // interpret it as a Russian letter key and then delete that letter.
        for (var i = 0; i < parts.Length; i++)
        {
            if (ignored.Contains(parts[i])) continue;
            // On the Russian layout the slash key types a period. Detach it
            // only from a complete Latin-layout token, never from a path or URL.
            if (Settings.FixKeyboardLayout && parts[i].EndsWith('/') && parts[i].Length > 1)
            {
                var slashCore = parts[i][..^1];
                if (slashCore.Any(char.IsAsciiLetter) && slashCore.All(KeyboardLayout.IsLatinLetterKey))
                {
                    punctuation[i] = "/";
                    parts[i] = slashCore;
                    continue;
                }
            }
            var core = parts[i].TrimEnd(',', '.', '!', '?', ';', ':');
            if (core.Length == 0 || core.Length == parts[i].Length) continue;
            var shortCore = core.Length is 1 or 2 && core.All(CorrectionRules.IsLetter) &&
                !ignored.Contains(core) && !core.Skip(1).Any(char.IsUpper) && !core.All(char.IsUpper);
            var keyboardCore = Settings.FixKeyboardLayout && core.Length is >= 1 and <= 32 &&
                core.Any(char.IsAsciiLetter) && core.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)) &&
                !ignored.Contains(core) && !core.Skip(1).Any(char.IsUpper) && !core.All(char.IsUpper);
            if (!CorrectionRules.IsCandidate(core, ignored) &&
                !(Settings.FixKeyboardLayout && (shortCore || keyboardCore || CorrectionRules.IsMixedCandidate(core, ignored)))) continue;
            if (Settings.FixKeyboardLayout && core.Any(char.IsAsciiLetter) &&
                core.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)) &&
                !(core.Length >= 2 && core.All(char.IsAsciiLetter) &&
                    await IsRecognizedSpellingAsync(core, cancellationToken)))
            {
                // The last punctuation key may complete a Russian word (ю/б/ж).
                // Keep it inside the word only if spelling accepts the complete
                // word unchanged and both layout comparisons confirm that word.
                // Any punctuation following the confirmed word remains literal.
                for (var end = Math.Min(parts[i].Length, 32); end > core.Length; end--)
                {
                    var originalWord = parts[i][..end];
                    var mappedWord = KeyboardLayout.Normalize(originalWord, false);
                    if (!mappedWord.All(CorrectionRules.IsLetter)) continue;
                    // A punctuation key must not be swallowed to create a word
                    // that the installed spelling provider explicitly rejects.
                    var mappedEvidence = spellingHints == null ? null : await spellingHints.CheckAsync(mappedWord, cancellationToken).ConfigureAwait(false);
                    if (mappedEvidence?.HasError == true) continue;
                    var mappedCore = KeyboardLayout.Normalize(core, false);
                    var recognizedCore = mappedCore.Length >= 2 && mappedCore.All(CorrectionRules.IsLetter) &&
                        await IsRecognizedSpellingAsync(mappedCore, cancellationToken);
                    if (recognizedCore && spellingHints != null &&
                        (await spellingHints.CheckAsync(mappedWord, cancellationToken).ConfigureAwait(false))?.HasError == false)
                    {
                        terminalAlternatives[i] ??= new(parts[i], mappedCore, parts[i][core.Length..], mappedWord, parts[i][end..]);
                        // With two valid forms and no literal sign left over,
                        // retain punctuation unless sentence context resolves
                        // the ambiguity in the dedicated pass below.
                        if (end == parts[i].Length) continue;
                    }
                    if (recognizedCore && await ConfirmLayoutAsync(mappedWord, mappedCore, cancellationToken)) continue;
                    if (mappedEvidence?.HasError != false)
                    {
                        var spelling = await AskSpellingAsync(mappedWord, cancellationToken);
                        if (!string.Equals(spelling?.Trim(), mappedWord, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (!await ConfirmLayoutAsync(originalWord, mappedWord, cancellationToken) &&
                        !(await IsRecognizedSpellingAsync(mappedWord, cancellationToken) &&
                            (await AskAsync(Settings.WordValidityInstructions, originalWord, cancellationToken))?.Trim() == "НЕТ")) continue;
                    terminalLayout[i] = mappedWord;
                    punctuation[i] = parts[i][end..];
                    parts[i] = originalWord;
                    break;
                }
                if (terminalLayout[i] != null) continue;
            }
            punctuation[i] = parts[i][core.Length..];
            parts[i] = core;
        }
        var originalParts = (string[])parts.Clone();
        var layoutConfirmed = new bool[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (terminalLayout[i] is { } mapped) { parts[i] = mapped; layoutConfirmed[i] = true; }
        (string[] Left, string[] Right) SpellingNeighbours(int index)
        {
            string JoinParts(int start, int count) => string.Concat(Enumerable.Range(start, count)
                .Select(position => parts[position] + punctuation[position]));
            var before = precedingContext + " " + JoinParts(0, index);
            var after = punctuation[index] + JoinParts(index + 1, parts.Length - index - 1) + " " + followingContext;
            var sentenceLeft = System.Text.RegularExpressions.Regex.Split(before, @"[.!?/\r\n]").Last();
            var sentenceRight = System.Text.RegularExpressions.Regex.Split(after, @"[.!?/\r\n]").First();
            return (NeighbouringWords(System.Text.RegularExpressions.Regex.Split(sentenceLeft.Trim(), @"[\s,;:]+"), true),
                NeighbouringWords(System.Text.RegularExpressions.Regex.Split(sentenceRight.Trim(), @"[\s,;:]+"), false));
        }
        bool Eligible(string token) => token.Length is >= 1 and <= 32 && !ignored.Contains(token) &&
            !(token.Any(char.IsAsciiLetter) && token.Any(CorrectionRules.IsRussianLetter)) &&
            !token.Skip(1).Any(char.IsUpper) && (token.Length == 1 || !token.All(char.IsUpper)) &&
            (token.All(CorrectionRules.IsLetter) || token.Any(char.IsAsciiLetter) && KeyboardLayout.Convert(token).All(CorrectionRules.IsLetter));
        if (Settings.FixKeyboardLayout)
        {
            // Compare consecutive words in one alphabet, preserving mixed-language neighbours.
            for (var start = 0; start < parts.Length; start++)
            {
                if (string.IsNullOrWhiteSpace(parts[start]) || !Eligible(parts[start])) continue;
                var latin = parts[start].Any(char.IsAsciiLetter);
                var end = start;
                var count = 1;
                while (end + 2 < parts.Length && !(punctuation[end]?.Any(c => ".!?/".Contains(c)) ?? false) &&
                    string.IsNullOrWhiteSpace(parts[end + 1]) &&
                    Eligible(parts[end + 2]) && parts[end + 2].Any(char.IsAsciiLetter) == latin)
                { end += 2; count++; }
                if (count >= 2)
                {
                    var allKnown = spellingHints != null;
                    if (spellingHints != null)
                        for (var index = start; index <= end; index += 2)
                            if ((await spellingHints.CheckAsync(parts[index], cancellationToken).ConfigureAwait(false))?.HasError != false)
                            { allKnown = false; break; }
                    if (allKnown) { start = end; continue; }
                    var original = string.Concat(parts[start..(end + 1)]);
                    var alternative = KeyboardLayout.Convert(original);
                    var choice = await AskAsync(Settings.PhraseLayoutInstructions, original + "\n" + alternative, cancellationToken, 128);
                    if (choice?.Trim() == alternative)
                    {
                        var confirm = await AskAsync(Settings.PhraseLayoutInstructions, alternative + "\n" + original, cancellationToken, 128);
                        if (confirm?.Trim() == alternative)
                            for (var index = start; index <= end; index++)
                            {
                                var mapped = KeyboardLayout.Convert(parts[index]);
                                if (!await CanChangeLayoutAsync(parts[index], mapped, cancellationToken)) continue;
                                parts[index] = mapped;
                                layoutConfirmed[index] = true;
                            }
                    }
                }
                start = end;
            }
        }
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (layoutConfirmed[i])
            {
                // The phrase already confirmed layout in both orders; only spelling remains.
                var spelled = await CheckMappedSpellingAsync(token, cancellationToken);
                // A spelling proposal must not hide a valid form already selected by layout.
                parts[i] = spelled != token && await ConfirmLayoutAsync(originalParts[i], token, cancellationToken)
                    ? token : spelled;
                continue;
            }
            if (Settings.FixKeyboardLayout && Eligible(token))
            {
                // Keep language context even when earlier words were already consumed by the queue.
                var left = NeighbouringWords(System.Text.RegularExpressions.Regex.Split(precedingContext, @"\s+").Concat(parts.Take(i)), true);
                var right = NeighbouringWords(parts.Skip(i + 1).Concat(System.Text.RegularExpressions.Regex.Split(followingContext, @"\s+")), false);
                var neighbours = left.Concat(right).Where(p => p.Length >= 2 && p.All(CorrectionRules.IsLetter)).ToArray();
                var latin = token.Any(char.IsAsciiLetter);
                if (neighbours.Length > 0 && (token.Length == 1 || neighbours.Any(p => p.Any(char.IsAsciiLetter) != latin)))
                {
                    var original = string.Join(" ", left.Append(token).Concat(right));
                    var mapped = KeyboardLayout.Convert(token);
                    if (mapped.All(CorrectionRules.IsLetter))
                    {
                        async Task<bool> ConfirmContext(string candidate)
                        {
                            var alternative = string.Join(" ", left.Append(candidate).Concat(right));
                            async Task<bool> ConfirmAlternative(string proposed)
                            {
                                if (!await CanChangeLayoutAsync(token, candidate, cancellationToken)) return false;
                                var choice = await AskAsync(Settings.ContextLayoutInstructions, original + "\n" + proposed, cancellationToken, 128);
                                if (choice?.Trim() != original && choice?.Trim() != proposed) return false;
                                var confirm = await AskAsync(Settings.ContextLayoutInstructions, proposed + "\n" + original, cancellationToken, 128);
                                if (choice?.Trim() == proposed && confirm?.Trim() == proposed) return true;
                                if (choice?.Trim() == original && confirm?.Trim() == original) return false;
                                // Either direction of an order-dependent choice is
                                // inconclusive. A first-option bias must not veto a
                                // correction before the reverse comparison is read.
                                // Recheck the complete alternatives with the phrase
                                // instruction, still requiring agreement in both orders.
                                var phraseChoice = await AskAsync(Settings.PhraseLayoutInstructions, original + "\n" + proposed, cancellationToken, 128);
                                if (phraseChoice?.Trim() != proposed) return false;
                                var phraseConfirm = await AskAsync(Settings.PhraseLayoutInstructions, proposed + "\n" + original, cancellationToken, 128);
                                return phraseConfirm?.Trim() == proposed;
                            }
                            if (await ConfirmAlternative(alternative)) return true;
                            // The mapped letters can still look incorrect until a missing
                            // hyphen is restored. Confirm that spelling without changing context here.
                            if (!Settings.FixHyphens) return false;
                            var hyphenated = await CorrectHyphensAsync(alternative, cancellationToken);
                            return hyphenated != alternative && await ConfirmAlternative(hyphenated);
                        }
                        if (await ConfirmContext(mapped)) { parts[i] = mapped; continue; }
                        var spelled = await CheckMappedSpellingAsync(mapped, cancellationToken);
                        if (spelled != mapped && await ConfirmContext(spelled)) { parts[i] = spelled; continue; }
                    }
                }
            }
            if (CorrectionRules.IsCandidate(token, ignored) || Settings.FixKeyboardLayout && CorrectionRules.IsMixedCandidate(token, ignored))
            {
                var result = await CorrectAsync(token, cancellationToken);
                parts[i] = result.Replacement ?? token;
                if (parts[i] != token && parts[i].Any(char.IsAsciiLetter) == token.Any(char.IsAsciiLetter))
                {
                    var neighbours = SpellingNeighbours(i);
                    parts[i] = await CheckSpellingChoiceInContextAsync(token, parts[i], neighbours.Left, neighbours.Right, cancellationToken);
                }
                if (parts[i] != token && parts[i].Length == token.Length &&
                    parts[i].Any(char.IsAsciiLetter) == token.Any(char.IsAsciiLetter))
                {
                    var neighbours = SpellingNeighbours(i);
                    if (await KeepOriginalSpellingAsync(token, neighbours.Left, neighbours.Right, cancellationToken)) parts[i] = token;
                }
            }
            else if (Settings.FixKeyboardLayout && Eligible(token) && token.Length >= 2)
            {
                var alternative = KeyboardLayout.Convert(token);
                if (alternative.All(CorrectionRules.IsLetter))
                {
                    if (await ConfirmLayoutAsync(token, alternative, cancellationToken))
                    { parts[i] = alternative; continue; }
                    alternative = await CheckMappedSpellingAsync(alternative, cancellationToken);
                    var choice = await AskAsync(Settings.LayoutInstructions, token + "\n" + alternative, cancellationToken);
                    if (choice?.Trim() == alternative)
                    {
                        var confirm = await AskAsync(Settings.LayoutInstructions, alternative + "\n" + token, cancellationToken);
                        if (confirm?.Trim() == alternative) parts[i] = alternative;
                    }
                }
            }
            if (parts[i] == token)
            {
                // Preserve sentence punctuation while correcting the preceding word.
                var core = token.TrimEnd(',', '.', '!', '?', ';', ':');
                if (core != token && (CorrectionRules.IsCandidate(core, ignored) || Settings.FixKeyboardLayout && CorrectionRules.IsMixedCandidate(core, ignored)))
                {
                    var result = await CorrectAsync(core, cancellationToken);
                    parts[i] = (result.Replacement ?? core) + token[core.Length..];
                }
            }
            if (Settings.FixKeyboardLayout && parts[i] == token && token.Contains(','))
                parts[i] = await CorrectCommaSeparatedAsync(token, cancellationToken);
        }
        if (Settings.FixKeyboardLayout)
            for (var i = 0; i < parts.Length; i++)
            {
                var word = parts[i];
                // Validate spelling after layout conversion: choosing a layout
                // does not establish that the mapped spelling is valid.
                var original = originalParts[i];
                var mappedLayout = word == KeyboardLayout.Convert(original) ||
                    CorrectionRules.IsMixedCandidate(original, ignored) &&
                    (word == KeyboardLayout.Normalize(original, false) || word == KeyboardLayout.Normalize(original, true));
                if (word == original || !mappedLayout || !CorrectionRules.IsCandidate(word, ignored)) continue;
                // Recognizing the intended layout does not mean its mapped
                // spelling is correct. Compare the spellings themselves, not
                // the misspelling against the original wrong-layout keystrokes.
                var spelled = await CheckMappedSpellingAsync(word, cancellationToken);
                var spellingNeighbours = SpellingNeighbours(i);
                if (spelled != word && await ConfirmSpellingAsync(word, spelled, cancellationToken) &&
                    (spelled.Length != word.Length ||
                        await HasSpellingErrorAsync(word, cancellationToken) ||
                        (await AskAsync(Settings.WordValidityInstructions, word, cancellationToken))?.Trim() != "ДА") &&
                    await ConfirmContextualSpellingAsync(word, spelled, spellingNeighbours.Left, spellingNeighbours.Right, cancellationToken))
                {
                    parts[i] = spelled;
                    continue;
                }
                var left = NeighbouringWords(System.Text.RegularExpressions.Regex.Split(precedingContext, @"\s+").Concat(parts.Take(i)), true, 1);
                var right = NeighbouringWords(parts.Skip(i + 1).Concat(System.Text.RegularExpressions.Regex.Split(followingContext, @"\s+")), false, 1);
                parts[i] = await CheckContextualTranspositionAsync(word, left, right, cancellationToken);
                if (parts[i] == word)
                {
                    var neighbours = SpellingNeighbours(i);
                    parts[i] = await CheckSpellingInContextAsync(word, neighbours.Left, neighbours.Right, cancellationToken);
                    if (parts[i] == word)
                        parts[i] = await CheckMappedKeyTypoAsync(word, neighbours.Left, neighbours.Right, cancellationToken);
                }
            }
        if (Settings.FixKeyboardLayout)
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] != originalParts[i] || !Eligible(parts[i])) continue;
                var next = Enumerable.Range(i + 1, parts.Length - i - 1)
                    .Where(index => !string.IsNullOrWhiteSpace(parts[index])).Take(2).ToArray();
                if (!next.Any(index => parts[index] != originalParts[index])) continue;
                // Earlier words were checked against the original right context.
                // Recheck once after that context changes. The nested call has
                // only one token, so it cannot trigger this pass recursively.
                var left = NeighbouringWords(System.Text.RegularExpressions.Regex.Split(precedingContext, @"\s+").Concat(parts.Take(i)), true);
                var right = NeighbouringWords(parts.Skip(i + 1).Concat(System.Text.RegularExpressions.Regex.Split(followingContext, @"\s+")), false);
                parts[i] = await CorrectPhraseAsync(parts[i], cancellationToken, string.Join(" ", left), string.Join(" ", right));
            }
        // A terminal key may complete another valid grammatical form. Revisit
        // that ambiguity only after the neighbouring words have their correct
        // layout, retaining every original sign in the literal alternative.
        for (var index = 0; index < parts.Length; index++)
        {
            var choice = terminalAlternatives[index];
            if (choice == null || parts[index] != choice.LiteralWord && parts[index] != choice.MappedWord ||
                punctuation[index] != choice.LiteralTail && punctuation[index] != choice.RemainingTail) continue;
            string Joined(int start, int count) => string.Concat(Enumerable.Range(start, count).Select(position => parts[position] + punctuation[position]));
            var before = precedingContext + " " + Joined(0, index);
            var after = Joined(index + 1, parts.Length - index - 1) + " " + followingContext;
            before = System.Text.RegularExpressions.Regex.Split(before, @"[.!?\r\n]").Last();
            after = System.Text.RegularExpressions.Regex.Split(after, @"[.!?\r\n]").First();
            var left = NeighbouringWords(System.Text.RegularExpressions.Regex.Split(before.Trim(), @"\s+"), true);
            var right = NeighbouringWords(System.Text.RegularExpressions.Regex.Split(after.Trim(), @"\s+"), false);
            if (left.Length + right.Length == 0) continue;
            string Context(string word, string tail) => string.Join(" ", left.Append(word + tail).Concat(right));
            var literal = Context(choice.LiteralWord, choice.LiteralTail);
            var mapped = Context(choice.MappedWord, choice.RemainingTail);
            if (literal.Length + mapped.Length > 800) continue;
            async Task<string?> Select(string instruction, string? original = null)
            {
                var prefix = original == null ? "" : original + "\n";
                if (prefix.Length + literal.Length + mapped.Length > 800) return null;
                var result = (await AskAsync(instruction, prefix + literal + "\n" + mapped, cancellationToken, 128))?.Trim();
                if (result != literal && result != mapped) return null;
                return (await AskAsync(instruction, prefix + mapped + "\n" + literal, cancellationToken, 128))?.Trim() == result ? result : null;
            }
            // Spelling choice has a different input contract: its first line
            // is the original text. Keep that line fixed when reversing candidates.
            var selected = await Select(Settings.PhraseLayoutInstructions) ??
                await Select(Settings.SpellingChoiceInstructions, Context(choice.OriginalToken, ""));
            if (selected == null) continue;
            // A single neighbour often leaves a valid sentence fragment. The
            // small model can confidently invent an inflection there; require
            // a fuller phrase before consuming its otherwise valid punctuation.
            if (selected == mapped && left.Length + right.Length < 2) continue;
            parts[index] = selected == literal ? choice.LiteralWord : choice.MappedWord;
            punctuation[index] = selected == literal ? choice.LiteralTail : choice.RemainingTail;
        }
        return string.Concat(parts.Select((part, index) => part +
            (punctuation[index] == "/" && originalParts[index].Any(char.IsAsciiLetter) &&
                part.All(CorrectionRules.IsLetter) && !part.Any(char.IsAsciiLetter) ? "." : punctuation[index])));
    }

    private async Task<string> CorrectHyphenatedLayoutAsync(string token, ISet<string> ignored, CancellationToken cancellationToken)
    {
        if (!token.Contains('-') || token.Length > 72 || TextBoundary.IsProtected(token, ignored)) return token;
        var coreLength = token.TrimEnd(',', '.', ';', ':', '!', '?').Length;
        // Prefer a literal terminal sign when the shorter compound is already
        // a word. Only consider its letter-key reading if that form fails.
        for (var end = coreLength; end <= Math.Min(token.Length, 64); end++)
        {
            var original = token[..end];
            if (!IsHyphenatedLayoutWord(original, ignored)) continue;
            foreach (var toLatin in new[] { false, true })
            {
                var mapped = KeyboardLayout.Normalize(original, toLatin);
                if (mapped == original || !mapped.All(c => CorrectionRules.IsLetter(c) || c == '-') ||
                    !IsHyphenatedLayoutWord(mapped, ignored)) continue;
                if (await HasSpellingErrorAsync(mapped, cancellationToken)) continue;
                if (!string.Equals((await AskSpellingAsync(mapped, cancellationToken))?.Trim(), mapped, StringComparison.OrdinalIgnoreCase) ||
                    (await AskAsync(Settings.WordValidityInstructions, mapped, cancellationToken))?.Trim() != "ДА") continue;
                var confirmed = await ConfirmLayoutAsync(original, mapped, cancellationToken);
                var otherLayout = KeyboardLayout.Normalize(original, !toLatin);
                if (!confirmed && otherLayout != original)
                    confirmed = await ConfirmLayoutAsync(otherLayout, mapped, cancellationToken);
                if (!confirmed)
                {
                    var choice = await AskAsync(Settings.PhraseLayoutInstructions, original + "\n" + mapped, cancellationToken, 128);
                    confirmed = choice?.Trim() == mapped &&
                        (await AskAsync(Settings.PhraseLayoutInstructions, mapped + "\n" + original, cancellationToken, 128))?.Trim() == mapped;
                }
                if (!confirmed) continue;
                return mapped + token[end..];
            }
        }
        return token;
    }

    private static bool IsHyphenatedLayoutWord(string word, ISet<string> ignored)
    {
        if (word.Length > 64 || !word.Contains('-') || ignored.Contains(word) || word.Skip(1).Any(char.IsUpper)) return false;
        var pieces = word.Split('-');
        return pieces.Length is >= 2 and <= 4 && word.Any(CorrectionRules.IsLetter) &&
            pieces.All(piece => piece.Length is >= 1 and <= 32 && !ignored.Contains(piece) &&
                piece.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)));
    }

    private static string[] NeighbouringWords(IEnumerable<string> values, bool preceding, int count = 2)
    {
        var words = values.Where(value => !string.IsNullOrWhiteSpace(value));
        if (preceding) words = words.Reverse();
        // Treat an oversized neighbouring token as a boundary. It cannot be a
        // correction candidate and must not overflow the model context or allow
        // unrelated words beyond it to decide the current word's layout.
        words = words.Take(count).TakeWhile(value => value.Length <= 32);
        return (preceding ? words.Reverse() : words).ToArray();
    }

    private async Task<List<int>> FindSeparatorsAsync(string text, ISet<string> ignored, CancellationToken token)
    {
        var result = new List<int>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\S+"))
        {
            var word = match.Value;
            if (!word.Any(TextBoundary.IsSign) || TextBoundary.IsProtected(word, ignored)) continue;
            // Paired wrappers are punctuation even when their physical keys are
            // letters in the other layout. Process their contents recursively.
            if (word.Length >= 2 && TextBoundary.IsOpening(word[0]) && TextBoundary.IsClosing(word[^1]))
            {
                result.Add(match.Index); result.Add(match.Index + word.Length - 1);
                continue;
            }
            var terminalCore = word.TrimEnd(',', '.', ';', ':', '!', '?');
            if (terminalCore.Length > 0 && terminalCore.Length < word.Length && terminalCore.Any(char.IsAsciiLetter) &&
                terminalCore.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)))
            {
                // Leave a run of terminal keys to the dedicated word/punctuation
                // resolver. Internal punctuation still needs separate evidence.
                var terminalWord = terminalCore.All(CorrectionRules.IsLetter);
                if (!terminalWord)
                    for (var end = terminalCore.Length; end <= Math.Min(word.Length, 32); end++)
                    {
                        var mapped = KeyboardLayout.Normalize(word[..end], false);
                        if (await IsRecognizedSpellingAsync(mapped, token)) { terminalWord = true; break; }
                    }
                if (terminalWord) continue;
            }
            var trailing = word.Length;
            while (trailing > 0 && TextBoundary.IsSign(word[trailing - 1]) && !KeyboardLayout.IsLatinLetterKey(word[trailing - 1])) trailing--;
            if (trailing > 0 && trailing < word.Length)
            {
                result.AddRange(Enumerable.Range(match.Index + trailing, word.Length - trailing));
                continue;
            }
            // Resolve one outer wrapper at a time after terminal punctuation.
            // Splitting every sign at once would also break a keyboard word
            // inside it, e.g. (,.l;tnf; inspecting [jxe! whole would misread х.
            if (word.Length > 1 && TextBoundary.IsOpening(word[0]) &&
                !await IsWrapperLetterAsync(word, true, ignored, token))
            {
                result.Add(match.Index);
                continue;
            }
            if (word.Length > 1 && TextBoundary.IsClosing(word[^1]) &&
                !await IsWrapperLetterAsync(word, false, ignored, token))
            {
                result.Add(match.Index + word.Length - 1);
                continue;
            }
            // Preserve the established terminal-key check (e.g. a dot can be ю).
            if (word.Length > 1 && KeyboardLayout.IsLatinLetterKey(word[^1]) &&
                word[..^1].All(char.IsAsciiLetter)) continue;
            var punctuation = Enumerable.Range(0, word.Length)
                .Where(index => TextBoundary.IsSign(word[index]) && !TextBoundary.IsInternalJoiner(word, index)).ToArray();
            if (punctuation.Length == 0) continue;
            if (Settings.FixKeyboardLayout &&
                !(punctuation.All(index => index == word.Length - 1) && word[..^1].All(CorrectionRules.IsLetter) && !word[..^1].Any(char.IsAsciiLetter)) &&
                (word.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)) || IsHyphenatedLayoutWord(word, ignored)))
            {
                var confirmedWhole = false;
                foreach (var toLatin in new[] { false, true })
                {
                    var mapped = KeyboardLayout.Normalize(word, toLatin);
                    var compound = IsHyphenatedLayoutWord(mapped, ignored) && mapped.All(c => CorrectionRules.IsLetter(c) || c == '-');
                    var shortWord = mapped.Length == 2 && mapped.All(CorrectionRules.IsLetter) &&
                        !ignored.Contains(mapped) && !mapped.Skip(1).Any(char.IsUpper) && !mapped.All(char.IsUpper);
                    if ((!CorrectionRules.IsCandidate(mapped, ignored) && !compound && !shortWord) || mapped == word) continue;
                    // This only keeps a possible keyboard word intact. The normal
                    // correction pipeline must still confirm any actual replacement.
                    var spelling = await AskSpellingAsync(mapped, token);
                    if ((compound || shortWord) ? string.Equals(spelling?.Trim(), mapped, StringComparison.OrdinalIgnoreCase) &&
                        !await HasSpellingErrorAsync(mapped, token) : CorrectionRules.Validate(mapped, spelling) != null)
                    { confirmedWhole = true; break; }
                }
                if (confirmedWhole) continue;
            }
            result.AddRange(punctuation.Select(index => match.Index + index));
        }
        return result;
    }

    private async Task<bool> IsWrapperLetterAsync(string word, bool opening, ISet<string> ignored, CancellationToken token)
    {
        var edge = opening ? 0 : word.Length - 1;
        if (!Settings.FixKeyboardLayout || !KeyboardLayout.IsLatinLetterKey(word[edge]) ||
            !word.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c))) return false;
        foreach (var toLatin in new[] { false, true })
        {
            var mapped = KeyboardLayout.Normalize(word, toLatin);
            if (!CorrectionRules.IsCandidate(mapped, ignored) || mapped == word) continue;
            if (spellingHints != null && (await spellingHints.CheckAsync(mapped, token).ConfigureAwait(false))?.HasError == false)
                return true;
            var corrected = CorrectionRules.Validate(mapped, await AskSpellingAsync(mapped, token));
            if (corrected == null || await HasSpellingErrorAsync(corrected, token)) continue;
            // A proposal which deletes the mapped first/last letter is evidence
            // for a literal wrapper, not permission to consume that punctuation.
            if (char.ToLowerInvariant(corrected[opening ? 0 : corrected.Length - 1]) ==
                char.ToLowerInvariant(mapped[edge])) return true;
        }
        return false;
    }

    private async Task<string> CorrectCommaSeparatedAsync(string original, CancellationToken token)
    {
        // A comma can be a letter key inside one word or a separator without
        // surrounding spaces. Try the latter only if whole-word correction failed.
        var words = original.Split(',');
        var ignored = Settings.WordSet();
        if (words.Length is < 2 or > 4 || ignored.Contains(original) ||
            words.Any(word => word.Length is < 2 or > 32 || !word.All(CorrectionRules.IsLetter) ||
                ignored.Contains(word) || word.Skip(1).Any(char.IsUpper) || word.All(char.IsUpper))) return original;
        foreach (var toLatin in new[] { false, true })
        {
            var mapped = words.Select(word => KeyboardLayout.Normalize(word, toLatin)).ToArray();
            var alternative = string.Join(',', mapped);
            if (alternative == original) continue;
            var confirmedWords = true;
            for (var index = 0; index < words.Length; index++)
                if (mapped[index] != words[index] && !await ConfirmLayoutAsync(words[index], mapped[index], token))
                { confirmedWords = false; break; }
            if (!confirmedWords) continue;
            // Present normal punctuation to the model, then retain the user's
            // exact separators when applying the selected layout.
            var originalPhrase = string.Join(", ", words);
            var alternativePhrase = string.Join(", ", mapped);
            var choice = await AskAsync(Settings.PhraseLayoutInstructions, originalPhrase + "\n" + alternativePhrase, token, 128);
            if (choice?.Trim() != alternativePhrase) continue;
            var confirm = await AskAsync(Settings.PhraseLayoutInstructions, alternativePhrase + "\n" + originalPhrase, token, 128);
            if (confirm?.Trim() == alternativePhrase) return alternative;
        }
        return original;
    }

    private async Task<string> CheckContextualTranspositionAsync(string word, string[] left, string[] right, CancellationToken token)
    {
        if (left.Length + right.Length == 0) return word;
        if (spellingHints != null && (await spellingHints.CheckAsync(word, token).ConfigureAwait(false))?.HasError == false) return word;
        string Phrase(string value) => string.Join(" ", left.Append(value).Concat(right));
        var original = Phrase(word);
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < word.Length; i++)
        {
            if (word[i] == word[i + 1]) continue;
            var letters = word.ToCharArray();
            (letters[i], letters[i + 1]) = (letters[i + 1], letters[i]);
            var candidate = new string(letters);
            if (CorrectionRules.Validate(word, candidate) != candidate) continue;
            candidates[Phrase(candidate)] = candidate;
        }
        var input = string.Join("\n", new[] { original }.Concat(candidates.Keys));
        if (candidates.Count == 0 || input.Length > 1400) return word; // Bound the small model's context.
        var selected = (await AskAsync(Settings.ContextLayoutInstructions, input, token, 128))?.Trim();
        if (selected == null || !candidates.TryGetValue(selected, out var replacement)) return word;
        var choice = await AskAsync(Settings.ContextLayoutInstructions, original + "\n" + selected, token, 128);
        if (choice?.Trim() != selected) return word;
        var confirm = await AskAsync(Settings.ContextLayoutInstructions, selected + "\n" + original, token, 128);
        if (confirm?.Trim() != selected) return word;
        var validity = await AskAsync(Settings.WordValidityInstructions, replacement, token);
        return validity?.Trim() == "ДА" ? replacement : word;
    }

    private async Task<string> CheckSpellingInContextAsync(string word, string[] left, string[] right, CancellationToken token)
    {
        if (left.Length + right.Length == 0 || left.Concat(right).Any(value => !value.All(CorrectionRules.IsLetter))) return word;
        if (spellingHints != null && (await spellingHints.CheckAsync(word, token).ConfigureAwait(false))?.HasError == false) return word;
        // An unfinished sentence must not rewrite an independently accepted word.
        if ((await AskAsync(Settings.WordValidityInstructions, word.ToLowerInvariant(), token))?.Trim() == "ДА" &&
            string.Equals((await AskSpellingAsync(word.ToLowerInvariant(), token))?.Trim(), word, StringComparison.OrdinalIgnoreCase)) return word;
        var words = left.Append(word).Concat(right).ToArray();
        var original = string.Join(" ", words);
        // Lowercase only the model's spelling sample, then restore the original
        // case. A capitalized typo can otherwise be mistaken for a proper name.
        var sample = original.ToLowerInvariant();
        var raw = await AskSpellingAsync(sample, token, 128);
        if (string.IsNullOrWhiteSpace(raw)) return word;
        var proposed = System.Text.RegularExpressions.Regex.Split(raw.Trim(), @"\s+");
        if (proposed.Length != words.Length) return word;
        for (var index = 0; index < words.Length; index++)
            if (index != left.Length && proposed[index] != words[index].ToLowerInvariant()) return word;
        var replacement = CorrectionRules.Validate(word, proposed[left.Length]);
        if (replacement == null || replacement == word) return word;
        // This extra pass restores missing or removes extra letters. Replacing
        // letters in an already complete word can change its meaning instead.
        // Ordinary spelling and transposition checks handle those edit types.
        if (replacement.Length == word.Length) return word;
        var checkedSpelling = await AskSpellingAsync(replacement, token);
        if (!string.Equals(checkedSpelling?.Trim(), replacement, StringComparison.OrdinalIgnoreCase)) return word;
        return (await AskAsync(Settings.WordValidityInstructions, replacement, token))?.Trim() == "ДА" ? replacement : word;
    }

    private async Task<bool> KeepOriginalSpellingAsync(string word, string[] left, string[] right, CancellationToken token)
    {
        if (left.Length + right.Length == 0 || left.Concat(right).Any(value => !value.All(CorrectionRules.IsLetter))) return false;
        if (await CheckSpellingHintsAsync(word, token) != word) return false;
        if ((await AskAsync(Settings.WordValidityInstructions, word, token))?.Trim() != "ДА") return false;
        var sample = string.Join(" ", left.Append(word).Concat(right)).ToLowerInvariant();
        var spelling = await AskSpellingAsync(sample, token, 128);
        return string.Equals(spelling?.Trim(), sample, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> CheckMappedKeyTypoAsync(string word, string[] left, string[] right, CancellationToken token)
    {
        // A wrong-layout word can also contain a neighbouring-key typo which
        // happens to be a dictionary word. Only review words converted by us,
        // with two complete, independently recognized neighbours.
        if (spellingHints == null || left.Length + right.Length < 2 ||
            left.Concat(right).Any(value => !value.All(CorrectionRules.IsLetter)) ||
            (await spellingHints.CheckAsync(word, token).ConfigureAwait(false))?.HasError != false) return word;
        foreach (var neighbour in left.Concat(right))
            if ((await spellingHints.CheckAsync(neighbour, token).ConfigureAwait(false))?.HasError != false) return word;
        var ignored = Settings.WordSet();
        var candidates = new List<string>();
        foreach (var candidate in KeyboardLayout.AdjacentKeyTypos(word).Distinct(StringComparer.Ordinal))
        {
            if (ignored.Contains(candidate) || CorrectionRules.Validate(word, candidate) != candidate) continue;
            if ((await spellingHints.CheckAsync(candidate, token).ConfigureAwait(false))?.HasError == false)
                candidates.Add(candidate);
            if (candidates.Count > 8) return word; // Too ambiguous for this bounded comparison.
        }
        if (candidates.Count == 0) return word;
        string Phrase(string value) => string.Join(" ", left.Append(value).Concat(right));
        var original = Phrase(word);
        var alternatives = candidates.ToDictionary(Phrase, value => value, StringComparer.Ordinal);
        var options = new[] { original }.Concat(alternatives.Keys).ToArray();
        var input = original + "\n" + string.Join("\n", options);
        if (input.Length > 1400) return word;
        var selected = (await AskAsync(Settings.SpellingChoiceInstructions, input, token, 128))?.Trim();
        if (selected == null || !alternatives.TryGetValue(selected, out var replacement)) return word;
        // The source stays first; reverse only the selectable alternatives.
        var reverse = (await AskAsync(Settings.SpellingChoiceInstructions,
            original + "\n" + string.Join("\n", options.Reverse()), token, 128))?.Trim();
        if (reverse != selected) return word;
        foreach (var comparison in new[] { original + "\n" + selected, selected + "\n" + original })
            if ((await AskAsync(Settings.ContextLayoutInstructions, comparison, token, 128))?.Trim() != selected) return word;
        if (!string.Equals((await AskSpellingAsync(replacement, token))?.Trim(), replacement, StringComparison.OrdinalIgnoreCase) ||
            (await AskAsync(Settings.WordValidityInstructions, replacement, token))?.Trim() != "ДА") return word;
        return replacement;
    }

    public static string SpellingContextSuffix(string context) => System.Text.RegularExpressions.Regex.Match(context,
        @"(?<!\S)(?:[А-ЯЁа-яё]{1,32} +)?[А-ЯЁа-яё]{1,32} +$").Value;

    public async Task<string> CorrectPrecedingSpellingAsync(string context, string following, CancellationToken token,
        IReadOnlySet<string>? convertedWords = null)
    {
        if (!Settings.FixKeyboardLayout || context.Length == 0 || SpellingContextSuffix(context) != context) return context;
        var parts = System.Text.RegularExpressions.Regex.Split(context, @"(\s+)");
        var nextSentence = System.Text.RegularExpressions.Regex.Split(following, @"[.!?/\r\n]").First();
        var nextWords = NeighbouringWords(System.Text.RegularExpressions.Regex.Split(nextSentence.Trim(), @"\s+"), false);
        if (nextWords.Length == 0) return context;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!CorrectionRules.IsCandidate(parts[index], Settings.WordSet())) continue;
            var left = parts.Take(index).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var right = parts.Skip(index + 1).Where(value => !string.IsNullOrWhiteSpace(value)).Concat(nextWords).Take(2).ToArray();
            var original = parts[index];
            parts[index] = await CheckSpellingInContextAsync(original, left, right, token);
            if (parts[index] == original && convertedWords?.Contains(original) == true)
                parts[index] = await CheckMappedKeyTypoAsync(original, left, right, token);
        }
        return string.Concat(parts);
    }

    public async Task<string> CorrectHyphensAsync(string text, CancellationToken cancellationToken)
    {
        if (!Settings.FixHyphens) return text;
        var ignored = Settings.WordSet();
        foreach (var match in Hyphenation.Words(text, ignored).Reverse().ToArray())
        {
            var spaced = await FindWordSpacingAsync(match.Value, cancellationToken).ConfigureAwait(false);
            if (spaced != match.Value) text = text[..match.Index] + spaced + text[(match.Index + match.Length)..];
        }
        // Resolve the relationship between neighbouring words before considering an
        // internal spelling change. Joined components must not be checked in isolation.
        foreach (var boundary in Hyphenation.Boundaries(text, ignored).OrderDescending().ToArray())
        {
            if (!Hyphenation.Boundaries(text, ignored).Contains(boundary)) continue;
            var pair = Hyphenation.PairAt(text, boundary);
            var proposed = await AskAsync(Settings.HyphenInstructions, pair.Text, cancellationToken, 128);
            var validated = Hyphenation.Validate(pair.Text, proposed?.Trim(), ignored, allowInsertions: false);
            text = text[..pair.Start] + validated + text[(pair.Start + pair.Text.Length)..];
        }
        // Suggest every single-hyphen position, then require the model to confirm its
        // selected spelling in both orders. No word-specific substitutions are used.
        foreach (var match in Hyphenation.Words(text, ignored).Reverse().ToArray())
        {
            var word = match.Value;
            if (spellingHints != null && (await spellingHints.CheckAsync(word, cancellationToken).ConfigureAwait(false))?.HasError == false) continue;
            var candidates = Hyphenation.Insertions(word, ignored).Order().Select(i => word.Insert(i, "-")).ToArray();
            if (candidates.Length == 0) continue;
            var selected = (await AskAsync(Settings.LayoutInstructions, string.Join("\n", new[] { word }.Concat(candidates)), cancellationToken))?.Trim();
            if (selected == null || !candidates.Contains(selected) || !await ConfirmLayoutAsync(word, selected, cancellationToken)) continue;
            if ((await AskAsync(Settings.WordValidityInstructions, word, cancellationToken))?.Trim() == "ДА" &&
                string.Equals((await AskSpellingAsync(word, cancellationToken))?.Trim(), word, StringComparison.OrdinalIgnoreCase)) continue;
            // Candidate selection can consistently prefer an artificial split.
            // Accept it only if a separate spelling review keeps it unchanged.
            var spelling = await AskSpellingAsync(selected, cancellationToken);
            if (!string.Equals(spelling?.Trim(), selected, StringComparison.OrdinalIgnoreCase)) continue;
            text = text[..match.Index] + selected + text[(match.Index + match.Length)..];
        }
        return text;
    }

    private async Task<bool> ConfirmSpellingAsync(string original, string proposed, CancellationToken token)
    {
        if (await CheckSpellingHintsAsync(original, token) == proposed) return true;
        // Confirm an orthographic proposal with spelling and word recognition.
        // Choosing a keyboard layout does not validate a same-alphabet spelling.
        var corrected = await AskSpellingAsync(original, token);
        if (!string.Equals(corrected?.Trim(), proposed, StringComparison.OrdinalIgnoreCase) &&
            await CheckSpellingHintsAsync(original, token) != proposed) return false;
        var checkedSpelling = await AskSpellingAsync(proposed, token);
        if (!string.Equals(checkedSpelling?.Trim(), proposed, StringComparison.OrdinalIgnoreCase)) return false;
        return (await AskAsync(Settings.WordValidityInstructions, proposed, token))?.Trim() == "ДА";
    }

    private async Task<bool> ConfirmContextualSpellingAsync(string original, string proposed, string[] left, string[] right, CancellationToken token)
    {
        if (await CheckSpellingHintsAsync(original, token) == proposed) return true;
        if ((await AskAsync(Settings.WordValidityInstructions, original.ToLowerInvariant(), token))?.Trim() == "НЕТ") return true;
        if (left.Length + right.Length == 0) return true;
        if (left.Concat(right).Any(value => !value.All(CorrectionRules.IsLetter))) return false;
        var sample = string.Join(" ", left.Append(original).Concat(right)).ToLowerInvariant();
        var expected = string.Join(" ", left.Append(proposed).Concat(right)).ToLowerInvariant();
        return string.Equals((await AskSpellingAsync(sample, token, 128))?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ConfirmLayoutAsync(string original, string mapped, CancellationToken cancellationToken)
    {
        var choice = await AskAsync(Settings.LayoutInstructions, original + "\n" + mapped, cancellationToken);
        if (string.Equals(choice?.Trim(), mapped, StringComparison.OrdinalIgnoreCase))
        {
            var confirm = await AskAsync(Settings.LayoutInstructions, mapped + "\n" + original, cancellationToken);
            if (string.Equals(confirm?.Trim(), mapped, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (spellingHints == null || mapped == original ||
            !(mapped == KeyboardLayout.Convert(original) || mapped == KeyboardLayout.Normalize(original, false) || mapped == KeyboardLayout.Normalize(original, true))) return false;
        var evidence = await spellingHints.CheckAsync(original, cancellationToken).ConfigureAwait(false);
        var missingLeadingLetter = original.Length >= 3 && !CorrectionRules.IsLetter(original[0]) &&
            KeyboardLayout.IsLatinLetterKey(original[0]) && original[1..].All(CorrectionRules.IsRussianLetter) &&
            await HasSpellingErrorAsync(original[1..], cancellationToken).ConfigureAwait(false);
        if (evidence?.HasError == false || evidence == null &&
            !((original.Any(char.IsAsciiLetter) || missingLeadingLetter || original.TakeWhile(c => !CorrectionRules.IsLetter(c) && KeyboardLayout.IsLatinLetterKey(c)).Count() >= 2) &&
                original.All(c => CorrectionRules.IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)))) return false;
        return await IsRecognizedSpellingAsync(mapped, cancellationToken) ||
            await FindWordSpacingAsync(mapped, cancellationToken).ConfigureAwait(false) != mapped;
    }

    private async Task<string> CheckMappedSpellingAsync(string mapped, CancellationToken cancellationToken)
    {
        if (!CorrectionRules.IsCandidate(mapped, Settings.WordSet())) return mapped;
        if (spellingHints != null && (await spellingHints.CheckAsync(mapped, cancellationToken).ConfigureAwait(false))?.HasError == false) return mapped;
        if (await FindWordSpacingAsync(mapped, cancellationToken).ConfigureAwait(false) != mapped) return mapped;
        var spelling = await AskSpellingAsync(mapped, cancellationToken).ConfigureAwait(false);
        var proposed = CorrectionRules.Validate(mapped, spelling) ?? mapped;
        if (proposed != mapped && spellingHints != null &&
            (await spellingHints.CheckAsync(proposed, cancellationToken).ConfigureAwait(false))?.HasError == false &&
            !await HasCloserSpellingHintAsync(mapped, proposed, cancellationToken).ConfigureAwait(false)) return proposed;
        var hinted = await CheckSpellingHintsAsync(mapped, cancellationToken).ConfigureAwait(false);
        if (hinted != mapped) return hinted;
        if (proposed != mapped && await HasSpellingErrorAsync(proposed, cancellationToken)) return mapped;
        if (proposed != mapped && proposed.Length == mapped.Length &&
            (await AskAsync(Settings.WordValidityInstructions, mapped, cancellationToken))?.Trim() == "ДА" &&
            !await HasSpellingErrorAsync(mapped, cancellationToken)) return mapped;
        return proposed;
    }

    private async Task<bool> HasSpellingErrorAsync(string word, CancellationToken token) =>
        spellingHints != null && (await spellingHints.CheckAsync(word, token).ConfigureAwait(false))?.HasError == true;

    private async Task<bool> CanChangeLayoutAsync(string original, string proposed, CancellationToken token)
    {
        if (spellingHints == null || !await HasSpellingErrorAsync(proposed, token)) return true;
        if ((await spellingHints.CheckAsync(original, token).ConfigureAwait(false))?.HasError == false) return false;
        // A whole-phrase choice can prefer one language even when it turns an
        // independently repairable word into gibberish. Preserve that spelling
        // unless the mapped text has its own confirmed spelling or spacing repair.
        if (await CheckSpellingHintsAsync(original, token) == original) return true;
        if (await CheckMappedSpellingAsync(proposed, token) != proposed ||
            await FindWordSpacingAsync(proposed, token).ConfigureAwait(false) != proposed) return true;
        return Settings.FixHyphens && await CorrectHyphensAsync(proposed, token) != proposed;
    }

    private async Task<bool> IsRecognizedSpellingAsync(string word, CancellationToken token)
    {
        if (spellingHints == null || (await spellingHints.CheckAsync(word, token).ConfigureAwait(false))?.HasError != false) return false;
        // Exact dictionary recognition preserves inflected forms and ё while
        // the model confirms that the mapped token is a word.
        return (await AskAsync(Settings.WordValidityInstructions, word, token))?.Trim() == "ДА";
    }

    private async Task<string> CheckSpellingHintsAsync(string word, CancellationToken token)
    {
        if (spellingHints == null || !CorrectionRules.IsCandidate(word, Settings.WordSet())) return word;
        var evidence = await spellingHints.CheckAsync(word, token).ConfigureAwait(false);
        if (evidence?.HasError != true) return word;
        if (await FindWordSpacingAsync(word, token).ConfigureAwait(false) != word) return word;
        var spellingOfOriginal = await AskSpellingAsync(word, token);
        var unchanged = string.Equals(spellingOfOriginal?.Trim(), word, StringComparison.OrdinalIgnoreCase);
        var preserveUnchanged = unchanged &&
            (await AskAsync(Settings.WordValidityInstructions, word, token))?.Trim() != "НЕТ";
        var ignored = Settings.WordSet();
        var candidates = evidence.Suggestions.Select(value => ValidateSpellingHint(word, value))
            .OfType<string>().Where(value => value != word && !ignored.Contains(value))
            // An unknown dictionary entry may be slang or a name. When the
            // model accepts it intact, only a repeated letter or a single
            // adjacent transposition is eligible for this additional check.
            .Where(value => !preserveUnchanged || IsRepeatedLetterRemoval(word, value) || IsAdjacentTransposition(word, value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (candidates.Length == 0) return word;
        var bestRank = candidates.Min(value => SpellingHintRank(word, value));
        candidates = candidates.Where(value => SpellingHintRank(word, value) == bestRank).ToArray();
        var selected = (await AskAsync(Settings.SpellingChoiceInstructions, string.Join("\n", new[] { word }.Concat(candidates)), token))?.Trim();
        var proposed = candidates.FirstOrDefault(value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase));
        var nativeFallback = proposed == null && (await AskAsync(Settings.WordValidityInstructions, word, token))?.Trim() == "НЕТ";
        if (nativeFallback) proposed = candidates[0];
        if (proposed == null && candidates.Length == 1 &&
            (IsRepeatedLetterRemoval(word, candidates[0]) || IsAdjacentTransposition(word, candidates[0])) &&
            await ConfirmLayoutAsync(word, candidates[0], token)) proposed = candidates[0];
        if (proposed == null) return word;
        if (preserveUnchanged && IsAdjacentTransposition(word, proposed) &&
            !await ConfirmLayoutAsync(word, proposed, token)) return word;
        if (candidates.Length > 1 && !nativeFallback && !string.Equals((await AskAsync(Settings.SpellingChoiceInstructions,
            string.Join("\n", new[] { word }.Concat(candidates.Reverse())), token))?.Trim(), proposed, StringComparison.OrdinalIgnoreCase)) return word;
        if ((await spellingHints.CheckAsync(proposed, token).ConfigureAwait(false))?.HasError != false) return word;
        if (!await ConfirmNativeSpellingAsync(proposed, token,
            IsRepeatedLetterRemoval(word, proposed) || IsAdjacentTransposition(word, proposed))) return word;
        // The installed provider and the model's spelling review independently
        // accept this exact candidate. A second yes/no classification from the
        // same small model can incorrectly reject ordinary English words.
        return proposed;
    }

    private async Task<bool> ConfirmNativeSpellingAsync(string word, CancellationToken token, bool mechanicalRepair = false)
    {
        var spelling = await AskSpellingAsync(word, token);
        if (string.Equals(spelling?.Trim(), word, StringComparison.OrdinalIgnoreCase)) return true;
        var edited = CorrectionRules.Validate(word, spelling);
        // A second model pass can damage a dictionary-confirmed candidate.
        // Reject that edit when Windows flags it and the model independently
        // recognizes the exact candidate; a different valid spelling stays ambiguous.
        return mechanicalRepair && edited != null && edited != word && await HasSpellingErrorAsync(edited, token) &&
            (await AskAsync(Settings.WordValidityInstructions, word, token))?.Trim() == "ДА";
    }

    private async Task<string> CheckSpellingChoiceInContextAsync(string original, string proposed, string[] left, string[] right, CancellationToken token)
    {
        if (spellingHints == null || left.Length + right.Length == 0 ||
            left.Concat(right).Any(value => !value.All(CorrectionRules.IsLetter)) ||
            !CorrectionRules.IsCandidate(original, Settings.WordSet())) return proposed;
        var evidence = await spellingHints.CheckAsync(original, token).ConfigureAwait(false);
        if (evidence?.HasError != true || (await spellingHints.CheckAsync(proposed, token).ConfigureAwait(false))?.HasError != false)
            return proposed;
        var ignored = Settings.WordSet();
        var candidates = evidence.Suggestions.Append(proposed).Select(value => ValidateSpellingHint(original, value))
            .OfType<string>().Where(value => value != original && !ignored.Contains(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (candidates.Length < 2) return proposed;
        // Context can resolve two equally small edits which standalone ranking
        // cannot: a repeated-letter deletion versus a transposition, for example.
        var distance = candidates.Min(value => CorrectionRules.DamerauDistance(original.ToLowerInvariant(), value.ToLowerInvariant()));
        candidates = candidates.Where(value => CorrectionRules.DamerauDistance(original.ToLowerInvariant(), value.ToLowerInvariant()) == distance).ToArray();
        var confirmed = new List<string>();
        foreach (var candidate in candidates)
            if ((await spellingHints.CheckAsync(candidate, token).ConfigureAwait(false))?.HasError == false) confirmed.Add(candidate);
        if (confirmed.Count < 2) return proposed;
        string Phrase(string word) => string.Join(" ", left.Append(word).Concat(right));
        var alternatives = confirmed.ToDictionary(Phrase, value => value, StringComparer.Ordinal);
        var sample = Phrase(original);
        var choice = (await AskAsync(Settings.SpellingChoiceInstructions,
            string.Join("\n", new[] { sample }.Concat(alternatives.Keys)), token, 128))?.Trim();
        if (choice == null || !alternatives.TryGetValue(choice, out var selected) || selected == proposed) return proposed;
        var reverse = (await AskAsync(Settings.SpellingChoiceInstructions,
            string.Join("\n", new[] { sample }.Concat(alternatives.Keys.Reverse())), token, 128))?.Trim();
        if (reverse != choice || !await ConfirmNativeSpellingAsync(selected, token,
            IsRepeatedLetterRemoval(original, selected) || IsAdjacentTransposition(original, selected))) return proposed;
        if (original.All(char.IsAsciiLetter) && left.Concat(right).All(word => word.All(char.IsAsciiLetter)))
        {
            // The editable English spelling instruction can preserve regional
            // usage in context. Do not override it when its independent review
            // chooses another exact, already validated candidate.
            var reviewed = (await AskSpellingAsync(sample, token, 128))?.Trim();
            if (reviewed != null && alternatives.TryGetValue(reviewed, out var reviewedWord) && reviewedWord != selected)
                return proposed;
        }
        return selected;
    }

    private static bool IsRepeatedLetterRemoval(string original, string proposed) =>
        original.Length == proposed.Length + 1 && Enumerable.Range(1, original.Length - 1).Any(index =>
            char.ToLowerInvariant(original[index]) == char.ToLowerInvariant(original[index - 1]) &&
            string.Equals(original.Remove(index, 1), proposed, StringComparison.OrdinalIgnoreCase));

    private static bool IsAdjacentTransposition(string original, string proposed)
    {
        var lower = original.ToLowerInvariant();
        var candidate = proposed.ToLowerInvariant();
        return lower != candidate && lower.Order().SequenceEqual(candidate.Order()) &&
            CorrectionRules.DamerauDistance(lower, candidate) == 1;
    }

    private static string? ValidateSpellingHint(string word, string suggested)
    {
        var ordinary = CorrectionRules.Validate(word, suggested);
        if (ordinary != null) return ordinary;
        // Two mistakes in a longer word can require a delete plus an insertion.
        // This wider distance is limited to independently supplied candidates;
        // arbitrary model output still uses the ordinary validation limit.
        if (word.Length < 7 || suggested.Length is < 2 or > 32 || !suggested.All(CorrectionRules.IsLetter) ||
            suggested.Any(char.IsAsciiLetter) != word.Any(char.IsAsciiLetter) ||
            suggested.Any(char.IsAsciiLetter) && !suggested.All(char.IsAsciiLetter) ||
            CorrectionRules.DamerauDistance(word.ToLowerInvariant(), suggested.ToLowerInvariant()) > 2) return null;
        suggested = suggested.ToLowerInvariant();
        return char.IsUpper(word[0]) ? char.ToUpperInvariant(suggested[0]) + suggested[1..] : suggested;
    }

    private static int SpellingHintRank(string word, string proposed)
    {
        var lower = word.ToLowerInvariant();
        var candidate = proposed.ToLowerInvariant();
        var distance = CorrectionRules.DamerauDistance(lower, candidate);
        var editKind = lower.Order().SequenceEqual(candidate.Order()) ? 0 :
            IsRepeatedLetterRemoval(word, proposed) ? 1 : 2;
        return distance * 10 + editKind;
    }

    private async Task<bool> HasCloserSpellingHintAsync(string word, string proposed, CancellationToken token)
    {
        if (spellingHints == null) return false;
        var evidence = await spellingHints.CheckAsync(word, token).ConfigureAwait(false);
        if (evidence?.HasError != true) return false;
        var rank = SpellingHintRank(word, proposed);
        var ignored = Settings.WordSet();
        return evidence.Suggestions.Select(value => ValidateSpellingHint(word, value)).OfType<string>()
            .Any(value => value != word && !ignored.Contains(value) && SpellingHintRank(word, value) < rank);
    }

    private async Task<string> FindWordSpacingAsync(string word, CancellationToken token)
    {
        var ignored = Settings.WordSet();
        if (!Settings.FixHyphens || spellingHints == null || word.Length < 4 || word.Any(char.IsAsciiLetter) ||
            !CorrectionRules.IsCandidate(word, ignored) || !await HasSpellingErrorAsync(word, token)) return word;
        var candidates = new List<string>();
        for (var index = 2; index <= word.Length - 2; index++)
        {
            var left = word[..index];
            var right = word[index..];
            if (ignored.Contains(left) || ignored.Contains(right)) continue;
            if ((await spellingHints.CheckAsync(left, token).ConfigureAwait(false))?.HasError != false ||
                (await spellingHints.CheckAsync(right, token).ConfigureAwait(false))?.HasError != false) continue;
            candidates.Add(left + " " + right);
        }
        if (candidates.Count == 0) return word;
        var selected = (await AskAsync(Settings.SpellingChoiceInstructions, string.Join("\n", new[] { word }.Concat(candidates)), token, 64))?.Trim();
        var proposed = candidates.FirstOrDefault(value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase));
        if (proposed == null) return word;
        var spelling = await AskSpellingAsync(proposed, token, 64);
        if (!string.Equals(spelling?.Trim(), proposed, StringComparison.OrdinalIgnoreCase)) return word;
        foreach (var part in proposed.Split(' '))
            if ((await AskAsync(Settings.WordValidityInstructions, part, token))?.Trim() != "ДА") return word;
        return proposed;
    }

    private Task<string?> AskSpellingAsync(string input, CancellationToken token, int maxTokens = 24) =>
        AskAsync(input.Any(char.IsAsciiLetter) && !input.Any(CorrectionRules.IsRussianLetter)
            ? Settings.EnglishSpellingInstructions : Settings.SpellingInstructions, input, token, maxTokens);

    private async Task<string?> AskAsync(string instructions, string input, CancellationToken cancellationToken, int maxTokens = 24)
    {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = (instructions, input, maxTokens);
            lock (responseLock)
                if (responseCache.TryGetValue(cacheKey, out var cachedResponse)) return cachedResponse;
            object request;
            string path;
            if (Settings.Backend == "Ollama")
            {
                path = "api/generate";
                var options = new Dictionary<string, object>
                {
                    ["temperature"] = 0, ["num_predict"] = maxTokens, ["num_ctx"] = Settings.RequiredContextTokens(),
                    ["num_thread"] = Settings.CpuThreads
                };
                if (Settings.CpuOnly) options["num_gpu"] = 0;
                request = new { model = Settings.Model, system = instructions, prompt = input,
                    stream = false, think = false, keep_alive = "15m", options };
            }
            else
            {
                path = "v1/chat/completions";
                request = new { model = Settings.Model, messages = new[] {
                    new { role = "system", content = instructions }, new { role = "user", content = input } },
                    stream = false, temperature = 0, max_tokens = maxTokens,
                    chat_template_kwargs = new { enable_thinking = false } };
            }
            using var response = await http.PostAsJsonAsync(path, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Модель или адрес API не найдены. Скачайте модель и проверьте настройки."
                    : $"Локальный сервер вернул HTTP {(int)response.StatusCode}.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string? raw;
            try
            {
                if (Settings.Backend == "Ollama")
                {
                    if (json.RootElement.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length") return null;
                    raw = json.RootElement.GetProperty("response").GetString();
                }
                else
                {
                    var choice = json.RootElement.GetProperty("choices")[0];
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length") return null;
                    raw = choice.GetProperty("message").GetProperty("content").GetString();
                }
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            { throw new JsonException("Локальный сервер вернул ответ неизвестного формата.", e); }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(raw))
                lock (responseLock)
                {
                    if (!responseCache.ContainsKey(cacheKey))
                    {
                        if (responseCache.Count >= 512) responseCache.Remove(responseOrder.Dequeue());
                        responseCache.Add(cacheKey, raw);
                        responseOrder.Enqueue(cacheKey);
                    }
                }
            return raw;
    }

    public void Dispose() => http.Dispose();
}
