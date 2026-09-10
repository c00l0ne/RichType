using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Kazrich.App;
using Kazrich.Core;
using WpfTextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.Contains("--native-checks"))
        {
            try { Task.Run(NativeSpellingChecks.Run).GetAwaiter().GetResult(); }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
        }
        else if (args.Contains("--instructions-qa")) InstructionsChecks.Run();
        else if (args.Contains("--long-capture")) Application.Run(new Fixture { ExtraScenario = "long-capture" });
        else if (args.Contains("--capture-races")) CaptureRaceChecks.Run();
        else if (args.Contains("--controller-lifecycle"))
        {
            var host = new Form { ShowInTaskbar = false, Width = 1, Height = 1 };
            host.Shown += async (_, _) =>
            {
                host.Hide();
                try { await ControllerLifecycleChecks.Run(); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        else if (args.Contains("--main-lifecycle")) MainLifecycleChecks.Run();
        else if (args.Contains("--main-save-failure")) MainLifecycleChecks.RunSaveFailure();
        else if (args.Contains("--network-checks"))
        {
            try { Task.Run(NetworkChecks.Run).GetAwaiter().GetResult(); }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
        }
        else if (args.Contains("--managed-context"))
        {
            try { Task.Run(ManagedContextChecks.Run).GetAwaiter().GetResult(); }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
        }
        else if (args.Contains("--managed-lifecycle"))
        {
            try { Task.Run(ManagedLifecycleChecks.Run).GetAwaiter().GetResult(); }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
        }
        else if (args.Contains("--real-cpu-input"))
        {
            try
            {
                var settings = SettingsFile.Load() with { Backend = "Managed", Model = "qwen3.5:2b", CpuOnly = true, CpuThreads = 2 };
                using var managed = new ManagedModel();
                using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                if (!managed.IsInstalled(settings.Model, cpuOnly: true)) throw new InvalidOperationException("The CPU input check requires the already installed 2B model.");
                Task.Run(() => managed.PrepareAndStartAsync(settings, false, new Progress<string>(Console.WriteLine), startup.Token)).GetAwaiter().GetResult();
                Application.Run(new Fixture(managed.ConnectionSettings(settings).Endpoint) { ExtraScenario = "cpu-input" });
            }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
        }
        else if (args.Contains("--managed-install"))
        {
            Task.Run(async () =>
            {
                using var managed = new ManagedModel();
                var settings = new Settings { CpuOnly = !args.Contains("--gpu") };
                await managed.PrepareAndStartAsync(settings, true, new Progress<string>(Console.WriteLine), CancellationToken.None);
                using var client = new ModelClient(managed.ConnectionSettings(settings));
                foreach (var phrase in new[] { "yj bcghfdkztn", "f что если yt ghjlflen", "pfvtys" })
                {
                    var watch = Stopwatch.StartNew();
                    var corrected = await client.CorrectPhraseAsync(phrase, CancellationToken.None);
                    Console.WriteLine($"{phrase} -> {corrected} [{watch.ElapsedMilliseconds} ms]");
                }
                foreach (var word in new[] { "прывет", "ghbdtn", "руддщ", "hello", "world" })
                {
                    var result = await client.CorrectAsync(word, CancellationToken.None);
                    Console.WriteLine(word + " -> " + (result.Replacement ?? word));
                }
                Console.WriteLine("MANAGED START/CORRECT/DISPOSE COMPLETED");
            }).GetAwaiter().GetResult();
        }
        else if (args.Contains("--real-chrome") || args.Contains("--chrome-fields"))
        {
            var host = new Form { Text = "Kazrich Chrome check", ShowInTaskbar = false, Width = 1, Height = 1 };
            host.Shown += async (_, _) =>
            {
                host.Hide();
                try
                {
                    if (args.Contains("--chrome-fields")) await ChromeFieldsChecks.Run(host, args.Last());
                    else await ChromeChecks.Run(host, args.Last());
                }
                catch (Exception e) { Console.WriteLine(e); Environment.ExitCode = 1; }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        else if (args.Contains("--real-main"))
        {
            var input = args.Contains("--phz") ? "dhtvz phz gjnhfnbkb" : "pfdnhf, d irjkf";
            var expected = args.Contains("--phz") ? "время зря потратили" : "завтра, в школа";
            var settings = SettingsFile.Load() with { Backend = "OpenAI", Endpoint = args.Last(), Enabled = true, DelayMs = 250 };
            var form = new MainForm(settings, false) { TopMost = true };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var controllerField = typeof(MainForm).GetField("controller", flags)!;
            var editor = (WpfTextBox)typeof(MainForm).GetField("typing", flags)!.GetValue(form)!;
            var status = (Label)typeof(MainForm).GetField("status", flags)!.GetValue(form)!;
            form.Shown += async (_, _) =>
            {
                try
                {
                    ((CorrectionController)controllerField.GetValue(form)!).Dispose();
                    var controller = new CorrectionController(form, settings, acceptSyntheticTestInput: true);
                    controllerField.SetValue(form, controller);
                    controller.Status += value => { status.Text = value; Console.WriteLine(value); };
                    Native.ShowWindow(form.Handle, 5);
                    form.Activate(); Native.SetForegroundWindow(form.Handle);
                    editor.Focus(); System.Windows.Input.Keyboard.Focus(editor);
                    await Task.Delay(150);
                    foreach (var keyDelay in new[] { 0, 5, 15, 40, 80 })
                    {
                        controller.Reset(); editor.Clear(); editor.Focus();
                        foreach (var c in input)
                        {
                            TestInput.PhysicalText(c.ToString(), "00000409");
                            if (keyDelay > 0) await Task.Delay(keyDelay);
                        }
                        var deadline = Environment.TickCount64 + 6000;
                        while ((editor.Text != expected || editor.CaretIndex != editor.Text.Length) && Environment.TickCount64 < deadline)
                            await Task.Delay(25);
                        Console.WriteLine($"MAIN {keyDelay} ms: {editor.Text} [caret {editor.CaretIndex}/{editor.Text.Length}]");
                        if (editor.Text != expected || editor.CaretIndex != editor.Text.Length)
                            throw new Exception("Actual main form skipped part of the reported phrase.");
                    }
                    foreach (var clearMode in new[] { "selection", "selection-backspace", "backspace" })
                    foreach (var keyDelay in new[] { 0, 5, 15, 40 })
                    {
                        if (clearMode == "backspace")
                        {
                            var length = editor.Text.Length;
                            for (var i = 0; i < length; i++) TestInput.Key(8);
                        }
                        else
                        {
                            TestInput.Chord(0x11, 0x41);
                            if (clearMode == "selection-backspace") TestInput.Key(8);
                        }
                        foreach (var c in input)
                        {
                            TestInput.PhysicalText(c.ToString(), "00000409");
                            if (keyDelay > 0) await Task.Delay(keyDelay);
                        }
                        await Task.Delay(75);
                        var deadline = Environment.TickCount64 + 6000;
                        while ((editor.Text != expected || editor.CaretIndex != editor.Text.Length) && Environment.TickCount64 < deadline)
                            await Task.Delay(25);
                        Console.WriteLine($"MAIN {clearMode} {keyDelay} ms: {editor.Text} [caret {editor.CaretIndex}/{editor.Text.Length}]");
                        if (editor.Text != expected || editor.CaretIndex != editor.Text.Length)
                            throw new Exception("Replacing the selected text in the main form skipped part of the phrase.");
                    }
                    Console.WriteLine("ALL ACTUAL MAIN FORM CHECKS PASSED");
                }
                catch (Exception e) { Console.WriteLine(e); Environment.ExitCode = 1; }
                finally
                {
                    typeof(MainForm).GetField("exit", flags)!.SetValue(form, true);
                    form.Close();
                }
            };
            Application.Run(form);
        }
        else if (args.Contains("--native-fixture"))
        {
            var form = new Form { Text = "Kazrich isolated external RichEdit", Width = 650, Height = 230, TopMost = true };
            var editor = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 18), Name = "ExternalTypingArea" };
            form.Controls.Add(editor);
            form.Shown += (_, _) => { Native.ShowWindow(form.Handle, 5); form.Activate(); editor.Focus(); };
            Application.Run(form);
        }
        else if (args.Contains("--screenshot"))
        {
            Form form = args.Contains("--instructions")
                ? new InstructionsForm(new Settings()) { TopMost = true }
                : new MainForm(new Settings { Enabled = false, CpuOnly = !args.Contains("--gpu") }, false) { TopMost = true };
            form.Shown += async (_, _) =>
            {
                Native.ShowWindow(form.Handle, 5);
                form.BringToFront(); form.Activate();
                if (args.Contains("--advanced")) ((CheckBox)form.Controls.Find("AdvancedSettings", true).Single()).Checked = true;
                await Task.Delay(2500);
                Directory.CreateDirectory("artifacts");
                using var bitmap = new Bitmap(form.Width, form.Height);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(form.Location, Point.Empty, form.Size);
                bitmap.Save("artifacts/app-window.png");
                Application.Exit();
            };
            Application.Run(form);
        }
        else if (Array.IndexOf(args, "--real-staged-case") is var stagedIndex && stagedIndex >= 0)
        {
            if (args.Length <= stagedIndex + 5) throw new ArgumentException("Use --real-staged-case first firstExpected continuation expected endpoint.");
            Application.Run(new Fixture(args[stagedIndex + 5])
            {
                ExtraScenario = "staged-case",
                StagedCase = args.Skip(stagedIndex + 1).Take(4).ToArray()
            });
        }
        else if (Array.IndexOf(args, "--real-case") is var caseIndex && caseIndex >= 0)
        {
            if (args.Length <= caseIndex + 3) throw new ArgumentException("Use --real-case source expected endpoint.");
            Application.Run(new Fixture(args[caseIndex + 3])
            {
                ExtraScenario = "reported-case",
                ReportedCases = [(args[caseIndex + 1], args[caseIndex + 2])]
            });
        }
        else if (args.Contains("--real-long-tail")) Application.Run(new Fixture(args.Last()) { ExtraScenario = "long-tail" });
        else if (args.Contains("--unicode-ui")) Application.Run(new Fixture { ExtraScenario = "unicode" });
        else Application.Run(new Fixture(args.Contains("--real-phrase") || args.Contains("--real-context") || args.Contains("--real-input") || args.Contains("--real-paragraph") || args.Contains("--real-sentence") || args.Contains("--real-switch") || args.Contains("--real-quality") ? args.Last() : null, args.Contains("--real-context"), args.Contains("--real-input"), args.Contains("--real-paragraph"), args.Contains("--real-sentence"), args.Contains("--real-switch"), args.Contains("--real-quality"), args.Contains("--network-ui")));
    }
}

internal sealed class Fixture : Form
{
    internal string ExtraScenario = "";
    internal (string Source, string Expected)[]? ReportedCases;
    internal string[]? StagedCase;
    private readonly string? realEndpoint;
    private readonly bool onlyContext;
    private readonly bool onlyInput;
    private readonly bool onlyParagraph;
    private readonly bool onlySentence;
    private readonly bool onlySwitch;
    private readonly bool onlyQuality;
    private readonly bool onlyNetwork;
    private string lastStatus = "";
    private readonly WpfTextBox field = new(), second = new();
    private readonly System.Windows.Controls.PasswordBox password = new();
    private readonly FakeServer server = new();
    private readonly List<string> results = new();
    private CorrectionController controller = null!;
    private Settings settings = null!;
    internal Fixture(string? realEndpoint = null, bool onlyContext = false, bool onlyInput = false, bool onlyParagraph = false, bool onlySentence = false, bool onlySwitch = false, bool onlyQuality = false, bool onlyNetwork = false)
    {
        this.realEndpoint = realEndpoint;
        this.onlyContext = onlyContext;
        this.onlyInput = onlyInput;
        this.onlyParagraph = onlyParagraph;
        this.onlySentence = onlySentence;
        this.onlySwitch = onlySwitch;
        this.onlyQuality = onlyQuality;
        this.onlyNetwork = onlyNetwork;
        Text = "Kazrich: isolated UI integration test";
        Size = new Size(720, 410);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Автоматическая проверка собственного тестового окна", Dock = DockStyle.Fill }, 0, 0);
        foreach (var text in new[] { field, second })
        {
            text.FontSize = 24;
            text.AcceptsReturn = true;
            AutomationProperties.SetAutomationId(text, "KazrichTypingArea");
        }
        layout.Controls.Add(new ElementHost { Child = field, Dock = DockStyle.Fill, Height = 85 }, 0, 1);
        layout.Controls.Add(new ElementHost { Child = second, Dock = DockStyle.Fill, Height = 85 }, 0, 2);
        AutomationProperties.SetAutomationId(password, "KazrichTypingArea");
        layout.Controls.Add(new ElementHost { Child = password, Dock = DockStyle.Fill, Height = 60 }, 0, 3);
        Shown += async (_, _) => await Run();
    }
    private void Check(bool condition, string name)
    {
        var line = (condition ? "PASS: " : "FAIL: ") + name;
        results.Add(line); Console.WriteLine(line);
        if (!condition) Console.WriteLine("Isolated test field: " + field.Text + $" [caret {field.CaretIndex}/{field.Text.Length}]");
        if (!condition) throw new Exception(name);
    }
    private async Task Prepare(string text = "")
    {
        controller.Reset();
        Native.ShowWindow(Handle, 5);
        Activate();
        Native.SetForegroundWindow(Handle);
        var testHandle = Handle;
        await Task.Run(() => AutomationElement.FromHandle(testHandle).SetFocus());
        field.Text = text;
        field.CaretIndex = field.Text.Length;
        field.Focus();
        System.Windows.Input.Keyboard.Focus(field);
        await Task.Delay(100);
    }
    private async Task WaitFor(Func<bool> condition, int milliseconds = 3500)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(25);
    }
    private async Task CheckReportedInput(bool cpuSmoke = false)
    {
        controller.Configure(settings with { DelayMs = 250 });
        const string prefix = "Сохранённый абзац.\n";
        var cpuCases = new[] { "ds,", "полноcnm.", ",.l;tnf", "я ltkf. работу", "жжираф", "colour", "coluor", "realsie" };
        var cases = ReportedCases ?? new[] {
                ("ds,", "вы,"), ("ds.", "вы."), ("ds, rfr ltkf", "вы, как дела"),
                ("полноcnm.", "полностью"), ("полноcnm..", "полностью."),
                ("ljdjklbv еще", "доводим еще"), ("t;bltn", "еж идет"), ("f;;fhrj", "аж жарко"),
                (",.l;tnf", "бюджета"), (".,ка", "юбка"), ("rke,rjv", "клубком"),
                ("я наслаждаюсь ;bpym..", "я наслаждаюсь жизнью."),
                ("я ltkf. работу", "я делаю работу"), ("как ltkf.", "как дела."),
                (":bpym", "Жизнь"), ("{jxe", "Хочу"), ("{очу", "Хочу"), ("\"nj", "Это"),
                (">yjcnm.", "Юность."), ("Ёkrf", "Ёлка"), ("~krf", "Ёлка"), ("<.l;tn", "Бюджет"),
                ("я отдаю свою ljk.", "я отдаю свою долю"),
                ("\"ghbdtn", "\"привет"), ("helllo\"", "hello\""), ("'пойдеv", "'пойдем"),
                ("[ghbdtn", "[привет"), ("ghbdtn]", "привет]"), ("{helllo", "{hello"),
                ("'[jxe", "'хочу"), ("\":bpym", "\"Жизнь"), ("(,.l;tnf", "(бюджета"),
                ("жжираф", "жираф"), ("ёёлочка", "ёлочка"), ("walk throough the door", "walk through the door"),
                ("a throough check", "a thorough check"), ("please seveer the connection", "please sever the connection"),
                ("colour", "colour"), ("color", "color"), ("organise", "organise"), ("organize", "organize"),
                ("licence", "licence"), ("license", "license"), ("coluor", "colour"), ("orgnaise", "organise"),
                ("realsie", "realise"), ("neihgbour", "neighbour") };
        foreach (var keyDelay in cpuSmoke ? new[] { 0, 80 } : new[] { 0, 80, 150, 250 })
            foreach (var (source, expected) in cases)
            {
                if (cpuSmoke && !cpuCases.Contains(source)) continue;
                await Prepare(prefix);
                string? previousLayout = null;
                foreach (var c in source)
                {
                    if (Native.GetForegroundWindow() != Handle || !field.IsKeyboardFocused)
                        throw new InvalidOperationException("The typing check stopped because its own field lost focus.");
                    var layout = CorrectionRules.IsRussianLetter(c) ? "00000419" : "00000409";
                    // SendInput queues key messages. Let the previous alphabet reach WPF
                    // before changing the receiving thread's keyboard layout.
                    if (keyDelay == 0 && previousLayout != null && previousLayout != layout)
                        await Task.Delay(25);
                    TestInput.PhysicalText(c.ToString(), layout);
                    previousLayout = layout;
                    if (keyDelay > 0) await Task.Delay(keyDelay);
                }
                if (keyDelay == 0)
                {
                    await Task.Delay(25);
                    Check(TypingQueue.Normalize(field.Text) == prefix + source,
                        $"physical mixed-layout test delivered exact source: {source}");
                }
                if (source == expected) await Task.Delay(850);
                await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + expected && field.CaretIndex == field.Text.Length, cpuSmoke ? 30000 : 15000);
                Check(TypingQueue.Normalize(field.Text) == prefix + expected && field.CaretIndex == field.Text.Length,
                    $"reported correction preserves previous paragraph and caret at {keyDelay} ms: {source}");
                if (!source.Any(char.IsWhiteSpace))
                {
                    controller.Undo();
                    await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + source, 5000);
                    Check(TypingQueue.Normalize(field.Text) == prefix + source && field.CaretIndex == field.Text.Length,
                        $"reported correction undo restores exact original keys at {keyDelay} ms: {source}");
                }
            }
        if (cpuSmoke)
        {
            await Prepare(prefix);
            lastStatus = "";
            TestInput.Text("клавитура ");
            await WaitFor(() => lastStatus.StartsWith("Проверяю очередь"), 5000);
            Check(lastStatus.StartsWith("Проверяю очередь"), "the CPU pipeline starts before more text is appended");
            TestInput.Text("работает ");
            await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "клавиатура работает " && field.CaretIndex == field.Text.Length, 30000);
            Check(TypingQueue.Normalize(field.Text) == prefix + "клавиатура работает " && field.CaretIndex == field.Text.Length,
                "CPU correction preserves the later input and exact caret while its model request is running");
        }
    }
    private async Task CheckStagedCase()
    {
        var values = StagedCase ?? throw new InvalidOperationException("Staged case data is missing.");
        controller.Configure(settings with { DelayMs = 250 });
        const string prefix = "Сохранённый абзац.\n";
        await Prepare(prefix);
        if (Native.GetForegroundWindow() != Handle || !field.IsKeyboardFocused) throw new InvalidOperationException("The staged test field lost focus.");
        TestInput.PhysicalText(values[0], "00000409");
        await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + values[1], 20000);
        Check(TypingQueue.Normalize(field.Text) == prefix + values[1] && field.CaretIndex == field.Text.Length,
            "the first typing stage is corrected before its later neighbour exists");
        await Task.Delay(800);
        Check(TypingQueue.Normalize(field.Text) == prefix + values[1], "the paused first stage stays stable");
        if (Native.GetForegroundWindow() != Handle || !field.IsKeyboardFocused) throw new InvalidOperationException("The staged test field lost focus.");
        TestInput.PhysicalText(values[2], "00000409");
        if (values[3] == values[1] + values[2]) await Task.Delay(1500);
        await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + values[3], 20000);
        Check(TypingQueue.Normalize(field.Text) == prefix + values[3] && field.CaretIndex == field.Text.Length,
            "a later word resolves the earlier word without losing the paragraph or caret");
        if (values[3] != values[1] + values[2])
        {
            controller.Undo();
            await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + values[1] + values[2], 5000);
            Check(TypingQueue.Normalize(field.Text) == prefix + values[1] + values[2] && field.CaretIndex == field.Text.Length,
                "undo restores both the previously deferred word and the new raw input");
        }
    }

    private async Task CheckLongFollowingInput()
    {
        const string prefix = "Сохранённый абзац.\n";
        var tail = string.Concat(Enumerable.Range(0, 1800).Select(index => (char)('а' + (index * 13 + index * index * 7) % 32)));
        controller.Configure(settings with { DelayMs = 250 });
        await Prepare(prefix);
        var longInputWatch = Stopwatch.StartNew();
        TestInput.Text("ghbdtn " + tail);
        await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "привет " + tail && field.CaretIndex == field.Text.Length, 20000);
        if (TypingQueue.Normalize(field.Text) != prefix + "привет " + tail)
        {
            var lateCapture = await Task.Run(() => TextTarget.Capture(Handle, "ghbdtn " + tail, settings, ownTest: true));
            Console.WriteLine($"LONG TAIL DIAGNOSTIC: exact raw input={TypingQueue.Normalize(field.Text) == prefix + "ghbdtn " + tail}, late capture={lateCapture != null}");
        }
        Check(TypingQueue.Normalize(field.Text) == prefix + "привет " + tail && field.CaretIndex == field.Text.Length,
            "a 1800-character following token cannot block correction or lose any tail characters");
        Console.WriteLine("Long-tail correction completed after " + longInputWatch.ElapsedMilliseconds + " ms");
        longInputWatch.Restart();
        controller.Undo();
        await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "ghbdtn " + tail && field.CaretIndex == field.Text.Length, 30000);
        Console.WriteLine("Long-tail undo wait: " + longInputWatch.ElapsedMilliseconds + " ms");
        Check(TypingQueue.Normalize(field.Text) == prefix + "ghbdtn " + tail && field.CaretIndex == field.Text.Length,
            "undo restores the short word while retaining the entire 1800-character following token");
    }
    private async Task CheckUnicodeInput()
    {
        const string prefix = "🙂 Café العربية\n";
        controller.Configure(settings);
        await Prepare(prefix);
        TestInput.Text("првиет ");
        await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "привет ");
        Check(TypingQueue.Normalize(field.Text) == prefix + "привет " && field.CaretIndex == field.Text.Length,
            "correction after pre-existing emoji, accented and Arabic text preserves that entire prefix");
        foreach (var unsupported in new[] { "🙂", "a\u0301", "é", "日本語", "👨‍👩‍👧" })
        {
            controller.Configure(settings);
            await Prepare(prefix);
            server.Delay = 600;
            var calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text(unsupported + " ");
            await Task.Delay(850);
            Check(TypingQueue.Normalize(field.Text) == prefix + "првиет " + unsupported + " " && field.CaretIndex == field.Text.Length,
                "unsupported Unicode invalidates a pending correction without damaging text: " + unsupported);
            server.Delay = 0;
            TestInput.Text("helllo ");
            await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "првиет " + unsupported + " hello ");
            Check(TypingQueue.Normalize(field.Text) == prefix + "првиет " + unsupported + " hello " && field.CaretIndex == field.Text.Length,
                "a supported word after the next boundary resumes correction: " + unsupported);
        }
        foreach (var separator in new[] { "\u00a0", "—", "«", "⭐" })
        {
            controller.Configure(settings);
            await Prepare(prefix);
            server.Delay = 600;
            var calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text(separator + "helllo ");
            server.Delay = 0;
            await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "привет " + separator + "hello ", 7000);
            Check(TypingQueue.Normalize(field.Text) == prefix + "привет " + separator + "hello " && field.CaretIndex == field.Text.Length,
                "catch-up retains Unicode punctuation and the later word: " + separator);
        }
        controller.Configure(settings);
        await Prepare("عربي: ");
        field.FlowDirection = System.Windows.FlowDirection.RightToLeft;
        var rtlTarget = await Task.Run(() => TextTarget.Capture(Handle, "عربي: ", settings, ownTest: true));
        Check(rtlTarget == null, "right-to-left fields are excluded before selecting a replacement range");
        var beforeCalls = server.Calls;
        TestInput.Text("првиет tail ");
        await Task.Delay(1200);
        Check(field.Text == "عربي: првиет tail " && field.CaretIndex == field.Text.Length && server.Calls == beforeCalls,
            "right-to-left input is retained exactly and never sent for correction");
        field.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        controller.Configure(settings);
        await Prepare("عربي: ");
        server.Delay = 600;
        beforeCalls = server.Calls;
        TestInput.Text("првиет ");
        await WaitFor(() => server.Calls > beforeCalls);
        field.FlowDirection = System.Windows.FlowDirection.RightToLeft;
        TestInput.Text("tail ");
        server.Delay = 0;
        await Task.Delay(2000);
        Check(field.Text == "عربي: првиет tail " && field.CaretIndex == field.Text.Length,
            "changing text direction during inference invalidates the replacement without altering the field");
        field.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        TestInput.Text("helllo ");
        await WaitFor(() => field.Text == "عربي: првиет tail hello ");
        Check(field.Text == "عربي: првиет tail hello " && field.CaretIndex == field.Text.Length,
            "normal correction resumes after returning to left-to-right flow");
    }
    private async Task CheckLongCapture()
    {
        controller.Configure(settings with { Enabled = false });
        const string prefix = "Сохранённый абзац.\n";
        var suffix = "ghbdtn " + new string('а', 600);
        var multiline = "ghbdtn \n" + new string('а', 600);
        foreach (var (text, expectedSuffix, caret, expected, readOnly) in new[] {
            (prefix + suffix, suffix, prefix.Length + suffix.Length, true, false),
            (prefix + suffix + " later", suffix, prefix.Length + suffix.Length, true, false),
            (prefix + suffix + " later", suffix, prefix.Length + suffix.Length + 6, false, false),
            (prefix + suffix + "\n" + suffix, suffix, prefix.Length + 2 * suffix.Length + 1, true, false),
            (prefix + suffix + "\n" + suffix, suffix, prefix.Length + suffix.Length, true, false),
            (prefix + suffix[..^1] + "б", suffix, prefix.Length + suffix.Length, false, false),
            ("prefix" + suffix, suffix, 6 + suffix.Length, false, false),
            ("user@" + suffix, suffix, 5 + suffix.Length, false, false),
            (prefix + multiline, multiline, prefix.Length + multiline.Length, true, false),
            (prefix + suffix, suffix, prefix.Length + suffix.Length, false, true) })
        {
            field.IsReadOnly = readOnly;
            await Prepare(text);
            field.CaretIndex = caret;
            var started = Stopwatch.StartNew();
            var target = await Task.Run(() => TextTarget.Capture(Handle, expectedSuffix, settings, ownTest: true));
            Check((target != null) == expected, "long suffix capture checks the exact caret, word boundary and editable state");
            Check(field.Text == text && field.CaretIndex == caret && field.SelectionLength == 0,
                "long suffix verification preserves all text, selection and the interior caret");
            Console.WriteLine("Long capture duration: " + started.ElapsedMilliseconds + " ms");
        }
        field.IsReadOnly = false;
    }
    private async Task CheckNetworkRecovery()
    {
        const string prefix = "Сохранённый абзац.\n";
        foreach (var backend in new[] { "Ollama", "OpenAI" })
            foreach (var failure in new[] { "http", "json", "timeout", "disconnect" })
            {
                controller.Configure(settings with { Backend = backend, TimeoutSeconds = 1 });
                await Prepare(prefix);
                server.StatusCode = failure == "http" ? 503 : 200;
                server.RawReply = failure == "json" ? "{bad json" : null;
                server.Delay = failure == "timeout" ? 1500 : 0;
                server.TruncateBody = failure == "disconnect";
                lastStatus = "";
                TestInput.Text("првиет ");
                await WaitFor(() => lastStatus.StartsWith("Нет ответа модели") || lastStatus.StartsWith("Модель не ответила вовремя"), 5000);
                Check((lastStatus.StartsWith("Нет ответа модели") || lastStatus.StartsWith("Модель не ответила вовремя")) &&
                    TypingQueue.Normalize(field.Text) == prefix + "првиет " && field.CaretIndex == field.Text.Length,
                    $"{backend}/{failure}: failure preserves the exact field, prior paragraph and caret");
                await Task.Delay(800);
                var calls = server.Calls;
                await Task.Delay(700);
                Check(server.Calls == calls, $"{backend}/{failure}: a failed request does not start a retry loop while idle");
                server.StatusCode = 200; server.RawReply = null; server.Delay = 0; server.TruncateBody = false;
                TestInput.Text("hello ");
                await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "привет hello ", 5000);
                Check(TypingQueue.Normalize(field.Text) == prefix + "привет hello " && field.CaretIndex == field.Text.Length,
                    $"{backend}/{failure}: the next input recovers and corrects the retained queue");
                controller.Undo();
                await WaitFor(() => TypingQueue.Normalize(field.Text) == prefix + "првиет hello ", 3000);
                Check(TypingQueue.Normalize(field.Text) == prefix + "првиет hello " && field.CaretIndex == field.Text.Length,
                    $"{backend}/{failure}: undo after recovery preserves later typed text");
            }
    }
    private async Task CheckAmbiguousLayout()
    {
        foreach (var keyDelay in new[] { 0, 15, 80 })
        {
            await Prepare("старый текст");
            TestInput.Chord(0x11, 0x41); TestInput.Key(8);
            foreach (var c in "dhtvz phz gjnhfnbkb")
            {
                TestInput.PhysicalText(c.ToString(), "00000409");
                if (keyDelay > 0) await Task.Delay(keyDelay);
            }
            await WaitFor(() => field.Text == "время зря потратили" && field.CaretIndex == field.Text.Length, 7000);
            Check(field.Text == "время зря потратили" && field.CaretIndex == field.Text.Length,
                $"ambiguous middle word after clearing at {keyDelay} ms per key");
        }
        await Prepare();
        TestInput.PhysicalText("dhtvz ", "00000409");
        await WaitFor(() => field.Text == "время ", 5000);
        Check(field.Text == "время ", "first word is corrected before the ambiguous word arrives");
        TestInput.PhysicalText("phz ", "00000409");
        await WaitFor(() => field.Text == "время зря ", 6000);
        Check(field.Text == "время зря ", "ambiguous word uses already consumed left context");
        TestInput.PhysicalText("gjnhfnbkb", "00000409");
        await WaitFor(() => field.Text == "время зря потратили" && field.CaretIndex == field.Text.Length, 6000);
        Check(field.Text == "время зря потратили" && field.CaretIndex == field.Text.Length,
            "typing the last word preserves the corrected middle word");
        await Prepare("время зря потратили");
        field.Select(6, 3);
        TestInput.PhysicalText("phz", "00000409");
        await WaitFor(() => field.Text == "время зря потратили" && field.CaretIndex == 9, 6000);
        Check(field.Text == "время зря потратили" && field.CaretIndex == 9,
            "editing the middle word uses both neighbours and preserves the caret");
    }
    private async Task CheckParagraphInput()
    {
        await CheckSentenceInput();
        foreach (var delay in new[] { 0, 25, 80 })
        {
            await Prepare();
            foreach (var c in "Yfccnegbkj ntgkjt ktnj")
            {
                TestInput.PhysicalText(c.ToString(), "00000409");
                if (delay > 0) await Task.Delay(delay);
            }
            await WaitFor(() => field.Text == "Наступило теплое лето" && field.CaretIndex == field.Text.Length, 10000);
            Check(field.Text == "Наступило теплое лето" && field.CaretIndex == field.Text.Length,
                "the extra repeated letter corrects after layout conversion at " + delay + " ms per key");
        }
        const string paragraphInput = "Vfvf vskf hfve/ Hfvf ,skf xbcnfz/ Yfnegbkj ntgkjt ktnj/ D cfle cjphtkb ckflrbt z,kjyb b dbiyb/";
        const string paragraphExpected = "Мама мыла раму. Рама была чистая. Наступило теплое лето. В саду созрели сладкие яблони и вишни.";
        foreach (var delay in new[] { 0, 25, 80 })
        {
            await Prepare();
            foreach (var c in paragraphInput)
            {
                TestInput.PhysicalText(c.ToString(), "00000409");
                if (delay > 0) await Task.Delay(delay);
            }
            await WaitFor(() => field.Text == paragraphExpected && field.CaretIndex == paragraphExpected.Length, 30000);
            if (field.Text != paragraphExpected)
                foreach (var member in typeof(CorrectionController).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Where(member => member.Name is "tracker" or "typedSinceReset" or "recentContext" or "inspectEdit"))
                {
                    var value = member.GetValue(controller);
                    Console.WriteLine("PARAGRAPH STATE: " + member.Name + "=" + (value is TypingQueue queue ? queue.Text : value));
                }
            Check(field.Text == paragraphExpected && field.CaretIndex == paragraphExpected.Length,
                "the reported paragraph corrects its words, inflections, capitalization and slash keys at " + delay + " ms per key");
        }
        await Prepare();
        TestInput.Text("Yfnegbkj ");
        await WaitFor(() => field.Text == "Наступило " && field.CaretIndex == field.Text.Length, 7000);
        Check(field.Text == "Наступило " && field.CaretIndex == field.Text.Length,
            "the missing letter is corrected before its right context arrives");
        TestInput.Text("ntgkjt ");
        await WaitFor(() => field.Text == "Наступило теплое " && field.CaretIndex == field.Text.Length, 10000);
        Check(field.Text == "Наступило теплое " && field.CaretIndex == field.Text.Length,
            "the newly corrected word preserves the earlier spelling correction and caret");
        controller.Undo();
        await WaitFor(() => field.Text == "Наступило ntgkjt " && field.CaretIndex == field.Text.Length, 3000);
        Check(field.Text == "Наступило ntgkjt " && field.CaretIndex == field.Text.Length,
            "undo restores the newly typed word while retaining the earlier independent correction");
        await Task.Delay(700);
        Check(field.Text == "Наступило ntgkjt ", "the rolling correction is not reapplied after explicit undo");
    }
    private async Task CheckLayoutSwitch()
    {
        foreach (var chord in new[] { (Name: "Ctrl+Alt", First: (ushort)0x11, Second: (ushort)0x12),
            (Name: "Alt+Shift", First: (ushort)0x12, Second: (ushort)0x10),
            (Name: "Ctrl+Shift", First: (ushort)0x11, Second: (ushort)0x10),
            (Name: "Win+Space", First: (ushort)0x5B, Second: (ushort)0x20) })
        {
            await Prepare();
            TestInput.PhysicalText("ghbrjkmyj", "00000409");
            await WaitFor(() => field.Text == "ghbrjkmyj", 200);
            TestInput.Chord(chord.First, chord.Second);
            await Task.Delay(70);
            TestInput.PhysicalText("о ", "00000419");
            await WaitFor(() => field.Text == "прикольно ", 7000);
            Console.WriteLine("SWITCH " + chord.Name + ": " + field.Text + " | " + lastStatus);
            Check(field.Text == "прикольно ", "layout switch inside a word preserves the whole token and checks its spelling: " + chord.Name);
            controller.Undo();
            await WaitFor(() => field.Text == "ghbrjkmyjо ", 3000);
            Check(field.Text == "ghbrjkmyjо ", "undo restores the exact mixed word after " + chord.Name);
        }
        foreach (var prefix in new[] { "ghbrjkmyj", "ghbrjkmy" })
            foreach (var delay in new[] { 0, 25, 80 })
            {
                await Prepare();
                foreach (var c in prefix)
                {
                    TestInput.PhysicalText(c.ToString(), "00000409");
                    if (delay > 0) await Task.Delay(delay);
                }
                await WaitFor(() => field.Text == prefix, 200);
                TestInput.Chord(0x11, 0x12);
                foreach (var c in "о то что можно переключаться прям на лету ")
                {
                    TestInput.PhysicalText(c.ToString(), "00000419");
                    if (delay > 0) await Task.Delay(delay);
                }
                const string expected = "прикольно то что можно переключаться прям на лету ";
                await WaitFor(() => field.Text == expected, 10000);
                Check(field.Text == expected && field.CaretIndex == expected.Length,
                    "Ctrl+Alt mid-word keeps the first word and subsequent phrase at " + delay + " ms per key: " + prefix);
            }
    }
    private async Task CheckSentenceInput()
    {
        foreach (var separator in new[] { ".", ";", ",", ":", "!", "?", "...", "…", "—", "–", ")", "]", "}", "»", "\"" })
        {
            foreach (var whole in new[] { false, true })
            {
                var lead = "вишни" + separator;
                await Prepare(whole ? "" : lead);
                if (whole) TestInput.Text(lead);
                TestInput.PhysicalText("ghbdtn ", "00000409");
                await WaitFor(() => field.Text == lead + "привет ", 7000);
                Check(field.Text == lead + "привет ", "separator preserves lowercase physical input, whole=" + whole + ": " + separator);
            }
        }
        const string prefix = "В саду созрели сладкие яблони и вишни.";
        const string input = "Vfktymrbq rjntyjr ";
        const string expected = prefix + "Маленький котенок ";
        foreach (var delay in new[] { 0, 25, 80 })
        {
            await Prepare(prefix);
            foreach (var c in input)
            {
                TestInput.PhysicalText(c.ToString(), "00000409");
                if (delay > 0) await Task.Delay(delay);
            }
            await WaitFor(() => field.Text == expected && field.CaretIndex == expected.Length, 10000);
            Check(field.Text == expected && field.CaretIndex == expected.Length,
                "typing immediately after a sentence dot corrects without inserting a space at " + delay + " ms per key");
        }
        const string fullPrefix = "Мама мыла раму. Рама была чистая. Наступило теплое лето. " + prefix;
        const string tail = "Vfktymrbq rjntyjr dtctkj buhfk c ,tksv rke,rjv ybnjr yf ковер.";
        const string fullExpected = fullPrefix + "Маленький котенок весело играл с белым клубком ниток на ковер.";
        foreach (var delay in new[] { 0, 25, 80 })
        {
            await Prepare(fullPrefix);
            foreach (var c in tail)
            {
                if (c >= 'А') TestInput.Text(c.ToString());
                else TestInput.PhysicalText(c.ToString(), "00000409");
                if (delay > 0) await Task.Delay(delay);
            }
            await WaitFor(() => field.Text == fullExpected && field.CaretIndex == fullExpected.Length, 15000);
            Check(field.Text == fullExpected && field.CaretIndex == fullExpected.Length,
                "the entire reported continuation corrects after an existing dot without whitespace at " + delay + " ms per key");
        }
        await Prepare();
        TestInput.Text(fullPrefix + tail);
        await WaitFor(() => field.Text == fullExpected && field.CaretIndex == fullExpected.Length, 15000);
        Check(field.Text == fullExpected && field.CaretIndex == fullExpected.Length,
            "joined sentences also correct when the complete paragraph enters the queue together");
        await Prepare(prefix);
        TestInput.PhysicalText("Vfktymrbq ", "00000409");
        await WaitFor(() => field.Text == prefix + "Маленький ", 7000);
        Check(field.Text == prefix + "Маленький ", "a single word corrects directly after the existing sentence dot");
        controller.Undo();
        await WaitFor(() => field.Text == prefix + "Vfktymrbq ", 3000);
        Check(field.Text == prefix + "Vfktymrbq " && field.CaretIndex == field.Text.Length,
            "undo after an adjacent sentence restores the exact new input and preserves the existing prefix");
        await Task.Delay(700);
        Check(field.Text == prefix + "Vfktymrbq ", "undo after a sentence dot is not immediately reapplied");
        foreach (var protectedPrefix in new[] { "https://вишни.", "user@вишни.", "C:\\вишни." })
        {
            await Prepare(protectedPrefix + "Vfktymrbq");
            var capture = await Task.Run(() => TextTarget.Capture(Handle, "Vfktymrbq", settings, ownTest: true));
            Check(capture == null, "sentence boundary capture still rejects a token suffix inside " + protectedPrefix);
        }
    }
    private async Task CheckFastInput()
    {
        await CheckParagraphInput();
        foreach (var input in new[] { "ntrcn", "ntrcn " })
        {
            await Prepare();
            TestInput.Text(input);
            var expected = input.EndsWith(' ') ? "текст " : "текст";
            await WaitFor(() => field.Text == expected && field.CaretIndex == expected.Length, 7000);
            Check(field.Text == expected && field.CaretIndex == expected.Length, "standalone layout word corrects without a neighbour: " + input);
            controller.Undo();
            await WaitFor(() => field.Text == input, 3000);
            Check(field.Text == input, "standalone layout correction supports exact undo: " + input);
        }
        await Prepare("старый текст");
        TestInput.Chord(0x11, 0x41); TestInput.Key(8);
        TestInput.Text("ntrcn ");
        await Task.Delay(1200);
        TestInput.Text("ghj,ysq");
        await WaitFor(() => field.Text == "текст пробный" && field.CaretIndex == field.Text.Length, 7000);
        Check(field.Text == "текст пробный" && field.CaretIndex == field.Text.Length,
            "clearing then pausing after a complete word preserves it for corrected right context");
        await Prepare();
        TestInput.Text("[jчу");
        await WaitFor(() => field.Text == "хочу" && field.CaretIndex == 4, 6000);
        Check(field.Text == "хочу" && field.CaretIndex == 4, "a leading bracket key is corrected as part of the mixed word");
        controller.Undo();
        await WaitFor(() => field.Text == "[jчу", 3000);
        Check(field.Text == "[jчу", "undo restores the leading bracket and mixed letters");
        foreach (var item in new[] { ("[очу", "хочу"), ("'то", "это"), (";иву", "живу"), (",уду", "буду"),
            (".бка", "юбка"), ("`жик", "ёжик"), ("об]явление", "объявление") })
        {
            await Prepare();
            TestInput.Text(item.Item1);
            await WaitFor(() => field.Text == item.Item2 && field.CaretIndex == field.Text.Length, 6000);
            Check(field.Text == item.Item2 && field.CaretIndex == field.Text.Length,
                "Russian punctuation letter key corrects in real input: " + item.Item1);
        }
        await Prepare("начинаетcz");
        TestInput.Key(8); TestInput.Key(8);
        await Task.Delay(700);
        Check(field.Text == "начинает", "deleting the mixed ending preserves the deliberate stem");
        TestInput.PhysicalText("cz", "00000409");
        await WaitFor(() => field.Text == "начинается" && field.CaretIndex == field.Text.Length, 6000);
        Check(field.Text == "начинается" && field.CaretIndex == field.Text.Length,
            "retyping the deleted Latin ending corrects the whole mixed word");
        controller.Undo();
        await WaitFor(() => field.Text == "начинаетcz", 3000);
        Check(field.Text == "начинаетcz", "undo restores the retyped mixed ending");
        await Prepare("начинаетcz");
        TestInput.Key(8); TestInput.Key(8);
        TestInput.PhysicalText("cz", "00000409");
        await WaitFor(() => field.Text == "начинается" && field.CaretIndex == field.Text.Length, 6000);
        Check(field.Text == "начинается" && field.CaretIndex == field.Text.Length,
            "immediately retyping a deleted ending corrects without a pause between deletion and typing");
        await Prepare();
        TestInput.Text("начинаетcz");
        await WaitFor(() => field.Text == "начинается" && field.CaretIndex == field.Text.Length, 6000);
        Check(field.Text == "начинается" && field.CaretIndex == field.Text.Length,
            "the mixed ending also corrects on initial typing");
        TestInput.Key(8); TestInput.Key(8);
        await Task.Delay(700);
        Check(field.Text == "начинает", "deleting the ending of the just-corrected word preserves the stem");
        TestInput.PhysicalText("cz", "00000409");
        await WaitFor(() => field.Text == "начинается" && field.CaretIndex == field.Text.Length, 6000);
        Check(field.Text == "начинается" && field.CaretIndex == field.Text.Length,
            "a new mixed ending corrects after deleting the previous automatic correction");
        await Prepare("старый текст");
        TestInput.Chord(0x11, 0x41); TestInput.Key(8);
        TestInput.PhysicalText("ehjr", "00000409");
        await WaitFor(() => field.Text == "урок" && field.CaretIndex == 4, 6000);
        Check(field.Text == "урок" && field.CaretIndex == 4, "layout correction does not insert an artificial hyphen into the reported word");
        controller.Undo();
        await WaitFor(() => field.Text == "ehjr", 3000);
        Check(field.Text == "ehjr", "undo restores the original layout without a hyphen");
        await Prepare();
        lastStatus = "";
        TestInput.PhysicalText("урок ", "00000419");
        await WaitFor(() => lastStatus.StartsWith("Проверено"), 6000);
        Check(field.Text == "урок " && field.CaretIndex == 5 && lastStatus.StartsWith("Проверено"),
            "the correctly typed Russian word remains unchanged after hyphen review");
        await CheckAmbiguousLayout();
        await Prepare("старый текст");
        TestInput.Chord(0x11, 0x41); TestInput.Key(8);
        TestInput.PhysicalText("yt pyf. rfr cltkfnm", "00000409");
        await WaitFor(() => field.Text == "не знаю как сделать" && field.CaretIndex == field.Text.Length, 7000);
        Check(field.Text == "не знаю как сделать" && field.CaretIndex == field.Text.Length,
            "a dot typed as the final Russian letter remains in the word after clearing and fast typing");
        await Prepare();
        TestInput.PhysicalText("pyf.", "00000409");
        await WaitFor(() => field.Text == "знаю", 5000);
        Check(field.Text == "знаю", "a terminal dot key is recognized as a letter without following context");
        controller.Undo();
        await WaitFor(() => field.Text == "pyf.", 3000);
        Check(field.Text == "pyf.", "undo restores the exact source punctuation key");
        await Prepare();
        TestInput.PhysicalText("ghbdt, rfr ltkf", "00000409");
        await WaitFor(() => field.Text == "привет, как дела", 7000);
        Check(field.Text == "привет, как дела" && field.CaretIndex == field.Text.Length,
            "the reported first word with a missing key receives both layout and spelling correction");
        await Prepare();
        TestInput.PhysicalText("ghbdt,", "00000409");
        await WaitFor(() => field.Text == "привет,", 5000);
        Check(field.Text == "привет,", "a missing letter is restored before the following words arrive");
        TestInput.PhysicalText(" rfr ltkf", "00000409");
        await WaitFor(() => field.Text == "привет, как дела", 5000);
        Check(field.Text == "привет, как дела", "continuing after the repaired first word preserves the comma and spelling");
        await Prepare();
        TestInput.PhysicalText("ghbdtn,rfr ltkf", "00000409");
        await WaitFor(() => field.Text == "привет,как дела", 7000);
        Check(field.Text == "привет,как дела" && field.CaretIndex == field.Text.Length,
            "comma without a following space separates two corrected words in the reported phrase");
        controller.Configure(settings with { DelayMs = 250 });
        await Prepare();
        var firstRequest = new TaskCompletionSource();
        void ObserveFirstRequest(string status)
        {
            if (status.StartsWith("Проверяю очередь")) firstRequest.TrySetResult();
        }
        controller.Status += ObserveFirstRequest;
        TestInput.PhysicalText("ghbdtn,", "00000409");
        await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.Status -= ObserveFirstRequest;
        System.Windows.Input.KeyEventHandler delayFollowingSpace = (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Space) e.Handled = true;
        };
        field.PreviewKeyDown += delayFollowingSpace;
        try
        {
            TestInput.PhysicalText(" ", "00000409");
            await Task.Delay(450);
        }
        finally { field.PreviewKeyDown -= delayFollowingSpace; }
        field.SelectedText = " ";
        field.CaretIndex = field.Text.Length;
        TestInput.PhysicalText("rfr ltkf", "00000409");
        await WaitFor(() => field.Text == "привет, как дела", 7000);
        Check(field.Text == "привет, как дела", "typing resumes while the first word is being verified without losing that word");
        controller.Configure(settings with { DelayMs = 250 });
        foreach (var keyDelay in new[] { 0, 1, 5, 15, 30 })
        {
            await Prepare();
            foreach (var c in "ghbdtn, rfr")
            {
                TestInput.PhysicalText(c.ToString(), "00000409");
                if (keyDelay > 0) await Task.Delay(keyDelay);
            }
            await WaitFor(() => field.Text == "привет, как", 5000);
            Check(field.Text == "привет, как", $"fast physical input at {keyDelay} ms between keys with the application's 250 ms delay");
        }
        await Prepare();
        var delayedInput = new StringBuilder();
        System.Windows.Input.TextCompositionEventHandler holdInput = (_, e) =>
        {
            delayedInput.Append(e.Text);
            e.Handled = true;
        };
        System.Windows.Input.KeyEventHandler holdSpace = (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Space) return;
            delayedInput.Append(' ');
            e.Handled = true;
        };
        field.PreviewTextInput += holdInput;
        field.PreviewKeyDown += holdSpace;
        try
        {
            TestInput.PhysicalText("ghbdtn, rfr", "00000409");
            await Task.Delay(450);
        }
        finally { field.PreviewTextInput -= holdInput; field.PreviewKeyDown -= holdSpace; }
        field.SelectedText = delayedInput.ToString();
        field.CaretIndex = field.Text.Length;
        Check(field.Text == "ghbdtn, rfr", "the delayed editor delivers the exact original input");
        await WaitFor(() => field.Text == "привет, как", 5000);
        Check(field.Text == "привет, как", "a delayed text provider does not discard a fast input burst");
        controller.Configure(settings);
    }
    private async Task CheckStagedContext()
    {
        await CheckFastInput();
        await Prepare();
        TestInput.PhysicalText("ghbdtn, rfr", "00000409");
        await WaitFor(() => field.Text == "привет, как", 7000);
        Check(field.Text == "привет, как", "physical English keys correct the exact comma example");
        await Prepare();
        foreach (var c in "ghbdtn,")
        {
            TestInput.PhysicalText(c.ToString(), "00000409");
            await Task.Delay(120);
        }
        await WaitFor(() => field.Text == "привет,", 5000);
        Check(field.Text == "привет,", "physical typing with inter-key delays preserves and corrects a comma");
        await Task.Delay(700);
        TestInput.PhysicalText(" rfr", "00000409");
        await WaitFor(() => field.Text == "привет, как", 5000);
        Check(field.Text == "привет, как", "typing after a comma and pause still corrects the next word");
        await Prepare();
        TestInput.Text("ghbdtn,");
        await WaitFor(() => field.Text == "привет,", 5000);
        Check(field.Text == "привет," && field.CaretIndex == field.Text.Length,
            "a trailing comma survives automatic layout correction");
        controller.Undo();
        await WaitFor(() => field.Text == "ghbdtn,", 3000);
        Check(field.Text == "ghbdtn,", "undo restores the original word and comma");
        await Prepare();
        TestInput.Text("ghbdtn, f ult vs ,eltv yjdxtdfnm ?");
        await WaitFor(() => field.Text == "привет, а где мы будем новчевать ?", 10000);
        Check(field.Text == "привет, а где мы будем новчевать ?" && field.CaretIndex == field.Text.Length,
            "the reported phrase retains its comma and subsequent text");
        await Prepare();
        TestInput.Text("ghb");
        await WaitFor(() => field.Text == "при", 5000);
        Check(field.Text == "при", "the first part is converted before the user continues typing");
        await Task.Delay(700);
        TestInput.Text(",skb");
        await WaitFor(() => field.Text == "прибыли", 5000);
        Check(field.Text == "прибыли" && field.CaretIndex == field.Text.Length,
            "continuing a corrected prefix in the original layout rechecks the complete word");
        await Prepare();
        TestInput.Text("а может поедем в архив и сделаем там запись о том что мы при");
        await Task.Delay(2500);
        TestInput.Text(",skb как у вас дела");
        await WaitFor(() => field.Text == "а может поедем в архив и сделаем там запись о том что мы прибыли как у вас дела", 8000);
        Check(field.Text == "а может поедем в архив и сделаем там запись о том что мы прибыли как у вас дела",
            "typing a wrong-layout suffix after a pause rechecks the entire existing word");
        await Prepare();
        TestInput.Text("мы при,skb как у вас дела");
        await WaitFor(() => field.Text == "мы прибыли как у вас дела", 7000);
        Check(field.Text == "мы прибыли как у вас дела" && field.CaretIndex == field.Text.Length,
            "mixed-layout word containing a punctuation key is corrected as a whole");
        await Prepare();
        TestInput.Text("f vj;tn ,snm gjtltv d fh[bd b cltkftv nfv pfgbcm");
        await WaitFor(() => field.Text == "а может быть поедем в архив и сделаем там запись", 10000);
        Check(field.Text == "а может быть поедем в архив и сделаем там запись",
            "the entire reported sentence corrects its single-letter conjunction");
        await Prepare();
        TestInput.Text("f vj;tn ,snm gjtltv d fh[bd ");
        await WaitFor(() => field.Text == "а может быть поедем в архив ", 7000);
        TestInput.Text("b ");
        await Task.Delay(1200);
        TestInput.Text("cltkftv nfv pfgbcm");
        await WaitFor(() => field.Text == "а может быть поедем в архив и сделаем там запись", 7000);
        Check(field.Text == "а может быть поедем в архив и сделаем там запись",
            "a one-letter conjunction is reconsidered when the following words arrive later");
        await Prepare();
        TestInput.Text("rfrjq ybe,lm dfcz");
        await WaitFor(() => field.Text == "какой-нибудь вася" && field.CaretIndex == field.Text.Length, 7000);
        Check(field.Text == "какой-нибудь вася" && field.CaretIndex == field.Text.Length,
            "the exact reported fast input corrects layout, transposition and hyphen together");
        await Prepare();
        TestInput.Text("какой yb,elm вася");
        await WaitFor(() => field.Text == "какой-нибудь вася" && field.CaretIndex == field.Text.Length, 6000);
        Check(field.Text == "какой-нибудь вася" && field.CaretIndex == field.Text.Length,
            "fast input with punctuation in the wrong-layout word preserves the following word");
        await Prepare();
        TestInput.Text("к какому ");
        await Task.Delay(1500);
        TestInput.Text("нибудь");
        await WaitFor(() => field.Text == "к какому-нибудь", 5000);
        Check(field.Text == "к какому-нибудь", "word-boundary hyphen takes precedence over splitting a particle internally");
        await Prepare();
        TestInput.Text("как-то мы хотели поехать к какому нибудь красивому место ");
        await WaitFor(() => field.Text == "как-то мы хотели поехать к какому-нибудь красивому место ", 7000);
        Check(field.Text == "как-то мы хотели поехать к какому-нибудь красивому место ",
            "the full reported sentence gains the correct hyphen without splitting the particle");
        await Prepare();
        TestInput.Text("rfrnj");
        await WaitFor(() => field.Text == "как-то", 5000);
        Check(field.Text == "как-то" && field.CaretIndex == 6, "wrong-layout joined word gains its missing internal hyphen");
        controller.Undo();
        await WaitFor(() => field.Text == "rfrnj");
        Check(field.Text == "rfrnj", "undo restores the exact original keyboard-layout input after a hyphen insertion");
        await Prepare();
        TestInput.Text("както ");
        await WaitFor(() => field.Text == "как-то ", 5000);
        Check(field.Text == "как-то ", "missing internal hyphen corrects in Russian input without a layout error");
        await Prepare("тут как-то дальше");
        field.Select(4, 6);
        lastStatus = "";
        TestInput.Text("както");
        await WaitFor(() => field.Text == "тут как-то дальше" && lastStatus == "Отредактированное слово исправлено", 5000);
        Check(field.Text == "тут как-то дальше" && field.CaretIndex == 10 && lastStatus == "Отредактированное слово исправлено",
            "manual spelling edit inserts the missing hyphen while preserving the following text and caret");
        await Prepare();
        TestInput.Text("праздник какой ");
        await Task.Delay(1500);
        TestInput.Text("nj");
        await WaitFor(() => field.Text == "праздник какой-то", 5000);
        Check(field.Text == "праздник какой-то" && field.CaretIndex == field.Text.Length,
            "short final layout error uses consumed context and joins the particle with a hyphen");
        controller.Undo();
        await WaitFor(() => field.Text == "праздник какой nj");
        Check(field.Text == "праздник какой nj", "undo restores both the original space and wrong-layout particle");
        await Prepare();
        TestInput.Text("праздник какой nj ");
        await WaitFor(() => field.Text == "праздник какой-то ", 5000);
        Check(field.Text == "праздник какой-то ", "one typing burst corrects layout and hyphen while preserving trailing space");
        await Prepare();
        TestInput.Text("кто то ");
        await WaitFor(() => field.Text == "кто-то ", 5000);
        Check(field.Text == "кто-то ", "hyphenation also works without a keyboard-layout error");
        await Prepare();
        lastStatus = "";
        TestInput.Text("то есть ");
        await WaitFor(() => lastStatus.StartsWith("Проверено"), 5000);
        Check(field.Text == "то есть " && lastStatus.StartsWith("Проверено"), "a correctly spaced phrase is not joined with a hyphen");
        await Prepare();
        TestInput.Text("pfv ");
        await Task.Delay(1500);
        TestInput.Text("начальника");
        await WaitFor(() => field.Text == "зам начальника", 5000);
        Check(field.Text == "зам начальника" && field.CaretIndex == field.Text.Length,
            "a previously checked first word is reconsidered when its Russian neighbour arrives");
        await Prepare("зам начальника");
        field.Select(0, 3);
        lastStatus = "";
        TestInput.Text("pfv");
        await WaitFor(() => field.Text == "зам начальника" && lastStatus == "Отредактированное слово исправлено", 5000);
        Check(field.Text == "зам начальника" && field.CaretIndex == 3 && lastStatus == "Отредактированное слово исправлено",
            "manual editing uses the following word as context without replacing it");
        await Prepare();
        TestInput.Text("hello ");
        await Task.Delay(1000);
        TestInput.Text("коллега");
        await Task.Delay(1500);
        Check(field.Text == "hello коллега", "valid English word is preserved next to Russian text");
        await Prepare();
        lastStatus = "";
        TestInput.Text("hello");
        await WaitFor(() => lastStatus.Contains("ожидаю соседнее слово"));
        var completedChecks = results.Count;
        await Task.Delay(500);
        Check(results.Count == completedChecks, "an unchanged isolated word is not repeatedly checked while idle");
        TestInput.Key(0x0D); TestInput.Text("ghbdtn");
        await WaitFor(() => TypingQueue.Normalize(field.Text) == "hello\nпривет", 5000);
        Check(TypingQueue.Normalize(field.Text) == "hello\nпривет", "Enter releases the retained word and the next line is checked");
        await Prepare("проверка замена");
        TestInput.Key(8); TestInput.Key(8); TestInput.Key(8);
        await Task.Delay(1800);
        Check(field.Text == "проверка зам" && field.CaretIndex == field.Text.Length,
            "deleting the ending of an existing word preserves the remaining letters");
        TestInput.Text("tyf");
        await WaitFor(() => field.Text == "проверка замена", 30000);
        Check(field.Text == "проверка замена", "typing after deletion resumes mixed-layout correction");
        await Prepare("проверка замена дальше");
        field.CaretIndex = "проверка зам".Length;
        TestInput.Key(0x2E); TestInput.Key(0x2E); TestInput.Key(0x2E);
        await Task.Delay(1800);
        Check(field.Text == "проверка зам дальше" && field.CaretIndex == "проверка зам".Length,
            "Delete preserves the truncated word, following text and caret");
        await Prepare();
        TestInput.Text("f gjxtve ");
        await WaitFor(() => field.Text == "а почему ", 30000);
        Check(field.Text == "а почему ", "first part of the phrase is corrected before the last word is typed");
        var stagedWatch = Stopwatch.StartNew();
        foreach (var c in "chfpe") { TestInput.Text(c.ToString()); await Task.Delay(80); }
        await WaitFor(() => field.Text == "а почему сразу", 30000);
        Check(field.Text == "а почему сразу" && field.CaretIndex == field.Text.Length,
            $"last word uses already corrected prefix instead of becoming chfp ({stagedWatch.ElapsedMilliseconds} ms)");
        await Prepare();
        lastStatus = "";
        TestInput.Text("ок скорость ");
        await WaitFor(() => lastStatus.StartsWith("Проверено") && field.Text == "ок скорость ", 30000);
        Check(field.Text == "ок скорость " && lastStatus.StartsWith("Проверено"), "Russian prefix is processed before the next layout error");
        TestInput.Text("pfvtys");
        await WaitFor(() => field.Text == "ок скорость замены", 30000);
        Check(field.Text == "ок скорость замены" && field.CaretIndex == field.Text.Length,
            "raw keyboard conversion retains valid inflection in a staged phrase");
        TestInput.Text(" неплохая");
        await WaitFor(() => field.Text == "ок скорость замены неплохая");
        Check(field.Text == "ок скорость замены неплохая", "following Russian word is preserved after correcting the layout error");
        await Prepare();
        TestInput.Text("нf");
        await WaitFor(() => field.Text == "на", 30000);
        Check(field.Text == "на", "two-letter mixed word corrects after idle without a separator");
        const string manualPhrase = "можно печатать что хочешь на каком";
        await Prepare(manualPhrase);
        TestInput.Key(0x24);
        await Task.Delay(50);
        field.Select("можно печатать что хочешь н".Length, 1);
        lastStatus = "";
        TestInput.Text("f");
        await WaitFor(() => lastStatus == "Отредактированное слово исправлено" && field.Text == manualPhrase, 30000);
        Check(field.Text == manualPhrase && lastStatus == "Отредактированное слово исправлено" && field.CaretIndex == "можно печатать что хочешь на".Length,
            "manual edit of a two-letter mixed word preserves the following word and caret");
        controller.Undo();
        await WaitFor(() => field.Text == "можно печатать что хочешь нf каком");
        Check(field.Text == "можно печатать что хочешь нf каком", "undo restores the original two-letter mixed word only");
    }

    private async Task Run()
    {
        settings = new Settings { Backend = "Ollama", Endpoint = server.Endpoint, AllowedApps = "", DelayMs = 50, FixKeyboardLayout = false, FixHyphens = false };
        if (realEndpoint != null) settings = settings with { Backend = "OpenAI", Endpoint = realEndpoint, TimeoutSeconds = 30, FixKeyboardLayout = true, FixHyphens = true };
        controller = new CorrectionController(this, settings, acceptSyntheticTestInput: true);
        controller.Status += s => { lastStatus = s; results.Add("STATUS: " + s); Console.WriteLine(s); };
        controller.Fault += e => Console.WriteLine(e);
        try
        {
            await Prepare("првиет ");
            if (Native.GetForegroundWindow() != Handle) Console.WriteLine("TEST WINDOW DID NOT RECEIVE FOREGROUND FOCUS");
            var target = await Task.Run(() => TextTarget.Capture(Handle, "првиет ", settings, ownTest: true));
            Check(target != null, "UI Automation finds a verified editable caret range");
            if (onlyNetwork) { await CheckNetworkRecovery(); return; }
            if (ExtraScenario == "unicode") { await CheckUnicodeInput(); return; }
            if (ExtraScenario == "long-capture") { await CheckLongCapture(); return; }

            if (realEndpoint != null)
            {
                if (ExtraScenario == "cpu-input") { await CheckReportedInput(cpuSmoke: true); return; }
                if (ExtraScenario == "reported-case") { await CheckReportedInput(); return; }
                if (ExtraScenario == "staged-case") { await CheckStagedCase(); return; }
                if (ExtraScenario == "long-tail") { await CheckLongFollowingInput(); return; }
                if (onlyInput) { await CheckFastInput(); return; }
                if (onlyParagraph) { await CheckParagraphInput(); return; }
                if (onlySentence) { await CheckSentenceInput(); return; }
                if (onlySwitch) { await CheckLayoutSwitch(); return; }
                if (onlyQuality) { await CheckReportedInput(); await CheckLongFollowingInput(); return; }
                if (onlyContext) { await CheckStagedContext(); return; }
                await Prepare();
                var latencyWatch = Stopwatch.StartNew();
                TestInput.Text("yj bcghfdkztn");
                await WaitFor(() => field.Text == "но исправляет", 30000);
                Check(field.Text == "но исправляет" && field.CaretIndex == field.Text.Length,
                    $"reported slow phrase corrects without delimiter ({latencyWatch.ElapsedMilliseconds} ms from input)");
                const string originalPhrase = "привет привет f vj;tn ,snm пойдем d fpvjr ";
                await Prepare();
                TestInput.Text(originalPhrase);
                await WaitFor(() => field.Text == "привет привет а может быть пойдем в замок ", 30000);
                Check(field.Text == "привет привет а может быть пойдем в замок ", "rapid mixed-layout phrase -> real local model -> verified native replacement");
                controller.Undo();
                await WaitFor(() => field.Text == originalPhrase);
                Check(field.Text == originalPhrase, "phrase undo restores every original character");
                await Prepare();
                TestInput.Text("а может в замок пойдеv ");
                await WaitFor(() => field.Text == "а может в замок пойдем ", 30000);
                Check(field.Text == "а может в замок пойдем ", "mixed alphabet word -> real model -> native replacement");
                controller.Undo();
                await WaitFor(() => field.Text == "а может в замок пойдеv ");
                Check(field.Text == "а может в замок пойдеv ", "mixed alphabet undo restores Latin character");
                await Prepare();
                TestInput.Text("ghbdtn ");
                await WaitFor(() => lastStatus.StartsWith("Проверяю очередь"));
                var caughtUp = false;
                const string realTail = "abcdefghijklmnopqrstuvwxyzabcdefghijklmn";
                foreach (var c in realTail)
                {
                    TestInput.Text(c.ToString());
                    await Task.Delay(80);
                    caughtUp |= field.Text.StartsWith("привет ", StringComparison.Ordinal);
                }
                Check(caughtUp && field.Text == "привет " + realTail && field.CaretIndex == field.Text.Length,
                    "real model catches up during continuous typing without changing tail or caret");
                await Prepare();
                TestInput.Text("cgfcb,j"); TestInput.Key(13); TestInput.Text("next");
                await WaitFor(() => TypingQueue.Normalize(field.Text) == "спасибо\nnext", 30000);
                Check(TypingQueue.Normalize(field.Text) == "спасибо\nnext", "real model corrects layout after Enter on preceding line");
                await Prepare("а может пойдет в супермаркет");
                TestInput.Key(0x24);
                await Task.Delay(50);
                field.Select("а может пой".Length, 3);
                TestInput.Text("ltv");
                await WaitFor(() => field.Text == "а может пойдем в супермаркет", 30000);
                Check(field.Text == "а может пойдем в супермаркет", "real model rechecks an earlier edited word after the sentence was already typed");
                Check(field.CaretIndex == "а может пойдем".Length, "edited-word correction preserves caret and following supermarket text");
                controller.Undo();
                await WaitFor(() => field.Text == "а может пойltv в супермаркет");
                Check(field.Text == "а может пойltv в супермаркет", "undo restores edited mixed word without touching later text");
                await Prepare();
                TestInput.Text("ghjlf.n gj 'njq wtyt ");
                await WaitFor(() => field.Text == "продают по этой цене ", 30000);
                Check(field.Text == "продают по этой цене ", "wrong-layout last word is not hidden by a spurious English spelling edit");
                await Prepare("по этой цене продадут интересно\n");
                TestInput.Text("f tckb yt продада");
                await Task.Delay(150);
                TestInput.Key(8); TestInput.Text("ут");
                await WaitFor(() => TypingQueue.Normalize(field.Text) == "по этой цене продадут интересно\nа если не продадут" && field.CaretIndex == field.Text.Length, 30000);
                Check(TypingQueue.Normalize(field.Text) == "по этой цене продадут интересно\nа если не продадут" && field.CaretIndex == field.Text.Length,
                    "real model keeps previous wrong-layout words after Backspace in the unfinished last word");
                await Prepare();
                TestInput.Text("f ");
                await Task.Delay(850);
                TestInput.Text("что если yt ghjlflen");
                await WaitFor(() => field.Text == "а что если не продадут", 30000);
                Check(field.Text == "а что если не продадут" && field.CaretIndex == field.Text.Length,
                    "single wrong-layout letter uses Russian neighbours and final word corrects without delimiter");
                await Prepare();
                TestInput.Text("I like music");
                await Task.Delay(10000);
                Check(field.Text == "I like music", "contextual single-letter check preserves a valid English pronoun");
                await CheckStagedContext();
                return;
            }

            await Prepare();
            TestInput.Text("првиет ");
            await WaitFor(() => field.Text == "привет ");
            Check(field.Text == "привет ", "Russian typing -> local HTTP model -> native replacement");
            controller.Undo();
            await WaitFor(() => field.Text == "првиет ");
            Check(field.Text == "првиет ", "undo restores exact original text");

            await Prepare();
            TestInput.Text("helllo ");
            await WaitFor(() => field.Text == "hello ");
            Check(field.Text == "hello ", "English typing correction");

            await Prepare();
            TestInput.PhysicalText("првиет ", "00000419");
            await WaitFor(() => field.Text == "привет ");
            Check(field.Text == "привет ", "Russian physical-key layout decoding");
            await Prepare();
            TestInput.PhysicalText("helllo ", "00000409");
            await WaitFor(() => field.Text == "hello ");
            Check(field.Text == "hello ", "English physical-key layout decoding");

            await Prepare("привет ");
            TestInput.Text("helllo ");
            await WaitFor(() => field.Text == "привет hello ");
            Check(field.Text == "привет hello ", "mixed Russian-English preserves previous text");

            await Prepare("go hello to supermarket");
            TestInput.Key(0x24);
            for (var i = 0; i < "go hello".Length; i++) TestInput.Key(0x27);
            TestInput.Key(8); TestInput.Text("lo");
            await WaitFor(() => field.Text == "go hello to supermarket");
            // The original field is temporarily changed to helllo before inference.
            await WaitFor(() => lastStatus == "Отредактированное слово исправлено" && field.Text == "go hello to supermarket" && field.CaretIndex == "go hello".Length);
            Check(field.Text == "go hello to supermarket" && field.CaretIndex == "go hello".Length,
                "earlier edited word rechecked without a new separator and later text preserved");
            controller.Undo();
            await WaitFor(() => field.Text == "go helllo to supermarket");
            Check(field.Text == "go helllo to supermarket", "edited-word undo restores only that word");

            await Prepare("go hello to supermarket");
            TestInput.Key(0x24);
            for (var i = 0; i < "go hel".Length; i++) TestInput.Key(0x27);
            TestInput.Text("l");
            await WaitFor(() => lastStatus == "Отредактированное слово исправлено" && field.Text == "go hello to supermarket" && field.CaretIndex == "go hell".Length);
            Check(field.Text == "go hello to supermarket" && field.CaretIndex == "go hell".Length,
                "editing inside a word reads both sides and restores interior caret");

            controller.Configure(settings);
            server.Delay = 500;
            await Prepare("go hello to supermarket");
            TestInput.Key(0x24);
            await Task.Delay(50);
            field.CaretIndex = "go hel".Length;
            var editCalls = server.Calls;
            TestInput.Text("l");
            await WaitFor(() => server.Calls > editCalls);
            TestInput.Key(0x27);
            await Task.Delay(650);
            Check(field.Text == "go helllo to supermarket", "moving caret cancels an in-flight edited-word correction");
            server.Delay = 0;

            await Prepare();
            var calls = server.Calls;
            TestInput.Text("NL100 ");
            await Task.Delay(250);
            Check(field.Text == "NL100 " && server.Calls == calls, "numeric poker token never sent");

            await Prepare("prefix");
            TestInput.Text("helllo ");
            await Task.Delay(250);
            await Task.Delay(350);
            Check(field.Text == "prefixhelllo ", "editing checks whole existing token rather than replacing its suffix");

            controller.Configure(settings); // Clear model cache for cancellation cases.
            server.Delay = 500;
            await Prepare();
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text("x");
            await Task.Delay(650);
            Check(field.Text == "привет x", "late inference corrects earlier word while preserving new tail");
            Check(lastStatus.StartsWith("Исправлено"), "background correction reports completed status");

            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            var correctedDuringTyping = false;
            foreach (var c in "abcdefghijklmno")
            {
                TestInput.Text(c.ToString());
                await Task.Delay(80);
                correctedDuringTyping |= field.Text.StartsWith("привет ", StringComparison.Ordinal);
            }
            Check(correctedDuringTyping, "model finishes and applies while user keeps typing");
            Check(field.Text == "привет abcdefghijklmno" && field.CaretIndex == field.Text.Length, "continuous typing preserves tail and restores caret");
            Check(server.Calls == calls + 1, "continued typing does not restart model request");
            controller.Undo();
            // Typing after the correction invalidates undo, as before.
            Check(field.Text == "привет abcdefghijklmno", "undo does not act on an outdated earlier correction");

            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text("helllo ");
            await WaitFor(() => field.Text == "привет hello ");
            Check(field.Text == "привет hello ", "queued second word corrected after first inference completes");

            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text("helllox"); TestInput.Key(8);
            await WaitFor(() => field.Text == "привет helllo");
            await Task.Delay(1200);
            Check(field.Text == "привет helllo", "deletion preserves unfinished tail while earlier queued word is corrected");
            TestInput.Text(" ");
            await WaitFor(() => field.Text == "привет hello ");
            Check(field.Text == "привет hello ", "separator after deletion resumes checking the shortened tail");

            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text("продада"); TestInput.Key(8); TestInput.Text("ут");
            await WaitFor(() => field.Text == "привет продадут" && field.CaretIndex == field.Text.Length);
            Check(field.Text == "привет продадут" && field.CaretIndex == field.Text.Length,
                "Backspace in unfinished tail preserves pending previous word without another separator");

            controller.Configure(settings);
            await Prepare();
            TestInput.Text("првиет продада"); TestInput.Key(8); TestInput.Text("ут");
            await WaitFor(() => field.Text == "привет продадут");
            Check(field.Text == "привет продадут", "Backspace in initial rapid burst preserves the queue before edit classification");

            controller.Configure(settings);
            await Prepare();
            TestInput.Text("првиет x");
            await Task.Delay(100);
            TestInput.Key(8); TestInput.Key(8); TestInput.Key(8); TestInput.Text("т ");
            await WaitFor(() => field.Text == "привет ");
            Check(field.Text == "привет ", "Backspace across queued word boundary invalidates stale result and rechecks changed text");

            server.Delay = 0;
            controller.Configure(settings);
            await Prepare();
            TestInput.Text("helllo");
            await WaitFor(() => field.Text == "hello");
            Check(field.Text == "hello" && field.CaretIndex == 5, "idle checks final word without inserting a delimiter");
            controller.Undo();
            await WaitFor(() => field.Text == "helllo");
            Check(field.Text == "helllo", "idle correction can be undone exactly");

            controller.Configure(settings);
            server.Delay = 700;
            await Prepare();
            calls = server.Calls;
            TestInput.Text("helllo");
            await WaitFor(() => server.Calls > calls);
            TestInput.Text("world");
            await Task.Delay(1700);
            Check(field.Text == "hellloworld", "continuing a word rejects the obsolete idle correction");

            server.Delay = 0;
            controller.Configure(settings);
            await Prepare();
            TestInput.Text("hello");
            await Task.Delay(1000);
            TestInput.Text("o");
            await Task.Delay(500);
            Check(field.Text == "helloo", "typing after an idle check retains the entire word");

            controller.Configure(settings);
            await Prepare();
            TestInput.Text("helllo");
            await WaitFor(() => field.Text == "hello");
            TestInput.Text(" helllo");
            await WaitFor(() => field.Text == "hello hello");
            Check(field.Text == "hello hello", "new word after an idle correction starts a valid queue at its separator");

            controller.Configure(settings);
            await Prepare();
            TestInput.Text("hello ");
            await WaitFor(() => lastStatus.StartsWith("Проверено") && field.Text == "hello ");
            await Task.Delay(100);
            server.Delay = 500;
            calls = server.Calls;
            TestInput.Text("helllo ");
            await WaitFor(() => server.Calls > calls);
            field.Text = "world helllo "; field.CaretIndex = field.Text.Length;
            await WaitFor(() => lastStatus.StartsWith("Замена пропущена"), 2200);
            Check(field.Text == "world helllo ", "changing remembered prefix invalidates a context-dependent pending correction");
            server.Delay = 0;

            controller.Configure(settings);
            await Prepare();
            TestInput.Text("helllo");
            TestInput.Key(13);
            TestInput.Text("next");
            await WaitFor(() => TypingQueue.Normalize(field.Text) == "hello\nnext" && field.CaretIndex == field.Text.Length);
            Check(TypingQueue.Normalize(field.Text) == "hello\nnext", "Enter completes word and correction preserves following line");
            Check(field.CaretIndex == field.Text.Length, "caret restored after correction across newline");
            controller.Undo();
            await WaitFor(() => TypingQueue.Normalize(field.Text) == "helllo\nnext" && field.CaretIndex == field.Text.Length);
            Check(TypingQueue.Normalize(field.Text) == "helllo\nnext", "undo preserves newline without reinjecting Enter");

            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            System.Windows.Input.KeyEventHandler sendMessage = (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { field.Clear(); e.Handled = true; }
            };
            field.PreviewKeyDown += sendMessage;
            TestInput.Text("helllo"); TestInput.Key(13);
            await Task.Delay(300);
            field.PreviewKeyDown -= sendMessage;
            Check(field.Text == "" && server.Calls == calls, "Enter that sends and clears a message is never replayed or overwritten");

            server.Delay = 500;
            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            field.CaretIndex = 0;
            await WaitFor(() => lastStatus.StartsWith("Замена пропущена"), 2200);
            Check(field.Text == "првиет ", "caret movement without a key event blocks replacement");
            Check(lastStatus.StartsWith("Замена пропущена"), "rejected stale replacement reports completed status");

            controller.Configure(settings);
            await Prepare();
            calls = server.Calls;
            TestInput.Text("првиет ");
            await WaitFor(() => server.Calls > calls);
            second.Text = "another field"; second.Focus();
            System.Windows.Input.Keyboard.Focus(second);
            await Task.Delay(650);
            Check(field.Text == "првиет " && second.Text == "another field", "focus change in same window blocks replacement");

            await Prepare();
            password.Focus(); System.Windows.Input.Keyboard.Focus(password);
            await Task.Delay(100);
            calls = server.Calls;
            TestInput.Text("првиет ");
            await Task.Delay(350);
            Check(password.Password == "првиет " && server.Calls == calls, "password is never sent or replaced");

            await Prepare("првиет ");
            field.SelectAll();
            target = await Task.Run(() => TextTarget.Capture(Handle, "првиет ", settings, ownTest: true));
            Check(target == null, "selection blocks replacement");
            field.Select(7, 0); field.IsReadOnly = true;
            target = await Task.Run(() => TextTarget.Capture(Handle, "првиет ", settings, ownTest: true));
            Check(target == null, "read-only field blocks replacement");
            field.IsReadOnly = false;

            controller.Configure(settings with { Enabled = false });
            await Prepare(); calls = server.Calls;
            TestInput.Text("првиет ");
            await Task.Delay(300);
            Check(field.Text == "првиет " && server.Calls == calls, "pause stops model requests and replacements");

            server.Delay = 0;
            controller.Configure(settings with { AllowedApps = "Kazrich.IntegrationTests" });
            using (var external = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--native-fixture")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!)
            {
                try
                {
                    await WaitFor(() => { external.Refresh(); return external.MainWindowHandle != 0; });
                    Native.ShowWindow(external.MainWindowHandle, 5);
                    Native.SetForegroundWindow(external.MainWindowHandle);
                    await Task.Delay(200);
                    Check(Native.GetForegroundWindow() == external.MainWindowHandle, "external native fixture has focus");
                    TestInput.Text("helllo ");
                    string text = "";
                    for (var i = 0; i < 30; i++)
                    {
                        await Task.Delay(100);
                        text = await Task.Run(() =>
                        {
                            var focused = AutomationElement.FocusedElement;
                            return focused.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
                                ? ((TextPattern)raw).DocumentRange.GetText(-1) : "NO TEXT PATTERN";
                        });
                        if (text.TrimEnd() == "hello") break;
                    }
                    Check(text.TrimEnd() == "hello", "cross-process native RichEdit correction: " + text.TrimEnd());
                    TestInput.Text("helllo"); TestInput.Key(13); TestInput.Text("tail");
                    for (var i = 0; i < 30; i++)
                    {
                        await Task.Delay(100);
                        text = await Task.Run(() => ((TextPattern)AutomationElement.FocusedElement.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));
                        if (TypingQueue.Normalize(text).TrimEnd() == "hello hello\ntail") break;
                    }
                    Check(TypingQueue.Normalize(text).TrimEnd() == "hello hello\ntail", "native RichEdit catch-up preserves CRLF line and tail");
                    TestInput.Key(0x24); TestInput.Key(0x26);
                    for (var i = 0; i < 5; i++) TestInput.Key(0x27);
                    TestInput.Key(8); TestInput.Text("lo");
                    await WaitFor(() => lastStatus == "Отредактированное слово исправлено");
                    text = await Task.Run(() => ((TextPattern)AutomationElement.FocusedElement.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));
                    Check(TypingQueue.Normalize(text).TrimEnd() == "hello hello\ntail", "native RichEdit rechecks an earlier edited word");
                }
                finally { external.Kill(); await external.WaitForExitAsync(); }
            }
            results.Add("ALL UI CHECKS PASSED");
        }
        catch (Exception e) { results.Add("ERROR: " + e); Environment.ExitCode = 1; }
        finally
        {
            Directory.CreateDirectory("artifacts");
            File.WriteAllLines("artifacts/ui-tests.txt", results);
            controller.Dispose(); server.Dispose(); Close();
        }
    }
}

internal static class TestInput
{
    internal static void Chord(ushort modifier, ushort key)
    {
        var inputs = new[] { (modifier, 0u), (key, 0u), (key, 2u), (modifier, 2u) }
            .Select(item => new Native.Input { Type = 1, Data = new Native.InputUnion
                { Keyboard = new Native.KeyInput { Vk = item.Item1, Flags = item.Item2 } } }).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()) != inputs.Length)
            throw new Exception("Test key chord was not delivered.");
    }
    internal static void Key(ushort key)
    {
        var inputs = new uint[] { 0, 2 }.Select(flags => new Native.Input { Type = 1,
            Data = new Native.InputUnion { Keyboard = new Native.KeyInput { Vk = key, Flags = flags } } }).ToArray();
        if (SendInput(2, inputs, Marshal.SizeOf<Native.Input>()) != 2) throw new Exception("Test key was not delivered.");
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Native.Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string name, uint flags);
    [DllImport("user32.dll")] private static extern nint ActivateKeyboardLayout(nint layout, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char c, nint layout);
    internal static void PhysicalText(string text, string layoutName)
    {
        var layout = LoadKeyboardLayout(layoutName, 0);
        ActivateKeyboardLayout(layout, 0);
        var inputs = new List<Native.Input>();
        foreach (var c in text)
        {
            var code = VkKeyScanEx(c, layout);
            if (code < 0 || ((code >> 8) & ~1) != 0) throw new Exception("Test supports unmodified and Shift-modified physical keys.");
            var shifted = (code & 0x100) != 0;
            if (shifted) inputs.Add(new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyInput { Vk = 0x10 } } });
            foreach (var flags in new uint[] { 0, 2 })
                inputs.Add(new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyInput { Vk = (ushort)(code & 0xff), Flags = flags } } });
            if (shifted) inputs.Add(new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyInput { Vk = 0x10, Flags = 2 } } });
        }
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.Input>()) != inputs.Count)
            throw new Exception("Physical test keys were not delivered.");
    }
    internal static void Text(string text)
    {
        var inputs = new List<Native.Input>();
        foreach (var c in text)
            foreach (var flags in new uint[] { 4, 6 })
                inputs.Add(new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyInput { Scan = c, Flags = flags } } });
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.Input>()) != inputs.Count)
            throw new Exception("Synthetic input was not fully delivered to test fixture.");
    }
}

internal sealed class FakeServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public int Calls, Delay;
    public int StatusCode = 200;
    public string? RawReply, Redirect, LastPath;
    public bool TruncateBody;
    public string Endpoint { get; }
    internal FakeServer()
    {
        listener.Start();
        Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Listen();
    }
    private async Task Listen()
    {
        try { while (!stop.IsCancellationRequested) _ = Handle(await listener.AcceptTcpClientAsync(stop.Token)); }
        catch (OperationCanceledException) { }
    }
    private async Task Handle(TcpClient connection)
    {
        using (connection)
        try
        {
            using var stream = connection.GetStream();
            var header = new List<byte>(); var one = new byte[1];
            while (header.Count < 16000)
            {
                if (await stream.ReadAsync(one, stop.Token) == 0) return;
                header.Add(one[0]);
                if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            var headerText = Encoding.ASCII.GetString(header.ToArray());
            LastPath = headerText.Split(' ')[1];
            var lengthLine = headerText.Split("\r\n").FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            byte[] body;
            if (lengthLine != null)
            {
                var length = int.Parse(lengthLine.Split(':')[1]);
                body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token);
            }
            else
            {
                using var chunks = new MemoryStream();
                while (true)
                {
                    var line = new List<byte>();
                    while (true)
                    {
                        if (await stream.ReadAsync(one, stop.Token) == 0) return;
                        if (one[0] == 10) break;
                        if (one[0] != 13) line.Add(one[0]);
                    }
                    var length = Convert.ToInt32(Encoding.ASCII.GetString(line.ToArray()).Split(';')[0], 16);
                    if (length == 0) { await stream.ReadExactlyAsync(new byte[2], stop.Token); break; }
                    var chunk = new byte[length]; await stream.ReadExactlyAsync(chunk, stop.Token);
                    chunks.Write(chunk);
                    await stream.ReadExactlyAsync(new byte[2], stop.Token);
                }
                body = chunks.ToArray();
            }
            using var json = JsonDocument.Parse(body);
            var ollama = json.RootElement.TryGetProperty("prompt", out var prompt);
            var word = ollama ? prompt.GetString() : json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Interlocked.Increment(ref Calls);
            await Task.Delay(Delay, stop.Token);
            var corrected = word switch { "првиет" => "привет", "helllo" => "hello", _ => word };
            var defaultReply = ollama ? JsonSerializer.Serialize(new { response = corrected, done = true, done_reason = "stop" }) :
                JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = corrected }, finish_reason = "stop" } } });
            var reply = Encoding.UTF8.GetBytes(RawReply ?? defaultReply);
            var redirectHeader = Redirect == null ? "" : $"Location: {Redirect}\r\n";
            var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 {StatusCode} Test\r\n{redirectHeader}Content-Type: application/json\r\nContent-Length: {reply.Length + (TruncateBody ? 10 : 0)}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(prefix, stop.Token); await stream.WriteAsync(reply, stop.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
