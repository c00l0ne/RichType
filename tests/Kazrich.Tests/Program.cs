using System.Net;
using System.Text;
using System.Text.Json;
using Kazrich.Core;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
var ignored = new Settings().WordSet();
foreach (var word in new[] { "NL100", "3bet", "AKs", "https://x", "test_name", "pass123", "HelloWorld", "пpивет", "CPU", "kazrich", "a" })
    Check(!CorrectionRules.IsCandidate(word, ignored), "skip protected token " + word);
Check(CorrectionRules.IsCandidate("првиет", ignored), "Russian candidate");
Check(CorrectionRules.IsCandidate("helllo", ignored), "English candidate");
Check(CorrectionRules.Validate("Првиет", "привет") == "Привет", "preserve title case");
Check(CorrectionRules.Validate("првиет", "привет") == "привет", "adjacent transposition");
Check(CorrectionRules.Validate("recieve", "receive") == "receive", "English transposition");
Check(CorrectionRules.Validate("helllo", "hello") == "hello", "repeated letter");
foreach (var raw in new[] { "hello", "Исправление: привет", "привет мир", "\"привет\"", "<think>x</think>привет", "программа", "привет!", "" })
    Check(CorrectionRules.Validate("првиет", raw) == null, "reject unsafe model output " + raw);
Check(CorrectionRules.Validate("мир", "Мир") == "мир", "no gratuitous capitalization");
Check(CorrectionRules.Validate("thnaks", "thank") == null, "reject unnecessary second edit");
var tracker = new WordTracker();
foreach (var c in "NL100") tracker.Feed(c);
Check(tracker.Feed(' ') == null, "never correct suffix of alphanumeric token");
foreach (var c in "првиет") tracker.Feed(c);
Check(tracker.Feed(' ') == "првиет", "word after delimiter");
foreach (var c in "hello@world") tracker.Feed(c);
Check(tracker.Feed(' ') == null, "email protection");
foreach (var c in "helllo") tracker.Feed(c);
tracker.Backspace(); tracker.Feed('o');
Check(tracker.Feed('.') == "helllo", "backspace within tracked word");
tracker.Reset(); tracker.Backspace(); tracker.Feed('x');
Check(tracker.Feed(' ') == null, "unknown deletion invalidates token");
foreach (var url in new[] { "https://example.com", "http://localhost:11434/path", "http://user:pass@localhost:11434", "file:///tmp/x" })
{
    var rejected = false;
    try { (new Settings { Endpoint = url }).Validate(); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "reject non-local or invalid endpoint " + url);
}
var handler = new FakeHandler();
var ordinaryContext = new Settings().RequiredContextTokens();
Check(ordinaryContext == 2048, "default editable instructions fit the small managed context");
var largeInstruction = new string('ж', 8000);
foreach (var contextSettings in new[] {
    new Settings { SpellingInstructions = largeInstruction }, new Settings { LayoutInstructions = largeInstruction },
    new Settings { EnglishSpellingInstructions = largeInstruction },
    new Settings { PhraseLayoutInstructions = largeInstruction }, new Settings { ContextLayoutInstructions = largeInstruction },
    new Settings { HyphenInstructions = largeInstruction }, new Settings { WordValidityInstructions = largeInstruction },
    new Settings { SpellingChoiceInstructions = largeInstruction } })
{
    contextSettings.Validate();
    Check(contextSettings.RequiredContextTokens() >= Encoding.UTF8.GetByteCount(largeInstruction) + 512 &&
        contextSettings.RequiredContextTokens() <= 32768, "every instruction field can enlarge the bounded model context");
}
var largeInstructionHandler = new FakeHandler();
using (var largeInstructionClient = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false,
    SpellingInstructions = largeInstruction }, largeInstructionHandler))
{
    await largeInstructionClient.CorrectAsync("првиет", CancellationToken.None);
    using var largeBody = JsonDocument.Parse(largeInstructionHandler.LastBody!);
    Check(largeBody.RootElement.GetProperty("system").GetString() == largeInstruction &&
        largeBody.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32() == 32768,
        "Ollama receives the entire long instruction and its enlarged context");
}
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, handler))
{
    var first = await client.CorrectAsync("првиет", CancellationToken.None);
    Check(first.Replacement == "привет" && !first.Cached, "parse actual Ollama wire format");
    var second = await client.CorrectAsync("првиет", CancellationToken.None);
    Check(second.Cached && handler.Calls == 1, "cache prevents repeat inference");
    using var body = JsonDocument.Parse(handler.LastBody!);
    Check(body.RootElement.GetProperty("think").ValueKind == JsonValueKind.False, "thinking disabled");
    Check(body.RootElement.GetProperty("options").GetProperty("num_gpu").GetInt32() == 0, "CPU-only request");
    Check(body.RootElement.GetProperty("options").GetProperty("num_thread").GetInt32() == 2, "CPU thread budget");
    await client.CorrectAsync("kazrich", CancellationToken.None);
    Check(handler.Calls == 1, "ignore list never reaches model");
}
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, new FakeHandler { Reply = "{\"response\":\"привет\",\"done_reason\":\"length\"}" }))
    Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == null, "reject truncated output");
foreach (var reply in new[] { "{}", "{\"response\":42}", "{\"choices\":[]}" })
{
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, new FakeHandler { Reply = reply });
    var rejected = false;
    try { await client.CorrectAsync("првиет", CancellationToken.None); } catch (JsonException) { rejected = true; }
    Check(rejected, "malformed server response is handled: " + reply);
}
using (var client = new ModelClient(new Settings { Backend = "OpenAI" }, new FakeHandler { Reply = "{\"choices\":[{\"message\":{\"content\":\"привет\"},\"finish_reason\":\"stop\"}]}" }))
    Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет", "local OpenAI protocol");
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, new FakeHandler { Delay = 500 }))
{
    using var cancel = new CancellationTokenSource(30);
    var cancelled = false;
    try { await client.CorrectAsync("првиет", cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "stale inference is cancellable");
    Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет", "cancellation releases request gate");
}
Check(KeyboardLayout.Convert("ghbdtn") == "привет", "generic keyboard mapping");
Check(KeyboardLayout.Convert("руддщ") == "hello", "reverse keyboard mapping");
Check(KeyboardLayout.AdjacentKeyTypos("хоты").Contains("хотя") &&
    KeyboardLayout.AdjacentKeyTypos("Хоты").Contains("Хотя"), "neighbouring physical keys provide a case-preserving spelling candidate");
Check(!KeyboardLayout.AdjacentKeyTypos("дела").Contains("дело") &&
    !KeyboardLayout.AdjacentKeyTypos("замены").Contains("замен"), "physical-key candidates exclude distant keys and deleted inflections");
foreach (var sample in new[] { "хоты", "Хоты", "time", "Time", "ёжик" })
    Check(KeyboardLayout.AdjacentKeyTypos(sample).All(value => value.Length == sample.Length && value.All(CorrectionRules.IsLetter) &&
        CorrectionRules.DamerauDistance(sample, value) == 1), "key candidates make one bounded letter substitution: " + sample);
Check(KeyboardLayout.ConvertedWords("[jns ,s ", "хоты бы ", ignored).SequenceEqual(["хоты"]),
    "only exact completed layout conversions supply later spelling provenance");
foreach (var (original, replacement) in new[] { ("хоты ", "хоты "), ("хоты ", "хотя "),
    ("[jns.com", "хоты"), ("[jns extra", "хоты"), ("[jns", "хотя"), ("[JNS", "ХОТЫ") })
    Check(!KeyboardLayout.ConvertedWords(original, replacement, ignored).Any(),
        "layout provenance excludes unchanged words, spelling changes and protected or misaligned text: " + original);
Check(!KeyboardLayout.ConvertedWords("[jns", "хоты", new HashSet<string>(["хоты"])).Any(),
    "mapped exclusions never supply spelling provenance");
Check(!KeyboardLayout.ConvertedWords("[jns хоты", "хоты хоты", ignored).Any(),
    "a literal duplicate must not inherit the converted word's spelling provenance");
foreach (var (key, letter) in new[] { ('{', 'Х'), ('}', 'Ъ'), (':', 'Ж'), ('\"', 'Э'), ('<', 'Б'), ('>', 'Ю'), ('~', 'Ё') })
{
    Check(KeyboardLayout.Convert(key.ToString()) == letter.ToString() && KeyboardLayout.Convert(letter.ToString()) == key.ToString(),
        "Shift-modified punctuation keys map reversibly to uppercase Russian letters: " + key);
    Check(KeyboardLayout.IsLatinLetterKey(key), "Shift punctuation participates in complete keyboard words: " + key);
}
Check(KeyboardLayout.Normalize("{jчу", false) == "Хочу" && KeyboardLayout.Normalize("Хочу", true) == "{jxe",
    "mixed normalization preserves Shift state for Russian punctuation letter keys");
Check(CorrectionRules.IsCandidate("Ёлка", ignored) && CorrectionRules.IsCandidate("Ёлочка", ignored),
    "uppercase Ё belongs to the Russian alphabet when filtering words");
Check(CorrectionRules.IsMixedCandidate("Ёkrf", ignored) && CorrectionRules.IsMixedCandidate("Ёn", ignored),
    "uppercase Ё participates in mixed-layout words including short ones");
Check(CorrectionRules.Validate("Ёлкка", "Ёлка") == "Ёлка",
    "spelling validation preserves a capital Ё at the start of a Russian word");
foreach (var validSuffix in new[] { true, false })
{
    var leadingHints = new FakeSpellingHints(word => word == "{очу" ? null : new(word == "очу" && !validSuffix, []));
    var leadingModel = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var instruction = request.RootElement.GetProperty("system").GetString();
        return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? "ДА" : prompt.Split('\n')[0] });
    } };
    using var leadingClient = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, leadingModel, leadingHints);
    Check(await leadingClient.CorrectPhraseAsync("{очу", CancellationToken.None) == (validSuffix ? "{очу" : "Хочу"),
        "single leading letter key needs an invalid literal suffix when layout selection is inconclusive: " + validSuffix);
}
foreach (var consistent in new[] { false, true })
{
    var sequence = new FakeHandler { Replies = new Queue<string>((consistent
        ? new[] { "привет", "привет" }
        : new[] { "привет", "ghbdtn", "привет", "ghbdtn" })
        .Select(word => JsonSerializer.Serialize(new { response = word }))) };
    using var client = new ModelClient(new Settings { Backend = "Ollama", SpellingInstructions = "Custom spelling", LayoutInstructions = "Custom layout" }, sequence);
    var result = await client.CorrectAsync("ghbdtn", CancellationToken.None);
    Check(result.Replacement == (consistent ? "привет" : "ghbdtn"), "layout requires consistent model choice: " + consistent);
    using var request = JsonDocument.Parse(sequence.LastBody!);
    Check(request.RootElement.GetProperty("system").GetString() == Settings.DefaultWordValidityInstructions, "editable instruction sent for the remaining check");
}
var editedHandler = new FakeHandler();
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, SpellingInstructions = "Custom spelling" }, editedHandler))
{
    await client.CorrectAsync("првиет", CancellationToken.None);
    using var request = JsonDocument.Parse(editedHandler.LastBody!);
    Check(request.RootElement.GetProperty("system").GetString() == "Custom spelling", "editable spelling instruction sent to model");
}
var phraseTracker = new PhraseTracker();
string? pendingPhrase = null;
foreach (var c in "f vj;tn ,snm ") pendingPhrase = phraseTracker.Feed(c);
Check(pendingPhrase == "f vj;tn ,snm ", "continued typing retains previous words and layout punctuation");
phraseTracker.Replaced("а может быть ");
foreach (var c in "d ") pendingPhrase = phraseTracker.Feed(c);
Check(pendingPhrase == "а может быть d ", "tracking follows corrected text");
phraseTracker.Reset(); phraseTracker.Backspace();
foreach (var c in "unknown ") pendingPhrase = phraseTracker.Feed(c);
Check(pendingPhrase == null, "unknown deletion invalidates pending phrase");
var phraseHandler = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
        (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt :
        prompt.Split('\n').FirstOrDefault(line => line == "а может" || line == "может") ?? "может";
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, phraseHandler))
    Check(await client.CorrectPhraseAsync("f vj;tn ", CancellationToken.None) == "а может ", "phrase mapping includes short words and punctuation keys");
var protectedHandler = new FakeHandler();
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, protectedHandler))
{
    Check(await client.CorrectPhraseAsync("NL100 kazrich ", CancellationToken.None) == "NL100 kazrich ", "protected phrase unchanged");
    Check(protectedHandler.Calls == 0, "protected tokens never sent for phrase inference");
}
var combinedHandler = new FakeHandler { Replies = new Queue<string>(new[] { "gjqltnv", "пойдет", "пойдет", "пойдет" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, combinedHandler))
    Check((await client.CorrectAsync("gjqltnv", CancellationToken.None)).Replacement == "пойдет", "layout and spelling correction compose without word-specific rules");
Check(KeyboardLayout.Normalize("пойдеv", false) == "пойдем", "normalize only Latin letters to Russian keys");
Check(KeyboardLayout.Normalize("hellщ", true) == "hello", "normalize only Russian letters to English keys");
foreach (var accepted in new[] { true, false })
{
    var mixedEnding = new FakeHandler { Respond = body =>
    {
        using var request = JsonDocument.Parse(body);
        var prompt = request.RootElement.GetProperty("prompt").GetString();
        var reply = prompt switch
        {
            "начинаетcz\nначинается" => "начинается",
            "начинается\nначинаетcz" => "начинает",
            "yfxbyftncz\nначинается" => "начинается",
            "начинается\nyfxbyftncz" => accepted ? "начинается" : "yfxbyftncz",
            "начинается" => "начинается",
            _ => "начинаетcz"
        };
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, mixedEnding);
    Check((await client.CorrectAsync("начинаетcz", CancellationToken.None)).Replacement == (accepted ? "начинается" : null),
        accepted ? "complete layouts recover a mixed ending after a truncated response" : "contradictory complete layouts cannot change a mixed ending");
}
Check(CorrectionRules.IsMixedCandidate("пойдеv", ignored), "mixed word allowed for explicit layout check");
foreach (var word in new[] { "NL100", "HelloWorld", "пойдеV", "test_name", "pass123" })
    Check(!CorrectionRules.IsMixedCandidate(word, ignored), "mixed-word path preserves protected token: " + word);
var mixedHandler = new FakeHandler { Replies = new Queue<string>(new[] { "пойдем", "пойдем", "пойдем", "пойдем" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, mixedHandler))
    Check(await client.CorrectPhraseAsync("пойдеv, ", CancellationToken.None) == "пойдем, ", "mixed layout corrected while punctuation preserved");
var disabledMixed = new FakeHandler();
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, disabledMixed))
{
    Check(await client.CorrectPhraseAsync("пойдеv ", CancellationToken.None) == "пойдеv ", "layout toggle disables mixed correction");
    Check(disabledMixed.Calls == 0, "disabled mixed correction makes no requests");
}
var queue = new TypingQueue();
foreach (var c in "hello ") queue.Append(c);
var queuedBatch = queue.Next()!;
foreach (var c in "world") queue.Append(c);
Check(queue.Matches(queuedBatch), "typing after a completed batch preserves it");
queue.Consume(queuedBatch);
Check(queue.Text == "world" && queue.Next() == null, "consuming batch retains unfinished tail");
queue.Append('\n');
queuedBatch = queue.Next()!;
Check(queuedBatch.Text == "world" && queuedBatch.Consumed == 6, "Enter boundary is consumed but never included in replacement");
queue.Consume(queuedBatch);
Check(queue.Text == "", "completed newline removed from queue");
foreach (var c in string.Concat(Enumerable.Repeat("hello ", 80))) queue.Append(c);
Check(queue.Text.Length == 480 && queue.Next()!.Consumed <= 160, "long backlog is retained and processed in bounded batches");
queue.Reset();
foreach (var c in "vj;") queue.Append(c);
Check(queue.Next() == null, "a punctuation letter key waits for the rest of the word before idle");
queuedBatch = queue.Next(includeUnfinished: true)!;
foreach (var c in "tn") queue.Append(c);
Check(!queue.Matches(queuedBatch), "punctuation inside a continued word invalidates obsolete batch");
queue.Reset();
foreach (var c in ",.") queue.Append(c);
Check(queue.Next() == null, "leading punctuation letter keys are not consumed before letters arrive");
foreach (var c in "l;tnf ") queue.Append(c);
Check(queue.Next()?.Text == ",.l;tnf ", "leading punctuation remains attached to the completed keyboard word");
queue.Reset();
foreach (var c in "сейчас ltkf. ра") queue.Append(c);
Check(queue.Next()?.Text == "сейчас ", "an ambiguous terminal key waits with its unfinished right neighbour");
Check(queue.Next(includeUnfinished: true)?.Text == "сейчас ltkf. ра", "idle can resolve a terminal key without another delimiter");
foreach (var c in "боту ") queue.Append(c);
Check(queue.Next()?.Text == "сейчас ltkf. работу ", "a completed right neighbour joins the ambiguous terminal word in one batch");
queue.Reset();
foreach (var c in "ltkf.\nработу") queue.Append(c);
Check(queue.Next() is { Text: "ltkf.", Consumed: 6 }, "Enter finishes an ambiguous terminal word without crossing the line");
queue.Reset();
foreach (var c in "ghbdtn!") queue.Append(c);
Check(queue.Next()?.Text == "ghbdtn!", "unambiguous terminal punctuation remains an immediate boundary");
queue.Reset();
foreach (var c in "ltkf. " + new string('x', 160)) queue.Append(c);
Check(queue.Next()?.Text == "ltkf. ", "an oversized following token cannot stall the completed queue prefix");
queue.Reset();
for (var i = 0; i < TypingQueue.Capacity; i++) queue.Append('x');
Check(!queue.Append('x'), "queue overflow explicitly reported");
Check(TypingQueue.Normalize("one\r\ntwo\rthree") == "one\ntwo\nthree", "normalize Windows and native newlines");
queue.Reset();
foreach (var c in "f ") queue.Append(c);
Check(queue.Next() == null, "isolated short layout token waits for a neighbour");
foreach (var c in "vj;tn ") queue.Append(c);
Check(queue.Next()!.Text == "f vj;tn ", "short layout token stays with the following word");
foreach (var (source, replacement, prefix) in new[] {
    ("cnegjh e ", "ступор e ", "cnegjh "),
    ("cnegjh e", "ступор e", "cnegjh "),
    ("Pyftnt kb  ", "Знаете kb  ", "Pyftnt "),
    ("ghbdtn to\t", "привет to\t", "ghbdtn "),
    ("cnegjh e ", "ступор у ", "cnegjh e "),
    ("cnegjh e ", "cnegjh e ", "cnegjh e "),
    ("cnegjh e, ", "ступор e, ", "cnegjh e, "),
    ("ghbdtn KB ", "привет KB ", "ghbdtn KB "),
    ("ghbdtn the ", "привет the ", "ghbdtn the "),
    ("какому то", "какому-то", "какому то"),
    ("что т", "черт", "что т"),
    (" e ", "что e ", " e "),
    ("e ", "у ", "e ") })
{
    var complete = new TypingBatch(source, source.Length, !char.IsWhiteSpace(source[^1]));
    var deferred = complete.DeferUnchangedShortWord(replacement);
    Check(deferred.Text == prefix && deferred.Consumed == prefix.Length &&
        (prefix == source || !deferred.Unfinished), "defer only an unchanged complete short ending: " + source);
    if (prefix != source)
    {
        queue.Reset(); foreach (var c in source + "yjdbxrjd") queue.Append(c);
        Check(queue.Matches(deferred), "the corrected prefix still matches before deferred input");
        queue.Consume(deferred);
        Check(queue.Text == source[prefix.Length..] + "yjdbxrjd", "deferral retains the exact short word and later input");
    }
}
var lineEndedShortWord = new TypingBatch("cnegjh e", "cnegjh e".Length + 1);
Check(lineEndedShortWord.DeferUnchangedShortWord("ступор e") == lineEndedShortWord,
    "Enter consumes the line instead of reusing a short word across its boundary");
var editedWord = EditedWord.Around("а может пойltv", " в супермаркет")!;
Check(editedWord.Word == "пойltv" && editedWord.CaretOffset == 6 && editedWord.After == " в супермаркет", "capture complete edited word with later text intact");
editedWord = EditedWord.Around("а может по", "йltv в супермаркет")!;
Check(editedWord.Word == "пойltv" && editedWord.CaretOffset == 2 && editedWord.CaretAfter("пойдем") == 2, "capture both sides of caret inside a word");
Check(EditedWord.Around("hello ", "world", true)!.Word == "world", "deletion at word start selects the word to the right");
Check(EditedWord.Around("hello ", "world")!.Word == "hello", "typed delimiter selects the preceding word");
Check(EditedWord.Around("prefix_helllo", " ")!.Word == "prefix_helllo", "editing never extracts an identifier suffix as a standalone word");
var misleadingSpelling = new FakeHandler { Replies = new Queue<string>(new[] { "цене", "цене" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, misleadingSpelling))
    Check((await client.CorrectAsync("wtyt", CancellationToken.None)).Replacement == "цене", "spelling proposal does not hide a confirmed keyboard-layout correction");
Check(misleadingSpelling.Calls == 3, "confirmed raw layout checks original validity without unnecessary spelling queries");
queue.Reset();
foreach (var c in "hello") queue.Append(c);
var idleBatch = queue.Next(includeUnfinished: true)!;
Check(queue.Next() == null && idleBatch.Text == "hello" && idleBatch.Unfinished, "idle batch includes final word without inventing a separator");
queue.Append('x');
Check(!queue.Matches(idleBatch), "continued word invalidates idle result");
queue.Reset(); foreach (var c in "f ") queue.Append(c);
Check(queue.Next(includeUnfinished: true) == null, "idle does not discard isolated short word before context arrives");
foreach (var input in new[] { ",s", ";t", "<s", ":t" })
{
    queue.Reset(); foreach (var c in input) queue.Append(c);
    Check(queue.Next() == null && queue.Next(includeUnfinished: true) is { Unfinished: true } shortBatch && shortBatch.Text == input,
        "idle schedules a complete short punctuation-key word without consuming it during typing: " + input);
}
foreach (var input in new[] { "a", "e", "ab" })
{
    queue.Reset(); foreach (var c in input) queue.Append(c);
    Check(queue.Next(includeUnfinished: true) == null, "short keyboard scheduling preserves isolated letters and protected fragments: " + input);
}
foreach (var input in new[] { "x/", "a_" })
{
    queue.Reset(); foreach (var c in input) queue.Append(c);
    Check(queue.Next(includeUnfinished: true) is { Unfinished: false } literalBatch && literalBatch.Text == input,
        "non-keyboard punctuation keeps its existing completed-boundary behavior: " + input);
}
queue.Reset(); foreach (var c in ",s ") queue.Append(c);
Check(queue.Next(includeUnfinished: true) is { Text: ",s ", Unfinished: false },
    "a space-terminated short keyboard word retains its delimiter and completed state");
var contextual = new FakeHandler { Replies = new Queue<string>(new[] { "а что", "а что" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama", IgnoredWords = "что" }, contextual))
    Check(await client.CorrectPhraseAsync("f что", CancellationToken.None) == "а что", "single-letter layout correction uses neighbours of a different alphabet");
var fastPhrase = new FakeHandler { Replies = new Queue<string>(new[] { "но исправляет", "но исправляет", "исправляет", "но исправляет" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, fastPhrase))
{
    Check(await client.CorrectPhraseAsync("yj bcghfdkztn", CancellationToken.None) == "но исправляет" && fastPhrase.Calls == 6,
        "confirmed phrase checks spelling and contextual transposition after two layout decisions");
    Check(await client.CorrectPhraseAsync("yj bcghfdkztn", CancellationToken.None) == "но исправляет" && fastPhrase.Calls == 6,
        "identical phrase decisions reuse bounded response cache");
}
var contextOnly = new FakeHandler { Replies = new Queue<string>(new[] { "а почему сразу", "а почему сразу", "сразу", "почему сразу" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, contextOnly))
    Check(await client.CorrectPhraseAsync("chfpe", CancellationToken.None, "а почему ") == "сразу",
        "previously corrected context chooses layout without returning or rewriting that context");
foreach (var acceptRecheck in new[] { true, false })
{
    var newRightContext = new FakeHandler { Respond = body =>
    {
        using var request = JsonDocument.Parse(body);
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var instructions = request.RootElement.GetProperty("system").GetString();
        var reply = prompt.Split('\n')[0];
        if (instructions == Settings.DefaultWordValidityInstructions && prompt == "пробный") reply = "ДА";
        if (prompt == "ntrcn\nтекст") reply = "текст";
        if (prompt == "текст\nntrcn") reply = "ntrcn";
        if (prompt is "ghj,ysq\nпробный" or "пробный\nghj,ysq") reply = "пробный";
        if (instructions == Settings.DefaultContextLayoutInstructions && prompt == "ntrcn пробный\nтекст пробный") reply = "текст пробный";
        if (instructions == Settings.DefaultContextLayoutInstructions && prompt == "текст пробный\nntrcn пробный")
            reply = acceptRecheck ? "текст пробный" : "ntrcn пробный";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, newRightContext);
    Check(await client.CorrectPhraseAsync("ntrcn ghj,ysq", CancellationToken.None) == (acceptRecheck ? "текст пробный" : "ntrcn пробный"),
        acceptRecheck ? "a newly corrected right neighbour resolves an earlier word" : "rechecking new right context still rejects contradictory choices");
}
foreach (var firstBias in new[] { false, true })
foreach (var decision in new[] { "accept", "reject-forward", "reject-reverse" })
{
    var disputedContext = new FakeHandler { Respond = body =>
    {
        using var request = JsonDocument.Parse(body);
        var prompt = request.RootElement.GetProperty("prompt").GetString();
        var instructions = request.RootElement.GetProperty("system").GetString();
        var reply = "phz";
        if (instructions == "Context choice")
            reply = prompt!.Split('\n')[firstBias ? 0 : 1];
        else if (instructions == "Phrase choice")
            reply = prompt == "время phz\nвремя зря"
                ? decision == "reject-forward" ? "время phz" : "время зря"
                : decision == "reject-reverse" ? "время phz" : "время зря";
        else if (prompt == "зря") reply = "зря";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false,
        ContextLayoutInstructions = "Context choice", PhraseLayoutInstructions = "Phrase choice" }, disputedContext);
    Check(await client.CorrectPhraseAsync("phz", CancellationToken.None, "время ") == (decision == "accept" ? "зря" : "phz"),
        "inconsistent context requires both editable phrase comparisons: " + firstBias + "/" + decision);
}
var retainedContext = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    return JsonSerializer.Serialize(new { response = instruction == "Keep context" ? "время phz" :
        instruction == "Would change context" ? "время зря" : prompt.Split('\n')[0] });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false,
    ContextLayoutInstructions = "Keep context", PhraseLayoutInstructions = "Would change context" }, retainedContext))
    Check(await client.CorrectPhraseAsync("phz", CancellationToken.None, "время ") == "phz",
        "a consistent decision to preserve the original context is not overridden by a fallback");
var preservedInflection = new FakeHandler { Replies = new Queue<string>(new[] { "замены", "замены" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))), Reply = JsonSerializer.Serialize(new { response = "замен" }) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, preservedInflection))
    Check((await client.CorrectAsync("pfvtys", CancellationToken.None)).Replacement == "замены" && preservedInflection.Calls == 3,
        "valid raw layout keeps its ending instead of accepting a harmful spelling edit");
var phraseInflection = new FakeHandler { Replies = new Queue<string>(new[]
    { "скорость замены", "скорость замены", "скорость", "замен", "замены", "замены", "скорость замены", "замены", "скорость замены" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, phraseInflection))
    Check(await client.CorrectPhraseAsync("crjhjcnm pfvtys", CancellationToken.None) == "скорость замены",
        "phrase-level layout confirmation also preserves a valid inflection from a harmful spelling proposal");
Check(CorrectionRules.IsMixedCandidate("нf", ignored), "two-letter mixed word is eligible for layout correction");
Check(!CorrectionRules.IsMixedCandidate("нF", ignored) && !CorrectionRules.IsMixedCandidate("нf", new HashSet<string>{ "нf" }),
    "short mixed words retain uppercase and ignore-list protection");
queue.Reset(); foreach (var c in "нf") queue.Append(c);
Check(queue.Next(includeUnfinished: true)?.Text == "нf", "idle checks a two-letter mixed word without a separator");
var shortMixedHandler = new FakeHandler { Replies = new Queue<string>(new[] { "на", "на" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, shortMixedHandler))
    Check(await client.CorrectPhraseAsync("нf", CancellationToken.None) == "на", "two-letter mixed word reaches the normal model confirmation path");
Check(EditedWord.Around("можно нf", " каком") is { Word: "нf", CaretOffset: 2, After: " каком" },
    "manual-edit capture includes a two-letter word and preserves following context");
var rightContext = new FakeHandler { Replies = new Queue<string>(new[] { "зам начальника", "зам начальника", "зам", "зам начальника" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama", ContextLayoutInstructions = "Custom context instruction" }, rightContext))
    Check(await client.CorrectPhraseAsync("pfv", CancellationToken.None, "", " начальника") == "зам" && rightContext.Calls == 6,
        "right context confirms layout in both orders while only the edited word is returned");
using (var body = JsonDocument.Parse(rightContext.LastBody!))
    Check(body.RootElement.GetProperty("system").GetString() == Settings.DefaultSpellingInstructions,
        "the contextual spelling check uses the configured spelling instruction");
queue.Reset(); foreach (var c in "nj") queue.Append(c);
Check(queue.Next(includeUnfinished: true) == null && queue.Next(includeUnfinished: true, hasContext: true)?.Text == "nj",
    "short final word is checked when preceding language context is available");
queue.Append(' ');
Check(queue.Next(hasContext: true)?.Text == "nj ", "completed short word uses preceding context instead of waiting for another neighbour");
Check(Hyphenation.ContextSuffix("праздник какой ") == "какой " && Hyphenation.ContextSuffix("text test ") == "",
    "hyphenation expands only the last Russian word of verified context");
Check(Hyphenation.Validate("какой то", "какой-то", ignored) == "какой-то", "hyphenation permits replacing a word boundary only");
foreach (var proposal in new[] { "какой-то!", "Какой-то", "какойтот", "какая-то", "какой  то", "какой\nто" })
    Check(Hyphenation.Validate("какой то", proposal, ignored) == "какой то", "hyphenation rejects changes to letters or other spacing: " + proposal);
Check(Hyphenation.Validate("какой то", "какой-то", new HashSet<string> { "какой" }) == "какой то", "hyphenation honours ignored words");
Check(Hyphenation.Boundaries("abc_какой то 123какой то", ignored).Count == 0, "hyphenation does not extract words from protected identifiers");
var hyphenHandler = new FakeHandler { Reply = JsonSerializer.Serialize(new { response = "какой-то" }) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, hyphenHandler))
    Check(await client.CorrectHyphensAsync(" какой то ", CancellationToken.None) == " какой-то ", "hyphenation preserves outer whitespace");
var disabledHyphenHandler = new FakeHandler();
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, disabledHyphenHandler))
    Check(await client.CorrectHyphensAsync("какой то", CancellationToken.None) == "какой то" && disabledHyphenHandler.Calls == 0,
        "disabled hyphenation makes no model request");
Check(Hyphenation.Validate("както", "как-то", ignored) == "как-то", "missing internal hyphen is a valid spelling correction");
foreach (var proposal in new[] { "как--то", "как-то!", "Как-то", "ка-то", "както-", "как то" })
    Check(Hyphenation.Validate("както", proposal, ignored) == "както", "internal hyphen validation rejects other edits: " + proposal);
Check(Hyphenation.Insertions("abc_както КАКТО какТо", ignored).Count == 0 &&
    Hyphenation.Insertions("както", new HashSet<string> { "както" }).Count == 0,
    "internal hyphen insertion skips identifiers, acronyms, mixed case and ignored words");
var insertedHyphen = new FakeHandler { Replies = new Queue<string>(new[] { "как-то", "как-то", "как-то", "как-то" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, insertedHyphen))
    Check(await client.CorrectHyphensAsync("както", CancellationToken.None) == "как-то" && insertedHyphen.Calls == 5,
        "model-selected hyphen position is confirmed in both candidate orders");
foreach (var spelling in new[] { "урока", "урок", "Ответ: уро-к", "" })
{
    var artificialSplit = new FakeHandler { Replies = new Queue<string>(new[] { "уро-к", "уро-к", "уро-к", spelling }
        .Select(word => JsonSerializer.Serialize(new { response = word }))) };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, artificialSplit);
    Check(await client.CorrectHyphensAsync("урок", CancellationToken.None) == "урок",
        "spelling review rejects a consistently selected artificial split: " + spelling);
}
var preservedBeforeHyphen = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
        (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt : "уро-к" });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, preservedBeforeHyphen))
    Check(await client.CorrectHyphensAsync("урок", CancellationToken.None) == "урок",
        "recognized intact word is protected even if all candidate checks would accept an artificial hyphen");
var disputedHyphen = new FakeHandler { Replies = new Queue<string>(new[] { "при-вет", "при-вет", "привет" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, disputedHyphen))
    Check(await client.CorrectHyphensAsync("привет", CancellationToken.None) == "привет",
        "inconsistent model choice cannot insert a hyphen into a correct word");
Check(!Hyphenation.Insertions("нибудь", ignored).Contains(5) &&
    Hyphenation.Validate("нибудь", "нибуд-ь", ignored) == "нибудь", "a hyphen cannot introduce a component beginning with a soft sign");
Check(Hyphenation.Validate("объект", "об-ъект", ignored) == "объект" &&
    Hyphenation.Validate("слово ь", "слово-ь", ignored) == "слово ь", "hard and soft signs cannot begin a new hyphenated component");
var joinedParticle = new FakeHandler { Replies = new Queue<string>(new[] { "какому-нибудь" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, joinedParticle))
    Check(await client.CorrectHyphensAsync("какому нибудь", CancellationToken.None) == "какому-нибудь" && joinedParticle.Calls == 1,
        "joined particles are not subsequently split by isolated-word candidate generation");
Check(Hyphenation.Validate("какому нибудь", "какому ни-будь", ignored, allowInsertions: false) == "какому нибудь",
    "the word-boundary stage cannot insert an internal hyphen instead of joining the pair");
var hyphenLayout = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var reply = instruction == Settings.DefaultHyphenInstructions ? "какой-нибудь" :
        instruction == Settings.DefaultContextLayoutInstructions ?
            (prompt.Contains("какой-нибудь") ? "какой-нибудь" : "какой yb,elm") :
        instruction == Settings.DefaultWordValidityInstructions ? "НЕИЗВЕСТНО" : prompt.Split('\n')[0];
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, hyphenLayout))
    Check(await client.CorrectPhraseAsync("yb,elm", CancellationToken.None, "какой ") == "нибудь",
        "hyphenated alternative can confirm layout without returning or rewriting the preceding context");
foreach (var decision in new[] { "accept", "reject-forward", "reject-reverse", "spelling", "validity", "native",
    "phrase-accept", "phrase-reject-forward", "phrase-reject-reverse" })
{
    var compoundLayout = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == "Compound spelling" ? (decision == "spelling" ? "Из за" : prompt) :
            instruction == "Compound validity" ? (decision == "validity" ? "НЕТ" : "ДА") :
            instruction == "Compound choice" ?
                (decision.StartsWith("phrase-") || decision == "reject-forward" && prompt.StartsWith("Bp-pf\n") ||
                 decision == "reject-reverse" && prompt.EndsWith("\nBp-pf") ? "Bp-pf" : "Из-за") : prompt;
        if (instruction == "Compound phrase" && decision.StartsWith("phrase-"))
            reply = decision == "phrase-reject-forward" && prompt.StartsWith("Bp-pf\n") ||
                decision == "phrase-reject-reverse" && prompt.EndsWith("\nBp-pf") ? "Bp-pf" : "Из-за";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", SpellingInstructions = "Compound spelling",
        WordValidityInstructions = "Compound validity", LayoutInstructions = "Compound choice", PhraseLayoutInstructions = "Compound phrase" }, compoundLayout,
        new FakeSpellingHints(word => new(decision == "native" && word == "Из-за", [])));
    Check(await client.CorrectPhraseAsync("Bp-pf", CancellationToken.None) == (decision is "accept" or "phrase-accept" ? "Из-за" : "Bp-pf"),
        "an existing compound requires unchanged whole spelling, recognition and both editable layout choices: " + decision);
}
foreach (var protectedCompound in new[] { "BP-PF", "Bp-Pf", "user_bp-pf", "bp-pf@example.com", "bp-pf.com",
    "https://example.com/bp-pf", "C:\\temp\\bp-pf", "bp-pf123", "-bp-pf", "bp--pf" })
{
    var compoundProtection = new FakeHandler();
    using var client = new ModelClient(new Settings { Backend = "Ollama", IgnoredWords = "bp, pf" }, compoundProtection);
    Check(await client.CorrectPhraseAsync(protectedCompound, CancellationToken.None) == protectedCompound,
        "compound layout preserves protected input and excluded components: " + protectedCompound);
}
foreach (var compoundSettings in new[] {
    new Settings { Backend = "Ollama", FixKeyboardLayout = false },
    new Settings { Backend = "Ollama", IgnoredWords = "Bp-pf" },
    new Settings { Backend = "Ollama", IgnoredWords = "bp" },
    new Settings { Backend = "Ollama", IgnoredWords = "Из-за" },
    new Settings { Backend = "Ollama", IgnoredWords = "за" } })
{
    var noCompoundCalls = new FakeHandler();
    using var client = new ModelClient(compoundSettings, noCompoundCalls);
    Check(await client.CorrectPhraseAsync("Bp-pf", CancellationToken.None) == "Bp-pf" && noCompoundCalls.Calls == 0,
        "compound layout respects the disabled setting and full or component exclusions");
}
var truncatedCompound = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
        instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions ? prompt :
        prompt.Contains("что-нибудь") ? "elm" :
        prompt.Contains("что-ни") ? "что-ни" : prompt.Split('\n')[0];
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, truncatedCompound,
    new FakeSpellingHints(_ => new(false, []))))
    Check(await client.CorrectPhraseAsync("xnj-yb,elm", CancellationToken.None) == "xnj-yb,elm",
        "a rejected complete compound cannot be split at its letter key and corrected as a truncated fragment");
foreach (var confirmSpelling in new[] { true, false })
{
    var mappedTypo = new FakeHandler { ValidityReply = "ДА", Replies = new Queue<string>(new[]
        { "ниубдь", "какой ниубдь", "какой ниубдь", "какой нибудь", "какой нибудь",
            confirmSpelling ? "какой нибудь" : "какой ниубдь" }
        .Select(word => JsonSerializer.Serialize(new { response = word }))) };
    using var client = new ModelClient(new Settings { Backend = "Ollama", IgnoredWords = "какой" }, mappedTypo);
    Check(await client.CorrectPhraseAsync("rfrjq ybe,lm", CancellationToken.None) ==
        (confirmSpelling ? "какой нибудь" : "какой ниубдь"),
        confirmSpelling ? "confirmed contextual transposition fixes a typo remaining after layout conversion" :
            "inconsistent contextual transposition is rejected");
}
Check(Hyphenation.Validate("архив и", "архив-и", ignored) == "архив и" &&
    Hyphenation.Validate("а ля", "а-ля", ignored) == "а-ля",
    "a single-letter conjunction is not joined to its preceding word while valid short first components remain eligible");
Check(CorrectionRules.IsMixedCandidate("при,skb", ignored), "mixed-layout candidates accept an internal Russian letter key shown as punctuation");
Check(CorrectionRules.IsMixedCandidate("[jчу", ignored), "a leading Russian letter key can begin a mixed-layout word");
foreach (var value in new[] { "@jчу", "/jчу", "_jчу", "[jЧу" })
    Check(!CorrectionRules.IsMixedCandidate(value, ignored), "leading-key support preserves token protection: " + value);
Check(!CorrectionRules.IsMixedCandidate("[jчу", new HashSet<string> { "[jчу" }), "leading-key support preserves ignored words");
foreach (var value in new[] { "[очу", "'то", ";иву", ",уду", ".бка", "`жик", "об]явление" })
    Check(CorrectionRules.IsMixedCandidate(value, ignored), "a Russian letter key alone establishes a layout difference: " + value);
var punctuationOnly = new FakeHandler { Replies = new Queue<string>(new[] { "это", "это", "это" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, punctuationOnly))
    Check((await client.CorrectAsync("'то", CancellationToken.None)).Replacement == "это" && punctuationOnly.Calls == 3,
        "a punctuation-only layout difference requires spelling and both direct comparisons");
foreach (var spellingAccepts in new[] { false, true })
{
    var literalBracket = new FakeHandler { Respond = body =>
    {
        using var request = JsonDocument.Parse(body);
        var prompt = request.RootElement.GetProperty("prompt").GetString();
        var reply = prompt == "хпривет" && spellingAccepts ? "хпривет" : "привет";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, literalBracket);
    Check((await client.CorrectAsync("[привет", CancellationToken.None)).Replacement == null && literalBracket.Calls == (spellingAccepts ? 2 : 1),
        "a possible literal bracket cannot be removed through normalized or spelling fallback: " + spellingAccepts);
}
var leadingKey = new FakeHandler { Replies = new Queue<string>(new[] { "хочу", "хочу" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, leadingKey))
    Check((await client.CorrectAsync("[jчу", CancellationToken.None)).Replacement == "хочу" && leadingKey.Calls == 3,
        "the leading-key mixed word reaches two-order confirmation and word recognition");
Check(KeyboardLayout.Normalize("при,skb", false) == "прибыли", "mixed normalization converts punctuation keys when targeting Russian");
Check(KeyboardLayout.Normalize("при,skb", true) == "ghb,skb", "mixed normalization preserves Latin punctuation keys when targeting English");
foreach (var value in new[] { "при_skb", "при@skb", "при/skb", "при1skb", "при,skb," })
    Check(!CorrectionRules.IsMixedCandidate(value, ignored), "mixed punctuation handling preserves protected tokens and trailing punctuation: " + value);
var punctuationMixed = new FakeHandler { Replies = new Queue<string>(new[] { "прибыли", "прибыли" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, punctuationMixed))
    Check((await client.CorrectAsync("при,skb", CancellationToken.None)).Replacement == "прибыли",
        "mixed punctuation word is confirmed through the normal model path");
var punctuationFallback = new FakeHandler { Replies = new Queue<string>(new[] { "при", "прибыли", "прибыли" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, punctuationFallback))
    Check((await client.CorrectAsync("при,skb", CancellationToken.None)).Replacement == "прибыли" && punctuationFallback.Calls == 4,
        "equivalent complete-layout candidates recover when the mixed punctuation is mistaken for a delimiter");
var punctuationTail = new FakeHandler { Replies = new Queue<string>(new[] { "прибыли", "прибыли", "прибыли", "прибыли" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, punctuationTail))
    Check(await client.CorrectPhraseAsync("при,skb,", CancellationToken.None) == "прибыли,",
        "a real trailing comma is preserved while an internal comma key is converted");
foreach (var (input, expected) in new[] { (",s", "бы"), (";t", "же"), ("<s", "Бы"), (":t", "Же"),
    (",s,", "бы,"), (";t.", "же.") })
{
    var wholeWord = expected.TrimEnd(',', '.');
    using var client = new ModelClient(new Settings { Backend = "Ollama" },
        new FakeHandler { Reply = JsonSerializer.Serialize(new { response = wholeWord }) },
        new FakeSpellingHints(word => new(!word.Equals(wholeWord, StringComparison.OrdinalIgnoreCase), [])));
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == expected,
        "a two-letter keyboard word keeps its leading letter key and literal ending: " + input);
}
foreach (var protection in new[] { "disabled", "ignored-source", "ignored-mapped", "native-veto" })
{
    var input = protection == "ignored-source" ? "<s" : ",s";
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = protection != "disabled",
        IgnoredWords = protection == "ignored-source" ? input : protection == "ignored-mapped" ? "бы" : "" },
        new FakeHandler { Reply = JsonSerializer.Serialize(new { response = "бы" }) },
        new FakeSpellingHints(word => new(protection == "native-veto" || word != "бы", [])));
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == input,
        "short punctuation-key conversion obeys its protections: " + protection);
}
foreach (var ending in new[] { ",", ".", ";", ":", "?!", ",..." })
{
    var endingHandler = new FakeHandler();
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, endingHandler);
    Check(await client.CorrectPhraseAsync("ghbdtn" + ending + " ", CancellationToken.None) == "привет" + ending + " ",
        "sentence punctuation survives all layout stages: " + ending);
}
foreach (var consistent in new[] { true, false })
{
    var commaHandler = new FakeHandler
    {
        Respond = body =>
        {
            using var request = JsonDocument.Parse(body);
            var prompt = request.RootElement.GetProperty("prompt").GetString();
            var reply = "ghbdtn,rfr";
            if (prompt is "ghbdtn\nпривет" or "привет\nghbdtn") reply = "привет";
            if (prompt == "rfr\nкак") reply = "как";
            if (prompt == "как\nrfr") reply = consistent ? "как" : "rfr";
            return JsonSerializer.Serialize(new { response = reply });
        }
    };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, commaHandler);
    Check(await client.CorrectPhraseAsync("ghbdtn,rfr", CancellationToken.None) == (consistent ? "привет,как" : "привет,rfr"),
        consistent ? "comma-separated words are independently confirmed and retain the separator" : "contradictory word confirmation preserves that component after a separator");
}
foreach (var protectedComma in new[] { "ghbdtn,test_name", "ghbdtn,NL100", "ghbdtn,kazrich", "ghbdtn,RFR", "ghbdtn,rfr@example.com" })
{
    var commaProtected = new FakeHandler();
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, commaProtected);
    var expectedComma = protectedComma is "ghbdtn,kazrich" or "ghbdtn,RFR" ? protectedComma.Replace("ghbdtn", "привет") : protectedComma;
    Check(await client.CorrectPhraseAsync(protectedComma, CancellationToken.None) == expectedComma,
        "comma fallback preserves protected input: " + protectedComma);
}
var rejectedCommaWord = new FakeHandler { Respond = body =>
{
    using var request = JsonDocument.Parse(body);
    var prompt = request.RootElement.GetProperty("prompt").GetString();
    var reply = prompt is "ghbdtn\nпривет" or "привет\nghbdtn" ? "привет" : "ghbdtn,rfr";
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, rejectedCommaWord))
    Check(await client.CorrectPhraseAsync("ghbdtn,rfr", CancellationToken.None) == "привет,rfr",
        "unconfirmed component is preserved while the independent confirmed word is corrected");
foreach (var confirmMappedSpelling in new[] { true, false })
{
    var mappedSpelling = new FakeHandler { ValidityReply = "ДА", Replies = new Queue<string>(new[]
        { "приве", "приве", "приве", "привет", "привет", confirmMappedSpelling ? "привет" : "приве" }
        .Select(word => JsonSerializer.Serialize(new { response = word }))) };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, mappedSpelling);
    Check(await client.CorrectPhraseAsync("ghbdt,", CancellationToken.None) == (confirmMappedSpelling ? "привет," : "приве,"),
        confirmMappedSpelling ? "confirmed layout can still receive a confirmed spelling correction while preserving the comma" :
            "a spelling correction after layout requires stable spelling of the proposed word");
}
var excessiveMappedSpelling = new FakeHandler { Replies = new Queue<string>(new[] { "приве", "приве", "приве", "приветствие" }
    .Select(word => JsonSerializer.Serialize(new { response = word }))) };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, excessiveMappedSpelling))
    Check(await client.CorrectPhraseAsync("ghbdt,", CancellationToken.None) == "приве,",
        "spelling after layout still rejects excessive changes");
foreach (var (input, expected) in new[] { ("pyf.", "знаю"), ("rke,", "клуб") })
{
    var endingLetter = new FakeHandler { Reply = JsonSerializer.Serialize(new { response = expected }) };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, endingLetter);
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == expected,
        "a terminal punctuation key remains part of a confirmed complete word: " + input);
}
var disputedEndingLetter = new FakeHandler { Respond = body =>
{
    using var request = JsonDocument.Parse(body);
    var prompt = request.RootElement.GetProperty("prompt").GetString();
    var reply = prompt switch { "знаю" or "pyf.\nзнаю" => "знаю", "знаю\npyf." => "pyf.", _ => "зна" };
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, disputedEndingLetter))
    Check(await client.CorrectPhraseAsync("pyf.", CancellationToken.None) == "зна.",
        "inconsistent whole-word confirmation preserves terminal punctuation");
foreach (var (originalReply, mappedReply, spellingReply, expected) in new[] {
    ("НЕТ", "ДА", "текст", "текст"), ("ДА", "ДА", "текст", "ntrcn"),
    ("НЕТ", "НЕТ", "текст", "ntrcn"), ("НЕТ", "ДА", "текста", "ntrcn"),
    ("НЕТ, это не слово", "ДА", "текст", "ntrcn"), ("НЕТ", "Возможно", "текст", "ntrcn") })
{
    var lexical = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var prompt = request.RootElement.GetProperty("prompt").GetString();
        var instruction = request.RootElement.GetProperty("system").GetString();
        var reply = instruction == "Custom validity" ? (prompt == "ntrcn" ? originalReply : mappedReply) :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) && prompt == "текст" ? spellingReply :
            prompt == "ntrcn\nтекст" ? "текст" : "ntrcn";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", WordValidityInstructions = "Custom validity" }, lexical);
    Check(((await client.CorrectAsync("ntrcn", CancellationToken.None)).Replacement ?? "ntrcn") == expected,
        $"independent word recognition requires exact rejection, acceptance and intact spelling: {originalReply}/{mappedReply}/{spellingReply}");
}
var recognizedMappedWord = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString();
    return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? "НЕТ" :
        instruction == Settings.DefaultContextLayoutInstructions ? "текст рпобный" :
        instruction == Settings.DefaultPhraseLayoutInstructions ? "текст пробный" : prompt });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, recognizedMappedWord))
    Check(await client.CorrectPhraseAsync("ntrcn ghj,ysq", CancellationToken.None) == "текст пробный",
        "a contextual transposition cannot introduce an unrecognized word");
var validOriginal = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString();
    return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
        (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt : "еуче" });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, validOriginal))
    Check((await client.CorrectAsync("text", CancellationToken.None)).Replacement == "text",
        "independent recognition and intact spelling preserve a valid original despite incorrect pair selection");
foreach (var (input, reply, expected) in new[] {
    ("hfve/", "раму", "раму."), ("hello/", "hello", "hello/"),
    ("some/path/", "путь", "some/path/"), ("https://example.com/", "пример", "https://example.com/"),
    ("hfve//", "раму", "hfve//") })
{
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, new FakeHandler { Reply = JsonSerializer.Serialize(new { response = reply }) });
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == expected,
        "terminal slash maps only with a corrected Latin-layout word, preserving real paths: " + input);
}
foreach (var (proposal, accept, expected) in new[] {
    ("наступило теплое лето", true, "Наступило"), ("наступило теплое лето", false, "Натупило"),
    ("наступило холодное лето", true, "Натупило"), ("прошло теплое лето", true, "Натупило"),
    ("Исправлено: наступило теплое лето", true, "Натупило") })
{
    var contextualSpelling = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "Наступило" && accept ? "ДА" : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (prompt == "натупило теплое лето" ? proposal : prompt) :
            instruction == Settings.DefaultLayoutInstructions ? "Натупило" :
            prompt.Contains("Наступило теплое лето") ? (accept ? "Наступило теплое лето" : "Натупило теплое лето") : prompt.Split('\n')[0];
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, contextualSpelling);
    Check(await client.CorrectPhraseAsync("Yfnegbkj", CancellationToken.None, "", "теплое лето") == expected,
        "contextual spelling preserves case, neighbours and edit limits, and requires consistent confirmation: " + proposal + "/" + accept);
    Check(await client.CorrectPrecedingSpellingAsync("Натупило теплое ", "лето.", CancellationToken.None) == expected + " теплое ",
        "new right context can resolve a preceding word while preserving whitespace and neighbours: " + proposal + "/" + accept);
}
Check(ModelClient.SpellingContextSuffix("Рама была чистая. Натупило теплое ") == "Натупило теплое " &&
    ModelClient.SpellingContextSuffix("Натупило теплое. ") == "" && ModelClient.SpellingContextSuffix("some/path ") == "",
    "rolling spelling context contains at most two complete Russian words and never crosses punctuation or paths");
var semanticRewrite = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    return JsonSerializer.Serialize(new { response = (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) && prompt == "яблони и вишни" ?
        "яблоки и вишни" : prompt.Split('\n')[0] });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, semanticRewrite))
    Check(await client.CorrectPrecedingSpellingAsync("яблони и ", "вишни.", CancellationToken.None) == "яблони и ",
        "rolling spelling does not replace a complete word with another word of the same length");
var ignoredSlash = new FakeHandler();
using (var client = new ModelClient(new Settings { Backend = "Ollama", IgnoredWords = "hfve/" }, ignoredSlash))
    Check(await client.CorrectPhraseAsync("hfve/", CancellationToken.None) == "hfve/" && ignoredSlash.Calls == 0,
        "ignored complete tokens are protected before terminal slash processing");
foreach (var recognized in new[] { true, false })
{
    var inflection = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? (recognized ? "ДА" : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (prompt == "мыла" ? "мыло" : prompt) : "мыла" });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, inflection);
    Check(await client.CorrectPhraseAsync("мыла", CancellationToken.None, "Мама ", " раму") == (recognized ? "мыла" : "мыло"),
        "recognition and unchanged contextual spelling protect an existing inflection without reapplying cached spelling: " + recognized);
}
foreach (var (checkedWord, validity, expected) in new[] {
    ("Наступило", "ДА", "Наступило"), ("Наступила", "ДА", "Насступило"),
    ("Наступило", "НЕТ", "Насступило"), ("Наступило", "ДА, это слово", "Насступило") })
{
    var duplicate = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "Наступило" ? validity : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (prompt == "Насступило" ? "Наступило" : prompt == "Наступило" ? checkedWord : prompt) : "Насступило";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, duplicate);
    Check(await client.CorrectPhraseAsync("Yfccnegbkj", CancellationToken.None) == expected,
        "spelling after layout requires stable spelling and exact word recognition: " + checkedWord + "/" + validity);
}
foreach (var keepContext in new[] { true, false })
{
    var contextualForm = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (keepContext || prompt == "тепло" ? "ДА" : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt switch {
                "теплое лето" => "тепло лето", _ => prompt } : "тепло лето";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, contextualForm);
    var formResult = await client.CorrectPrecedingSpellingAsync("теплое ", "лето", CancellationToken.None);
    Check(formResult == (keepContext ? "теплое " : "тепло "),
        "contextual proposal cannot change an independently recognized and spelling-confirmed word: " + keepContext + " => " + formResult);
}
foreach (var decision in new[] { "accept", "no-provenance", "no-native", "one-neighbour", "invalid-neighbour", "ignored-candidate",
    "reverse-choice", "reverse-context", "changed-spelling", "invalid-word", "malformed-choice", "layout-disabled" })
{
    var contextualSettings = new Settings { Backend = "Ollama", FixKeyboardLayout = decision != "layout-disabled",
        SpellingChoiceInstructions = "Editable spelling selection", ContextLayoutInstructions = "Editable context selection",
        SpellingInstructions = "Editable spelling review", WordValidityInstructions = "Editable word recognition",
        IgnoredWords = decision == "ignored-candidate" ? "хотя" : "" };
    var knownWords = new HashSet<string>(["хоты", "хотя", "бы", "по"]);
    var choiceCalls = 0;
    var contextCalls = 0;
    var model = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = prompt;
        if (instruction == contextualSettings.SpellingChoiceInstructions)
        {
            choiceCalls++;
            reply = decision == "malformed-choice" ? "хотя бы по дороге" :
                decision == "reverse-choice" && choiceCalls == 2 ? "хоты бы по" : "хотя бы по";
        }
        if (instruction == contextualSettings.ContextLayoutInstructions)
        {
            contextCalls++;
            reply = decision == "reverse-context" && contextCalls == 2 ? "хоты бы по" : "хотя бы по";
        }
        if (instruction == contextualSettings.SpellingInstructions && decision == "changed-spelling" && prompt == "хотя") reply = "хоте";
        if (instruction == contextualSettings.WordValidityInstructions) reply = decision == "invalid-word" ? "НЕТ" : "ДА";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(contextualSettings, model, decision == "no-native" ? null :
        new FakeSpellingHints(word => new(!knownWords.Contains(word) || decision == "invalid-neighbour" && word == "по", [])));
    var result = await client.CorrectPrecedingSpellingAsync("хоты ", decision == "one-neighbour" ? "бы" : "бы по",
        CancellationToken.None, decision == "no-provenance" ? null : new HashSet<string>(["хоты"]));
    Check(result == (decision == "accept" ? "хотя " : "хоты "),
        "a valid mapped key typo needs provenance, two known neighbours and every editable confirmation: " + decision);
    if (decision == "accept") Check(choiceCalls == 2 && contextCalls == 2,
        "real-word typo selection preserves the source and checks both alternative orders");
}
foreach (var input in new[] { "хоты бы по", "[jns ,s gj" })
{
    var phraseModel = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
            instruction == Settings.DefaultSpellingChoiceInstructions ? "хотя бы по" :
            instruction == Settings.DefaultContextLayoutInstructions && prompt.Contains("хотя бы по") ? "хотя бы по" :
            prompt.Split('\n').FirstOrDefault(value => value.All(c => CorrectionRules.IsRussianLetter(c) || char.IsWhiteSpace(c))) ?? prompt;
        return JsonSerializer.Serialize(new { response = reply });
    } };
    var knownWords = new HashSet<string>(["хоты", "хотя", "бы", "по"]);
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, phraseModel,
        new FakeSpellingHints(word => new(!knownWords.Contains(word), [])));
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == (input.Any(char.IsAsciiLetter) ? "хотя бы по" : input),
        "contextual neighbouring-key spelling only follows an actual layout conversion: " + input);
}
foreach (var (contextReply, expected) in new[] {
    ("сегодня наступило", "Наступило"), ("сегодня насступило", "Насступило"),
    ("today arrived", "Насступило"), ("вчера наступило", "Насступило") })
{
    var contextualConfirmation = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ?
            (prompt == "Наступило" || prompt.Equals("Насступило", StringComparison.OrdinalIgnoreCase) ? "ДА" : "НЕТ") : (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ?
            (prompt == "Насступило" ? "Наступило" : prompt == "сегодня насступило" ? contextReply : prompt) :
            instruction == Settings.DefaultLayoutInstructions ? "Насступило" : prompt.Split('\n')[0] });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, contextualConfirmation);
    Check(await client.CorrectPhraseAsync("Yfccnegbkj", CancellationToken.None, "Сегодня ") == expected,
        "isolated spelling proposal also requires exact contextual confirmation without translation or neighbour edits: " + contextReply);
}
foreach (var recognized in new[] { true, false })
{
    var preLayoutSpelling = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "дела" && recognized ? "ДА" : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (prompt == "дела" ? "дело" : prompt) :
            instruction == Settings.DefaultLayoutInstructions ? "дела" :
            prompt.Split('\n').FirstOrDefault(line => line == "привет как дело") ?? prompt.Split('\n')[0];
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, preLayoutSpelling);
    Check(await client.CorrectPhraseAsync("ltkf", CancellationToken.None, "привет как ") == (recognized ? "дела" : "дело"),
        "mapped spelling preserves a recognized inflection before contextual layout fallback: " + recognized);
}
foreach (var (text, accepted) in new[] {
    ("вишни.Vfktymrbq", true), ("вишни.Маленький", true), ("вишни...Маленький", true),
    ("hello!World", true), ("слово?Следующее", true),
    ("example.Com", false), ("пример.рф", false), ("И.Иванов", true),
    ("https://вишни.Vfktymrbq", false), ("user@вишни.Vfktymrbq", false),
    ("C:\\вишни.Vfktymrbq", false), ("some/вишни.Vfktymrbq", false),
    ("id_вишни.Vfktymrbq", false), ("1вишни.Vfktymrbq", false) })
{
    var boundary = text.LastIndexOfAny(new[] { '.', '!', '?' }) + 1;
    Check(TextBoundary.IsStart(text, boundary) == accepted,
        "sentence start without whitespace preserves domain, path and identifier boundaries: " + text);
}
Check(!TextBoundary.IsStart("вишни.Vfktymrbq", 6, new HashSet<string> { "вишни.Vfktymrbq" }),
    "ignored complete token is not split at a sentence-like dot");
using (var client = new ModelClient(new Settings { Backend = "Ollama", IgnoredWords = "вишни" }, new FakeHandler {
    Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        if (prompt.Contains("вишни")) throw new Exception("Sentence context crossed a dot");
        return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? "НЕТ" :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt : "Маленький" });
    }
}))
    Check(await client.CorrectPhraseAsync("вишни.Vfktymrbq", CancellationToken.None) == "вишни.Маленький",
        "joined sentences correct independently and preserve the exact separator without inserting whitespace");
foreach (var validity in new[] { "ДА", "НЕТ", "Возможно", "" })
{
    var mixedRecognition = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "прикольно" ? validity : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt : "ghbrjkmyj";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, mixedRecognition);
    Check((await client.CorrectAsync("ghbrjkmyо", CancellationToken.None)).Replacement == (validity == "ДА" ? "прикольно" : "ghbrjkmyj"),
        "independent mixed-word recognition rejects non-exact or negative replies: " + validity);
}
foreach (var mode in new[] { "both-valid", "original-valid", "spelling-changes" })
{
    var ambiguousMixed = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ?
            (prompt == "ghbrjkmyо" ? (mode == "original-valid" ? "ДА" : "НЕТ") : "ДА") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (mode == "spelling-changes" ? "привет" : prompt) : "ghbrjkmyо";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, ambiguousMixed);
    Check((await client.CorrectAsync("ghbrjkmyо", CancellationToken.None)).Replacement == null,
        "mixed recognition preserves ambiguity, a recognized original and unstable spelling: " + mode);
}
foreach (var (mixedInput, mapped, spelled) in new[] { ("ghbrjkmyjо", "прикольноо", "прикольно"), ("hellщo", "helloo", "hello") })
    foreach (var recognizeSpelling in new[] { true, false })
    {
        var mixedSpelling = new FakeHandler { Respond = body => {
            using var request = JsonDocument.Parse(body);
            var instruction = request.RootElement.GetProperty("system").GetString();
            var prompt = request.RootElement.GetProperty("prompt").GetString()!;
            var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == spelled && recognizeSpelling ? "ДА" : "НЕТ") :
                (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (prompt == mapped ? spelled : prompt) :
                prompt.Split('\n').FirstOrDefault(line => line == mapped) ?? prompt.Split('\n')[0];
            return JsonSerializer.Serialize(new { response = reply });
        } };
        using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, mixedSpelling);
        Check(await client.CorrectPhraseAsync(mixedInput, CancellationToken.None) == (recognizeSpelling ? spelled : mapped),
            "mixed-layout spelling requires independent confirmation after normalization: " + mixedInput + ", recognized=" + recognizeSpelling);
    }
foreach (var (input, expected) in new[] { ("ds,", "вы,"), ("ds, ", "вы, "), ("полноcnm.", "полностью"), ("полноcnm..", "полностью.") })
{
    var terminalHints = new FakeSpellingHints(word => word is "вы" or "полностью" ? new(false, []) : new(true, []));
    var confidentButWrong = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var lines = prompt.Split('\n');
        var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt :
            new[] { "полностьюю", "выб", "полностью", "вы" }.FirstOrDefault(lines.Contains) ?? lines[0];
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, confidentButWrong, terminalHints);
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == expected,
        "independent spelling evidence prevents consuming literal terminal punctuation: " + input);
}
foreach (var (input, expected) in new[] {
    ("'ghbdtn", "'привет"), ("[ghbdtn", "[привет"), ("ghbdtn]", "привет]"),
    ("ghbdtn'", "привет'"), ("'helllo", "'hello"), ("'[jxe", "'хочу"),
    ("\":bpym", "\"Жизнь"), ("(,.l;tnf", "(бюджета"), ("[jxe", "хочу"), ("{jxe", "Хочу"),
    ("[jxe!", "хочу!"), ("{jxe...", "Хочу...") })
{
    var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hello", "привет", "хочу", "Жизнь", "бюджета" };
    var proposals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["эпривет"] = "привет", ["хпривет"] = "привет", ["приветъ"] = "привет", ["приветэ"] = "привет",
        ["helllo"] = "hello", ["эхочу"] = "хочу"
    };
    var wrapperHints = new FakeSpellingHints(word => new(!recognized.Contains(word),
        proposals.TryGetValue(word, out var proposed) ? [proposed] : []));
    var wrapperModel = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var lines = prompt.Split('\n');
        var mapped = KeyboardLayout.Normalize(prompt, false);
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (recognized.Contains(prompt) ? "ДА" : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? proposals.GetValueOrDefault(prompt, prompt) :
            lines.FirstOrDefault(recognized.Contains) ?? (recognized.Contains(mapped) ? mapped : lines[0]);
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, wrapperModel, wrapperHints);
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == expected,
        "outer wrappers survive invalid echoes and spelling proposals deleting their mapped edge, while real letter keys remain: " + input);
}
foreach (var position in new[] { "left", "right", "inside" })
{
    var oversized = new string('ж', 1800);
    var sawBoundedRequest = false;
    var boundedContextModel = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        if (prompt.Contains(new string('ж', 33))) throw new InvalidOperationException("An oversized neighbour entered the model request.");
        sawBoundedRequest = true;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "привет" ? "ДА" : "НЕТ") :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt : "привет";
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = false }, boundedContextModel,
        new FakeSpellingHints(word => new(word != "привет", [])));
    var input = position == "inside" ? "ghbdtn " + oversized : "ghbdtn";
    var actual = await client.CorrectPhraseAsync(input, CancellationToken.None,
        position == "left" ? oversized : "", position == "right" ? oversized : "");
    Check(sawBoundedRequest && actual == (position == "inside" ? "привет " + oversized : "привет"),
        "an oversized neighbouring token cannot overflow model context and is preserved verbatim: " + position);
}
foreach (var acceptValidity in new[] { true, false })
    foreach (var damagedSpelling in new[] { true, false })
    {
        var independentReviewHints = new FakeSpellingHints(word => word == "жжираф" ? new(true, ["жираф"]) :
            new(word == "жирфа" && damagedSpelling, []));
        var independentReview = new FakeHandler { Respond = body => {
            using var request = JsonDocument.Parse(body);
            var instruction = request.RootElement.GetProperty("system").GetString();
            var prompt = request.RootElement.GetProperty("prompt").GetString()!;
            var reply = instruction == Settings.DefaultWordValidityInstructions ? (acceptValidity ? "ДА" : "НЕТ") :
                instruction == Settings.DefaultSpellingChoiceInstructions ? "жираф" : prompt == "жираф" ? "жирфа" : prompt;
            return JsonSerializer.Serialize(new { response = reply });
        } };
        using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, FixHyphens = false }, independentReview, independentReviewHints);
        Check(await client.CorrectPhraseAsync("жжираф", CancellationToken.None) == (acceptValidity && damagedSpelling ? "жираф" : "жжираф"),
            $"a native-confirmed candidate survives a model-created typo only with exact word recognition: {acceptValidity}/{damagedSpelling}");
    }
foreach (var consistent in new[] { true, false })
{
    var repeatReview = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
            instruction == Settings.DefaultLayoutInstructions && consistent ? "ёлочка" : prompt.Split('\n')[0];
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, FixHyphens = false }, repeatReview,
        new FakeSpellingHints(word => new(word == "ёёлочка", word == "ёёлочка" ? ["ёлочка"] : [])));
    Check(await client.CorrectPhraseAsync("ёёлочка", CancellationToken.None) == (consistent ? "ёлочка" : "ёёлочка"),
        "a single native repeated-letter repair still needs a consistent model comparison when the first choice abstains: " + consistent);
}
var conflictingSubstitution = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
        instruction == Settings.DefaultSpellingChoiceInstructions ? "сорят" : prompt == "сорян" ? "сорня" : prompt == "сорят" ? "сорат" : prompt;
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, FixHyphens = false }, conflictingSubstitution,
    new FakeSpellingHints(word => word == "сорян" ? new(true, ["соря", "сорят"]) : new(word is not ("соря" or "сорят"), []))))
    Check(await client.CorrectPhraseAsync("сорян", CancellationToken.None) == "сорян",
        "conflicting spelling reviews cannot replace an unknown word through a non-mechanical native substitution");
foreach (var consistent in new[] { true, false })
    foreach (var ignoredCandidate in new[] { true, false })
    {
        var contextSpelling = new FakeHandler { Respond = body => {
            using var request = JsonDocument.Parse(body);
            var instruction = request.RootElement.GetProperty("system").GetString();
            var prompt = request.RootElement.GetProperty("prompt").GetString()!;
            var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "throough" ? "НЕТ" : "ДА") :
                instruction == Settings.DefaultSpellingChoiceInstructions ? (consistent ? "walk through the door" : prompt.Split('\n')[1]) :
                prompt == "throough" ? "thorough" : prompt;
            return JsonSerializer.Serialize(new { response = reply });
        } };
        using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, FixHyphens = false,
            IgnoredWords = ignoredCandidate ? "through" : "" }, contextSpelling,
            new FakeSpellingHints(word => new(word == "throough", word == "throough" ? ["through", "thorough"] : [])));
        Check(await client.CorrectPhraseAsync("walk throough the door", CancellationToken.None) ==
            (consistent && !ignoredCandidate ? "walk through the door" : "walk thorough the door"),
            $"context selects equal-distance native spellings only with order agreement and exclusions: {consistent}/{ignoredCandidate}");
    }
foreach (var consistent in new[] { true, false })
    foreach (var chosen in new[] { true, false })
    {
        var transpositionReview = new FakeHandler { Respond = body => {
            using var request = JsonDocument.Parse(body);
            var instruction = request.RootElement.GetProperty("system").GetString();
            var prompt = request.RootElement.GetProperty("prompt").GetString()!;
            var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
                instruction == Settings.DefaultSpellingChoiceInstructions && chosen ? "realise" :
                instruction == Settings.DefaultLayoutInstructions && consistent ? "realise" : prompt.Split('\n')[0];
            return JsonSerializer.Serialize(new { response = reply });
        } };
        using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, FixHyphens = false }, transpositionReview,
            new FakeSpellingHints(word => new(word == "realsie", word == "realsie" ? ["realise"] : [])));
        Check(await client.CorrectPhraseAsync("realsie", CancellationToken.None) == (consistent ? "realise" : "realsie"),
            $"a model-accepted adjacent transposition needs a native candidate and both comparison orders: {consistent}/{chosen}");
    }
var oldSpellingDefault = "Исправь только опечатки в русском или английском слове или тексте. Сохраняй часть речи, число, род, падеж и время. Правильно написанные слова оставляй без изменений. Не переводи и не перефразируй. Верни только результат.";
Check(SettingsFile.Parse(JsonSerializer.Serialize(new { SpellingInstructions = oldSpellingDefault })).EnglishSpellingInstructions == Settings.DefaultEnglishSpellingInstructions,
    "previous default spelling instruction migrates regional spelling preservation");
Check(SettingsFile.Parse(JsonSerializer.Serialize(new { SpellingInstructions = oldSpellingDefault + " Custom." })).EnglishSpellingInstructions == oldSpellingDefault + " Custom.",
    "regional spelling instruction migration preserves customized wording");
Check(SettingsFile.Parse(JsonSerializer.Serialize(new { SpellingInstructions = "Custom RU", EnglishSpellingInstructions = "Custom EN" })).EnglishSpellingInstructions == "Custom EN",
    "explicit English instructions survive settings loading");
foreach (var invalid in new string?[] { null, "", new string('x', 8001) })
{
    var rejected = false;
    try { SettingsFile.Parse(JsonSerializer.Serialize(new { EnglishSpellingInstructions = invalid })); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "English instructions reject missing or excessive text");
}
foreach (var (word, expectedInstruction) in new[] { ("hello", "Custom English spelling"), ("привет", "Custom Russian spelling") })
{
    var languageHandler = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        return JsonSerializer.Serialize(new { response = request.RootElement.GetProperty("prompt").GetString() });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false,
        SpellingInstructions = "Custom Russian spelling", EnglishSpellingInstructions = "Custom English spelling" }, languageHandler);
    Check((await client.CorrectAsync(word, CancellationToken.None)).Replacement == word, "custom spelling instructions preserve exact words: " + word);
    using var request = JsonDocument.Parse(languageHandler.LastBody!);
    Check(request.RootElement.GetProperty("system").GetString() == expectedInstruction, "spelling uses the editable instruction for its alphabet: " + word);
}
var inflectedTerminalHints = new FakeSpellingHints(word => new(word is not ("это" or "Юность" or "Юностью"), []));
foreach (var (input, expected) in new[] { ("ghbdtn helllo", "привет hello"), ("ghbdtn  helllo ", "привет  hello "),
    ("привет helllo", "привет hello"), ("helllo привет", "hello привет") })
{
    var unsafePhrase = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var lines = prompt.Split('\n');
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt is "hello" or "привет" ? "ДА" : "НЕТ") :
            instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions ? (prompt == "helllo" ? "hello" : prompt) :
            instruction == Settings.DefaultSpellingChoiceInstructions ? lines.FirstOrDefault(line => line == "hello") ?? lines[0] :
            instruction is Settings.DefaultPhraseLayoutInstructions or Settings.DefaultContextLayoutInstructions ?
                lines.FirstOrDefault(line => line.Contains("рудддщ")) ?? lines[0] :
            instruction == Settings.DefaultLayoutInstructions ? lines.FirstOrDefault(line => line == "привет") ?? lines[0] : prompt;
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, unsafePhrase,
        new FakeSpellingHints(word => new(word is not ("hello" or "привет"), word == "helllo" ? ["hello"] : [])));
    Check(await client.CorrectPhraseAsync(input, CancellationToken.None) == expected,
        "phrase and context layout choices cannot replace a confirmed source spelling with unrepaired nonwords: " + input);
}
var closerHintProvider = new FakeSpellingHints(word => word == "исправленеи" ? new(true, ["исправлены", "исправление"]) : new(false, []));
foreach (var preserveRegional in new[] { false, true })
{
    var regionalContext = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var reply = instruction == Settings.DefaultWordValidityInstructions ? (prompt == "coluor" ? "НЕТ" : "ДА") :
            instruction == Settings.DefaultEnglishSpellingInstructions ? prompt switch {
                "coluor" => "colour", "my favourite coluor" => preserveRegional ? "my favourite colour" : "my favourite color", _ => prompt } :
            instruction == Settings.DefaultSpellingChoiceInstructions ? (prompt.StartsWith("my favourite") ? "my favourite color" : "colour") :
            prompt.Split('\n')[0];
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, regionalContext,
        new FakeSpellingHints(word => new(word is not ("my" or "favourite" or "colour" or "color"), word == "coluor" ? ["colour", "color"] : [])));
    var regionalActual = await client.CorrectPhraseAsync("my favourite coluor", CancellationToken.None);
    Check(regionalActual ==
        (preserveRegional ? "my favourite colour" : "my favourite color"),
        "context respects an exact competing candidate from the editable English spelling review: " + preserveRegional);
}
var closerHintModel = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var reply = instruction == Settings.DefaultSpellingChoiceInstructions ? "исправление" :
        instruction == Settings.DefaultWordValidityInstructions ? "НЕТ" : prompt == "исправленеи" ? "исправлены" : prompt;
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, FixHyphens = false }, closerHintModel, closerHintProvider))
    Check(await client.CorrectPhraseAsync("исправленеи", CancellationToken.None) == "исправление",
        "a valid model proposal cannot hide a closer independently confirmed transposition");
var inflectedTerminalModel = new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var instruction = request.RootElement.GetProperty("system").GetString();
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
        (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt : prompt.Split('\n').FirstOrDefault(line => line.EndsWith("Юностью")) ?? prompt.Split('\n')[0];
    return JsonSerializer.Serialize(new { response = reply });
} };
using (var client = new ModelClient(new Settings { Backend = "Ollama" }, inflectedTerminalModel, inflectedTerminalHints))
{
    Check(await client.CorrectPhraseAsync(">yjcnm.", CancellationToken.None) == "Юность.",
        "two valid inflections retain a literal final period until sentence context resolves the ambiguity");
    Check(await client.CorrectPhraseAsync("это >yjcnm.", CancellationToken.None) == "это Юность.",
        "one neighbouring word is insufficient to remove punctuation from another valid inflection");
}
foreach (var acceptChoice in new[] { true, false })
    foreach (var recognizeCandidate in new[] { true, false })
    {
        const string choiceInstruction = "Custom spelling candidate choice";
        var sawCustomInstruction = false;
        var hints = new FakeSpellingHints(word => word == "доволдим" ? new(true, ["доводим", "доводом", "слишком много слов"]) : new(word == "доводим" && !recognizeCandidate, []));
        var hintModel = new FakeHandler { Respond = body => {
            using var request = JsonDocument.Parse(body);
            var instruction = request.RootElement.GetProperty("system").GetString();
            var prompt = request.RootElement.GetProperty("prompt").GetString()!;
            sawCustomInstruction |= instruction == choiceInstruction;
            var reply = instruction == choiceInstruction ? (acceptChoice ? "доводим" : "доволдим") :
                instruction == Settings.DefaultWordValidityInstructions ? (prompt == "доводим" && !recognizeCandidate ? "НЕТ" : "ДА") :
                (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? (prompt == "доволдим" ? "довольны" : prompt) : prompt.Split('\n')[0];
            return JsonSerializer.Serialize(new { response = reply });
        } };
        using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false, SpellingChoiceInstructions = choiceInstruction }, hintModel, hints);
        var corrected = await client.CorrectPhraseAsync("доволдим еще", CancellationToken.None);
        Check(corrected == (acceptChoice && recognizeCandidate ? "доводим еще" : "доволдим еще"),
            "spelling hints require model choice and independent native candidate recognition: " + acceptChoice + "/" + recognizeCandidate);
        Check(sawCustomInstruction, "spelling candidate instruction is editable");
    }
foreach (var word in new[] { "сорян", "кринж", "лол" })
{
    var slangHints = new FakeSpellingHints(_ => new(true, ["соря", "сорят", "крин", "крен", "лоб"]));
    var conservativeModel = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        return JsonSerializer.Serialize(new { response = request.RootElement.GetProperty("prompt").GetString() });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, conservativeModel, slangHints);
    Check(await client.CorrectPhraseAsync(word, CancellationToken.None) == word,
        "dictionary uncertainty does not override model-accepted informal spelling: " + word);
}
foreach (var useUnavailableProvider in new[] { true, false })
{
    var fallbackModel = new FakeHandler();
    using var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, fallbackModel,
        useUnavailableProvider ? new FakeSpellingHints(_ => null) : null);
    Check((await client.CorrectAsync("првиет", CancellationToken.None)).Replacement == "привет", "unavailable spelling provider preserves model corrections");
}
var ignoredHints = new FakeSpellingHints(_ => throw new InvalidOperationException("Ignored text must never reach the provider."));
using (var client = new ModelClient(new Settings { Backend = "Ollama", IgnoredWords = "доволдим" }, new FakeHandler(), ignoredHints))
    Check(await client.CorrectPhraseAsync("доволдим", CancellationToken.None) == "доволдим", "ignored words bypass spelling hints");
foreach (var instruction in new[] { "", new string('x', 8001) })
{
    var rejected = false;
    try { (new Settings { SpellingChoiceInstructions = instruction }).Validate(); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "spelling candidate instruction rejects missing or excessive text");
}
foreach (var property in new[] { "AllowedApps", "IgnoredWords" })
{
    var rejected = false;
    try { SettingsFile.Parse("{\"" + property + "\":null}"); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "malformed null settings produce a recoverable validation error: " + property);
}
var legacySettings = SettingsFile.Parse("{\"WordValidityInstructions\":\"Существует ли такое обычное русское или английское слово? Ответь только ДА или НЕТ. Не считай случайный набор букв именем или сокращением.\"}");
Check(legacySettings.WordValidityInstructions == Settings.DefaultWordValidityInstructions &&
    legacySettings.SpellingChoiceInstructions == Settings.DefaultSpellingChoiceInstructions, "legacy settings migrate default word recognition and initialize spelling choices");
var customInstructions = SettingsFile.Parse("{\"WordValidityInstructions\":\"Custom recognition\",\"SpellingChoiceInstructions\":\"Custom choice\"}");
Check(customInstructions.WordValidityInstructions == "Custom recognition" && customInstructions.SpellingChoiceInstructions == "Custom choice",
    "settings migration preserves user-edited instructions");
foreach (var word in new[] { "дела", "тёплое", "this", "hello" })
{
    var knownSpelling = new FakeSpellingHints(_ => new(false, []));
    var unwantedInference = new FakeHandler { Respond = _ => throw new InvalidOperationException("Known correct words do not need inference.") };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, unwantedInference, knownSpelling);
    var first = await client.CorrectAsync(word, CancellationToken.None);
    var second = await client.CorrectAsync(word, CancellationToken.None);
    Check(first.Replacement == word && !first.Cached && second.Replacement == word && second.Cached,
        "known correct spelling is preserved immediately and cached: " + word);
}
var EnglishHints = new FakeSpellingHints(word => word == "wierd" ? new(true, ["weird", "wired", "wield"]) : new(false, []));
using (var client = new ModelClient(new Settings { Backend = "Ollama", FixKeyboardLayout = false }, new FakeHandler { Respond = body => {
    using var request = JsonDocument.Parse(body);
    var prompt = request.RootElement.GetProperty("prompt").GetString()!;
    var instruction = request.RootElement.GetProperty("system").GetString();
    return JsonSerializer.Serialize(new { response = instruction == Settings.DefaultWordValidityInstructions ? "НЕТ" :
        instruction == Settings.DefaultSpellingChoiceInstructions ? "wierd" : prompt });
} }, EnglishHints))
    Check((await client.CorrectAsync("wierd", CancellationToken.None)).Replacement == "weird",
        "native spelling plus stable spelling review survives a false model yes/no rejection of a real English word");
foreach (var enabled in new[] { true, false })
    foreach (var unchangedSpelling in new[] { true, false })
    {
        var spacingHints = new FakeSpellingHints(word => new(word is not ("аж" or "жарко" or "потом" or "по" or "том"), []));
        var spacingModel = new FakeHandler { Respond = body => {
            using var request = JsonDocument.Parse(body);
            var instruction = request.RootElement.GetProperty("system").GetString();
            var prompt = request.RootElement.GetProperty("prompt").GetString()!;
            var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
                instruction == Settings.DefaultSpellingChoiceInstructions && prompt.Split('\n').Contains("аж жарко") ? "аж жарко" :
                (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) && prompt == "аж жарко" && !unchangedSpelling ? "аж очень жарко" :
                prompt.Split('\n')[0];
            return JsonSerializer.Serialize(new { response = reply });
        } };
        using var client = new ModelClient(new Settings { Backend = "Ollama", FixHyphens = enabled }, spacingModel, spacingHints);
        Check(await client.CorrectHyphensAsync("ажжарко", CancellationToken.None) == (enabled && unchangedSpelling ? "аж жарко" : "ажжарко"),
            "missing-space correction changes no letters and requires independent spelling agreement: " + enabled + "/" + unchangedSpelling);
        Check(await client.CorrectHyphensAsync("потом", CancellationToken.None) == "потом", "known compound word is never split into separate dictionary words");
    }
foreach (var confirmedContext in new[] { true, false })
{
    var terminalChoicePrompts = new List<string>();
    var terminalWords = new HashSet<string> { "я", "работу", "дела", "делаю", "как" };
    var contextHints = new FakeSpellingHints(word => new(!terminalWords.Contains(word), []));
    var contextModel = new FakeHandler { Respond = body => {
        using var request = JsonDocument.Parse(body);
        var instruction = request.RootElement.GetProperty("system").GetString();
        var prompt = request.RootElement.GetProperty("prompt").GetString()!;
        var lines = prompt.Split('\n');
        if (instruction == Settings.DefaultSpellingChoiceInstructions && lines.Contains("я дела. работу") && lines.Contains("я делаю работу"))
            terminalChoicePrompts.Add(prompt);
        var reply = instruction == Settings.DefaultWordValidityInstructions ? "ДА" :
            (instruction is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) ? prompt :
            instruction == Settings.DefaultPhraseLayoutInstructions ? lines[0] :
            instruction == Settings.DefaultSpellingChoiceInstructions ? (confirmedContext ? "я делаю работу" : "Я делаю всю работу") :
            instruction == Settings.DefaultLayoutInstructions ? lines.FirstOrDefault(line => line == "дела") ?? lines.FirstOrDefault(line => line == "делаю") ?? lines[0] :
            lines.FirstOrDefault(line => line.Contains("дела")) ?? lines[0];
        return JsonSerializer.Serialize(new { response = reply });
    } };
    using var client = new ModelClient(new Settings { Backend = "Ollama" }, contextModel, contextHints);
    Check(await client.CorrectPhraseAsync("я ltkf. работу", CancellationToken.None) == (confirmedContext ? "я делаю работу" : "я дела. работу"),
        "terminal-key context selects only an exact confirmed alternative and never paraphrases neighbours: " + confirmedContext);
    Check(terminalChoicePrompts.Count > 0 && terminalChoicePrompts.All(prompt => prompt.Split('\n')[0] == "я ltkf. работу"),
        "terminal spelling comparisons retain the same original first line in every candidate order");
    Check(await client.CorrectPhraseAsync("я дела. работу", CancellationToken.None) == "я дела. работу",
        "terminal disambiguation does not rewrite a Cyrillic-only grammatical form");
}
foreach (var separator in new[] { ".", ",", ";", ":", "!", "?", "…", "—", "–", "(", ")", "[", "]", "{", "}", "«", "»", "\"", "\t", "\u00a0" })
{
    foreach (var prefix in new[] { "вишни", "hello", "А", new string('а', 40) })
        foreach (var suffix in new[] { "ghbdtn", "Ghbdtn" })
            Check(TextBoundary.IsStart(prefix + separator + suffix, prefix.Length + separator.Length),
                "boundary is independent of preceding alphabet, length and next case: " + prefix + separator + suffix);
    var boundaryQueue = new TypingQueue();
    var input = "ghbdtn" + separator + "rfr ";
    var acceptedInput = true;
    foreach (var c in input) acceptedInput &= boundaryQueue.Append(c);
    Check(acceptedInput && boundaryQueue.Text == input && boundaryQueue.Next(includeUnfinished: true)?.Text == input,
        "queue retains complete input and exact Unicode separator: " + separator);
}
foreach (var value in new[] { "example.com", "example.COM", "пример.рф", "EXAMPLE.XN--P1AI", "site.co.uk" })
    Check(TextBoundary.IsProtected(value), "domain protection uses offline TLD data across case and IDN: " + value);
foreach (var value in new[] { "как-то", "don't", "как‑то", "don’t" })
    Check(!TextBoundary.IsStart(value, value.IndexOfAny(new[] { '-', '\'', '‑', '’' }) + 1), "internal joiner stays inside the word: " + value);
Check(!TextBoundary.IsProtected("вишни.привет"), "ordinary adjacent words do not become a domain merely because they contain a dot");
Console.WriteLine($"{passed} checks passed.");

sealed class FakeSpellingHints(Func<string, SpellingEvidence?> check) : ISpellingHints
{
    public Task<SpellingEvidence?> CheckAsync(string word, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(check(word));
    }
}

sealed class FakeHandler : HttpMessageHandler
{
    public int Calls;
    public int Delay;
    public string? LastBody;
    public Queue<string>? Replies;
    public Func<string, string>? Respond;
    public string Reply = "{\"response\":\"привет\",\"done\":true,\"done_reason\":\"stop\"}";
    public string ValidityReply = "НЕИЗВЕСТНО";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (Delay > 0) await Task.Delay(Delay, cancellationToken);
        using var body = JsonDocument.Parse(LastBody);
        var defaultValidity = body.RootElement.TryGetProperty("system", out var instruction) && instruction.GetString() == Settings.DefaultWordValidityInstructions;
        var prompt = body.RootElement.TryGetProperty("prompt", out var input) ? input.GetString() : null;
        var contextualSpelling = instruction.ValueKind == JsonValueKind.String && (instruction.GetString() is Settings.DefaultSpellingInstructions or Settings.DefaultEnglishSpellingInstructions) && prompt?.Contains(' ') == true;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Respond?.Invoke(LastBody) ??
            (defaultValidity ? JsonSerializer.Serialize(new { response = ValidityReply }) :
                contextualSpelling ? JsonSerializer.Serialize(new { response = prompt }) : Replies?.Dequeue() ?? Reply), Encoding.UTF8, "application/json") };
    }
}
