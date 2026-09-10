using System.Diagnostics;
using System.Text.Json;
using Kazrich.Core;
using Kazrich.App;

if (Array.IndexOf(args, "--native-spelling") is var nativeIndex && nativeIndex >= 0)
{
    using var native = new WindowsSpellingHints();
    foreach (var word in args.Skip(nativeIndex + 1))
    {
        var evidence = await native.CheckAsync(word, CancellationToken.None);
        Console.WriteLine(word + ": " + (evidence == null ? "UNAVAILABLE" : evidence.HasError ? string.Join(", ", evidence.Suggestions) : "VALID"));
    }
    return;
}

var settings = (args.Contains("--saved") ? SettingsFile.Load() : new Settings()) with { Endpoint = args.ElementAtOrDefault(0) ?? "http://localhost:11434",
    Backend = args.ElementAtOrDefault(1) ?? "Ollama", Model = args.ElementAtOrDefault(2) ?? "qwen3.5:0.8b",
    TimeoutSeconds = 90, CpuThreads = 2, CpuOnly = true };
using var managedCpu = args.Contains("--managed-cpu") ? new ManagedModel() : null;
if (managedCpu != null)
{
    if (!managedCpu.IsInstalled(settings.Model, cpuOnly: true))
        throw new InvalidOperationException("CPU benchmarking requires an already installed local model and runtime.");
    var startup = Stopwatch.StartNew();
    await managedCpu.PrepareAndStartAsync(settings, false, new Progress<string>(Console.WriteLine), CancellationToken.None);
    settings = managedCpu.ConnectionSettings(settings);
    Console.WriteLine("Isolated CPU model ready: " + startup.ElapsedMilliseconds + " ms; threads=" + settings.CpuThreads);
}
using var spellingHints = args.Contains("--no-native") ? null : new WindowsSpellingHints();
using var client = new ModelClient(settings, args.Contains("--trace") ? new TraceHandler() : null, spellingHints);
if (Array.IndexOf(args, "--choice-probe") is var choiceIndex && choiceIndex >= 0)
{
    var first = args.ElementAtOrDefault(choiceIndex + 1) ?? throw new ArgumentException("First candidate required.");
    var second = args.ElementAtOrDefault(choiceIndex + 2) ?? throw new ArgumentException("Second candidate required.");
    var ask = typeof(ModelClient).GetMethod("AskAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var sourceIndex = Array.IndexOf(args, "--source");
    var source = sourceIndex >= 0 ? args.ElementAtOrDefault(sourceIndex + 1) : null;
    foreach (var (label, instruction) in new[] { ("phrase", settings.PhraseLayoutInstructions),
        ("spelling-choice", settings.SpellingChoiceInstructions), ("context", settings.ContextLayoutInstructions), ("layout", settings.LayoutInstructions) })
        foreach (var input in new[] { first + "\n" + second, second + "\n" + first })
        {
            var prompt = label == "spelling-choice" && source != null ? source + "\n" + input : input;
            var reply = await (Task<string?>)ask.Invoke(client, [instruction, prompt, CancellationToken.None, 128])!;
            Console.WriteLine(label + ": " + input.Replace("\n", " | ") + " -> " + reply);
        }
    return;
}
if (args.Contains("--quality"))
{
    var groupIndex = Array.IndexOf(args, "--group");
    var group = groupIndex >= 0 ? args.ElementAtOrDefault(groupIndex + 1) : null;
    var groups = group?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var results = new List<object>();
    var passed = 0;
    foreach (var item in QualityCases.All().Where(item => groups == null || groups.Any(selected => item.Group == selected || selected.EndsWith('*') && item.Group.StartsWith(selected[..^1]))))
    {
        var watch = Stopwatch.StartNew();
        var actual = await client.CorrectPhraseAsync(item.Input, CancellationToken.None);
        if (args.Contains("--pipeline")) actual = await client.CorrectHyphensAsync(actual, CancellationToken.None);
        string? repeated = null;
        if (args.Contains("--idempotent"))
        {
            repeated = await client.CorrectPhraseAsync(actual, CancellationToken.None);
            if (args.Contains("--pipeline")) repeated = await client.CorrectHyphensAsync(repeated, CancellationToken.None);
        }
        var ok = (actual == item.Expected || item.AcceptedAlternatives?.Contains(actual) == true) && (repeated == null || repeated == actual);
        if (ok) passed++; else Environment.ExitCode = 1;
        results.Add(new { item.Group, item.Input, item.Expected, item.AcceptedAlternatives, Actual = actual, Repeated = repeated, Passed = ok, ElapsedMs = watch.ElapsedMilliseconds });
        var expectedText = string.Join(" or ", new[] { item.Expected }.Concat(item.AcceptedAlternatives ?? []));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [{item.Group}]: {item.Input} -> {actual} (expected {expectedText}) [{watch.ElapsedMilliseconds} ms]");
        if (repeated != null && repeated != actual) Console.WriteLine("DRIFT: " + actual + " -> " + repeated);
    }
    var outputIndex = Array.IndexOf(args, "--output");
    if (outputIndex >= 0 && outputIndex + 1 < args.Length)
        File.WriteAllText(args[outputIndex + 1], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"QUALITY: {passed}/{results.Count} passed");
    if (results.Count == 0) { Console.WriteLine("No quality cases matched the requested groups."); Environment.ExitCode = 2; }
    return;
}
if (args.Contains("--ambiguity"))
{
    foreach (var (input, expected) in new[] {
        ("tot", "tot"), ("tot немного", "еще немного"), ("the little tot laughed", "the little tot laughed"),
        ("полность.", "полностью."), ("полноcnm.", "полностью"), ("полноcnm..", "полностью."),
        ("gjkyjcnm..", "полностью."), ("это gjkyjcnm.. Всё.", "это полностью. Всё."),
        ("это моя ;bpym.", "это моя жизнь."), ("я наслаждаюсь ;bpym.", "я наслаждаюсь жизнью"),
        ("я наслаждаюсь ;bpym..", "я наслаждаюсь жизнью."), ("я ltkf. работу", "я делаю работу"),
        ("как ltkf.", "как дела."), ("я vjk. о помощи", "я молю о помощи"),
        ("f;;fhrj", "аж жарко"), ("F;;fhrj!", "Аж жарко!"), ("f;;fhrj сегодня", "аж жарко сегодня"),
        ("потом", "потом"), ("аж  жарко", "аж  жарко"), ("hello ghbdtn", "hello привет"),
        ("я отдаю свою ljk.", "я отдаю свою долю"), ("я восхищаюсь >yjcnm.", "я восхищаюсь Юностью"),
        ("это >yjcnm.", "это Юность."), ("\"то", "\"то"), ("\"то\"", "\"то\"") })
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        actual = await client.CorrectHyphensAsync(actual, CancellationToken.None);
        Console.WriteLine($"{(actual == expected ? "PASS" : "FAIL")}: {input} -> {actual} (expected {expected})");
        if (actual != expected) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--terminal"))
{
    foreach (var (input, expected) in new[] {
        ("полноcnm.", "полностью"), ("полноcnm. ", "полностью "), ("Поkyjcnm.", "Полностью"),
        ("ds,", "вы,"), ("ds, rfr ltkf", "вы, как дела"), ("Ds,", "Вы,"), ("ds.", "вы."),
        ("вы,", "вы,"), ("we,", "we,"), ("go.", "go."), ("DS,", "DS,"), ("t;", "еж"),
        // Both core and complete mapping are words; a lone final point is
        // retained. The ambiguity suite checks the forms selected by context.
        ("ljk.", "дол."), ("vjk.", "мол."),
        ("полноcnm..", "полностью."), ("полноcnm.!", "полностью!"), ("полноcnm.,", "полностью,"),
        ("это полноcnm. работает", "это полностью работает"), ("[полноcnm.]", "[полностью]"),
        ("зyf.", "знаю"), ("rлу,", "клуб"), ("это полноcnm.. Всё работает.", "это полностью. Всё работает."),
        ("полностью.", "полностью."), ("пойдеv.", "пойдем."), ("привет.", "привет."),
        ("example.com", "example.com"), ("user@полноcnm.", "user@полноcnm."), ("C:\\полноcnm.", "C:\\полноcnm."),
        ("pyf.", "знаю"), ("rke,", "клуб"), ("ghbdtn.", "привет.") })
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        Console.WriteLine($"{(actual == expected ? "PASS" : "FAIL")}: {input} -> {actual}");
        if (actual != expected) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--switch"))
{
    foreach (var (input, expected) in new[] {
        ("ghbrjkmyjо", "прикольно"), ("ghbrjkmyо", "прикольно"), ("Ghbrjkmyjо", "Прикольно"),
        ("ghbrjkmyjо то что можно переключаться прям на лету", "прикольно то что можно переключаться прям на лету"),
        ("ghbrольно", "прикольно"), ("прикольyj", "прикольно"), ("hellщ", "hello"), ("руддo", "hello"),
        ("пойдеv", "пойдем"), ("начинаетcz", "начинается"), ("прибыkи", "прибыли"),
        ("прикольно", "прикольно"), ("hello", "hello"), ("какой-то", "какой-то"),
        ("id_ghbrjkmyjо", "id_ghbrjkmyjо"), ("user@ghbrjkmyjо.com", "user@ghbrjkmyjо.com") })
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        Console.WriteLine($"{(actual == expected ? "PASS" : "FAIL")}: {input} -> {actual}");
        if (actual != expected) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--boundaries"))
{
    var boundaryCases = new List<(string Input, string Expected)>();
    foreach (var separator in new[] { ".", ",", ";", ":", "!", "?", "...", "…", "—", "–" })
        foreach (var (input, expected) in new[] { ("ghbdtn", "привет"), ("Ghbdtn", "Привет"), ("привет", "привет") })
            boundaryCases.Add(("вишни" + separator + input, "вишни" + separator + expected));
    foreach (var (open, close) in new[] { ("(", ")"), ("[", "]"), ("{", "}"), ("«", "»"), ("\"", "\"") })
        boundaryCases.Add((open + "ghbdtn" + close, open + "привет" + close));
    boundaryCases.AddRange(new[] { ("ghbdtn\u00a0rfr", "привет\u00a0как"), ("ghbdtn\trfr", "привет\tкак"),
        ("pyf.", "знаю"), (";bpym", "жизнь"), ("об]явление", "объявление") });
    foreach (var value in new[] { "https://example.com/ghbdtn", "user@ghbdtn.com", "C:\\temp\\ghbdtn", "example.COM", "пример.рф", "1.2.3", "user_ghbdtn", "как-то", "don't" })
        boundaryCases.Add((value, value));
    foreach (var (input, expected) in boundaryCases)
    {
        var watch = Stopwatch.StartNew();
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        Console.WriteLine($"{(actual == expected ? "PASS" : "FAIL")}: {input} -> {actual} [{watch.ElapsedMilliseconds} ms]");
        if (actual != expected) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--sentence"))
{
    const string prefix = "Мама мыла раму. Рама была чистая. Наступило теплое лето. В саду созрели сладкие яблони и вишни.";
    const string tail = "Vfktymrbq rjntyjr dtctkj buhfk c ,tksv rke,rjv ybnjr yf ковер.";
    const string correctedTail = "Маленький котенок весело играл с белым клубком ниток на ковер.";
    foreach (var (input, expected) in new[] {
        (prefix + tail, prefix + correctedTail), (prefix + " " + tail, prefix + " " + correctedTail),
        ("вишни.Vfktymrbq rjntyjr", "вишни.Маленький котенок"),
        ("https://пример.COM/path user@example.com C:\\текст.Vfktymrbq пример.рф 1.2.3", "https://пример.COM/path user@example.com C:\\текст.Vfktymrbq пример.рф 1.2.3") })
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        Console.WriteLine($"{(actual == expected ? "PASS" : "FAIL")}: {input} -> {actual}");
        if (actual != expected) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--double-letters"))
{
    var stagedForm = await client.CorrectPhraseAsync("pfvtys ", CancellationToken.None, "ок скорость ");
    Console.WriteLine($"{(stagedForm == "замены " ? "PASS" : "FAIL")}: staged inflection -> {stagedForm}");
    if (stagedForm != "замены ") Environment.ExitCode = 1;
    var stagedEnding = await client.CorrectPhraseAsync("ltkf", CancellationToken.None, "привет, как ");
    Console.WriteLine($"{(stagedEnding == "дела" ? "PASS" : "FAIL")}: staged ending -> {stagedEnding}");
    if (stagedEnding != "дела") Environment.ExitCode = 1;
    var doubleCases = new List<(string Input, string Expected)> {
        ("pfvtys", "замены"), ("ntgkjt", "теплое"),
        ("ghbdt, rfr ltkf", "привет, как дела"), ("ghbdt, rfr ", "привет, как "), ("ghbdt,", "привет,"),
        ("Yfccnegbkj", "Наступило"), ("Yfccnegbkj ntgkjt ktnj", "Наступило теплое лето"),
        ("Насступило теплое лето", "Наступило теплое лето") };
    foreach (var word in new[] { "класс", "касса", "ссора", "рассказ", "суббота", "Россия", "аллея" })
    { doubleCases.Add((word, word)); doubleCases.Add((KeyboardLayout.Convert(word), word)); }
    foreach (var word in new[] { "coffee", "letter", "book" }) doubleCases.Add((word, word));
    foreach (var (input, expected) in doubleCases)
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        var passed = actual == expected;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {input} -> {actual} (expected {expected})");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--paragraph"))
{
    const string first = "Vfvf vskf hfve/ Hfvf ,skf xbcnfz/ Yfnegbkj ntgkjt ktnj/";
    const string expectedFirst = "Мама мыла раму. Рама была чистая. Наступило теплое лето.";
    const string last = "В саду созрели сладкие яблони и вишни.";
    foreach (var (input, expected) in new[] {
        (first, expectedFirst), (first + " D cfle cjphtkb ckflrbt z,kjyb b dbiyb/", expectedFirst + " " + last),
        ("В саду созрели сладкие яблони b dbiyb/", last), (expectedFirst + " " + last, expectedFirst + " " + last),
        ("hello/ some/path/ https://example.com/", "hello/ some/path/ https://example.com/") })
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        var passed = actual == expected;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {input} -> {actual}");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--standalone"))
{
    foreach (var (input, expected) in new[] {
        ("ntrcn", "текст"), ("ntrcn ", "текст "), ("phz", "зря"), ("ehjr", "урок"),
        ("ghbdtn", "привет"), ("pfvtys", "замены"), ("текст", "текст"), ("пробный", "пробный"),
        ("hello", "hello"), ("world", "world"), ("text", "text"), ("test", "test"), ("python", "python"),
        ("rhythm", "rhythm"), ("myths", "myths"), ("http", "http"), ("HTML", "HTML"), ("DNS", "DNS"),
        ("NL100", "NL100"), ("hello ghj,ysq", "hello пробный"), ("ntrcn ghj,ysq", "текст пробный") })
    {
        var actual = await client.CorrectPhraseAsync(input, CancellationToken.None);
        var passed = actual == expected;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {input} -> {actual} (expected {expected})");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--letter-keys"))
{
    var letterCases = new (string Input, string Expected)[]
    {
        ("[jчу", "хочу"), ("[очу", "хочу"), ("'тj", "это"), ("'то", "это"),
        (";bву", "живу"), (";иву", "живу"), (",eду", "буду"), (",уду", "буду"),
        (".kя", "юля"), (".бка", "юбка"), ("`жbr", "ёжик"), ("`жик", "ёжик"),
        ("об]zвление", "объявление"), ("об]явление", "объявление"),
        ("привет,", "привет,"), ("сто.", "сто."), ("'привет", "'привет"), ("[привет", "[привет"),
        (".ля", ".ля") // Conflicting model choices must preserve an ambiguous fragment.
    };
    foreach (var item in letterCases)
    {
        var corrected = await client.CorrectPhraseAsync(item.Input, CancellationToken.None);
        corrected = await client.CorrectHyphensAsync(corrected, CancellationToken.None);
        var passed = corrected == item.Expected;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {item.Input} -> {corrected} (expected {item.Expected})");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--latency"))
{
    foreach (var phrase in new[] { "yj bcghfdkztn", "ghbdtn", "f что если yt ghjlflen" })
    {
        using var fresh = new ModelClient(settings, spellingHints: spellingHints);
        for (var pass = 0; pass < 2; pass++)
        {
            var watch = Stopwatch.StartNew();
            var corrected = await fresh.CorrectPhraseAsync(phrase, CancellationToken.None);
            Console.WriteLine($"{phrase} -> {corrected} [{watch.ElapsedMilliseconds} ms, {(pass == 0 ? "first" : "repeat")}]");
        }
    }
    return;
}
if (Array.IndexOf(args, "--text") is var textIndex && textIndex >= 0 && textIndex + 1 < args.Length)
{
    var watch = Stopwatch.StartNew();
    string Context(string flag) => Array.IndexOf(args, flag) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    var corrected = await client.CorrectPhraseAsync(args[textIndex + 1], CancellationToken.None, Context("--left"), Context("--right"));
    Console.WriteLine(args.Contains("--hyphens") ? await client.CorrectHyphensAsync(corrected, CancellationToken.None) : corrected);
    Console.WriteLine($"Elapsed: {watch.ElapsedMilliseconds} ms");
    return;
}
if (args.Contains("--mixed"))
{
    foreach (var phrase in new[] { "пойдеv ", "а может в замок пойдеv ", "пойдеv, ", "hellщ ", "ghbdtn ", "hello world ", "привет мир ", "NL100 HelloWorld test_name " })
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine(phrase + "-> " + await client.CorrectPhraseAsync(phrase, CancellationToken.None) + $" [{watch.ElapsedMilliseconds} ms]");
    }
    return;
}
if (args.Contains("--phrase"))
{
    foreach (var phrase in new[] { "gjqltnv ", "а может в замок gjqltnv ", "привет привет f vj;tn ,snm пойдем d fpvjr ", "f vj;tn ,snm ", "пойдем d fpvjr ", "hello world python ", "привет hello world ", "прывет ghbdtn руддщ ", "hello, world! " })
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine(phrase + "-> " + await client.CorrectPhraseAsync(phrase, CancellationToken.None) + $" [{watch.ElapsedMilliseconds} ms]");
    }
    return;
}
var cases = new (string input, string expected)[] {
    ("pfvtys", "замены"), ("wtyt", "цене"), ("прывет", "привет"), ("ghbdtn", "привет"), ("руддщ", "hello"), ("python", "python"),
    ("првиет", "привет"), ("helllo", "hello"),
    ("спасбио", "спасибо"), ("пожалуста", "пожалуйста"), ("севодня", "сегодня"),
    ("завтро", "завтра"), ("хоршо", "хорошо"), ("друзя", "друзья"), ("интиресно", "интересно"),
    ("проврека", "проверка"), ("програма", "программа"), ("компютер", "компьютер"),
    ("recieve", "receive"), ("teh", "the"), ("becuase", "because"), ("freind", "friend"),
    ("tomorow", "tomorrow"), ("thnaks", "thanks"), ("keybaord", "keyboard"),
    ("hello", "hello"), ("world", "world"), ("river", "river"), ("fold", "fold"),
    ("привет", "привет"), ("сегодня", "сегодня"), ("слово", "слово"), ("друзья", "друзья"),
    ("казалось", "казалось"), ("текст", "текст"), ("лол", "лол")
};
var rows = new List<object>();
int matches = 0, destructive = 0;
var timings = new List<long>();
foreach (var (input, expected) in cases)
{
    var watch = Stopwatch.StartNew();
    var result = await client.CorrectAsync(input, CancellationToken.None);
    watch.Stop();
    var actual = result.Replacement ?? input;
    var correct = actual == expected;
    if (correct) matches++;
    if (input == expected && actual != input) destructive++;
    timings.Add(watch.ElapsedMilliseconds);
    rows.Add(new { input, expected, actual, milliseconds = watch.ElapsedMilliseconds, correct });
    Console.WriteLine($"{input} -> {actual} [{watch.ElapsedMilliseconds} ms] {(correct ? "OK" : "MISS")}");
}
Directory.CreateDirectory("artifacts");
File.WriteAllText("artifacts/model-benchmark.json", JsonSerializer.Serialize(new { settings.Model, settings.Backend,
    Date = DateTimeOffset.Now, matches, total = cases.Length, destructive, medianMs = timings.Order().ElementAt(timings.Count / 2), rows },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Matches: {matches}/{cases.Length}; changed correct words: {destructive}; median: {timings.Order().ElementAt(timings.Count / 2)} ms.");

sealed class TraceHandler : DelegatingHandler
{
    public TraceHandler() : base(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Console.WriteLine("REQUEST " + await request.Content!.ReadAsStringAsync(token));
        var response = await base.SendAsync(request, token);
        Console.WriteLine("RESPONSE " + await response.Content.ReadAsStringAsync(token));
        return response;
    }
}
