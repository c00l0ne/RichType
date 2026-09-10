using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Kazrich.App;
using Kazrich.Core;

internal static class ChromeChecks
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string name, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    internal static async Task Run(Form host, string endpoint)
    {
        var settings = SettingsFile.Load() with { Backend = "OpenAI", Endpoint = endpoint, Enabled = true };
        using var controller = new CorrectionController(host, settings, acceptSyntheticTestInput: true);
        var status = "";
        controller.Status += message => { status = message; Console.WriteLine(message); };
        var profile = Path.GetFullPath(Path.Combine("artifacts", "chrome-check-" + Guid.NewGuid().ToString("N")));
        var start = new ProcessStartInfo(@"C:\Program Files\Google\Chrome\Application\chrome.exe") { UseShellExecute = false };
        foreach (var argument in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--new-window", "https://www.google.com/" })
            start.ArgumentList.Add(argument);
        using var chrome = Process.Start(start)!;
        try
        {
            AutomationElement? search = null;
            var deadline = Environment.TickCount64 + 25000;
            while (search == null && Environment.TickCount64 < deadline)
            {
                await Task.Delay(300);
                chrome.Refresh();
                if (chrome.MainWindowHandle == 0) continue;
                var window = chrome.MainWindowHandle;
                Native.ShowWindow(window, 5);
                Native.SetForegroundWindow(window);
                search = await Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(window);
                    var reject = root.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Отклонить все"),
                            new PropertyCondition(AutomationElement.NameProperty, "Reject all"))));
                    if (reject != null && reject.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                    { ((InvokePattern)invoke).Invoke(); return null; }
                    var fields = root.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                    foreach (AutomationElement field in fields)
                        Console.WriteLine($"FIELD: {field.Current.ControlType.ProgrammaticName}, {field.Current.Name}, text={field.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty)}");
                    return fields.Cast<AutomationElement>().FirstOrDefault(field => field.Current.IsEnabled &&
                        !field.Current.IsOffscreen && (field.Current.Name is "Поиск" or "Найти" or "Search" || field.Current.AutomationId == "APjFqb"));
                });
            }
            if (search == null)
            {
                var root = AutomationElement.FromHandle(chrome.MainWindowHandle);
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Take(60))
                    Console.WriteLine($"PAGE: {element.Current.ControlType.ProgrammaticName}, {element.Current.Name}");
                var bounds = root.Current.BoundingRectangle;
                using var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.CopyFromScreen((int)bounds.X, (int)bounds.Y, 0, 0, bitmap.Size);
                bitmap.Save("artifacts/chrome-check.png");
                throw new Exception("Google search field was not exposed in the isolated Chrome window.");
            }
            Native.ShowWindow(chrome.MainWindowHandle, 5);
            Native.SetForegroundWindow(chrome.MainWindowHandle);
            await Task.Run(search.SetFocus);
            await Task.Delay(200);
            Console.WriteLine($"GOOGLE FIELD: {search.Current.ControlType.ProgrammaticName}, {search.Current.FrameworkId}");
            var captured = await Task.Run(() => TextTarget.Capture(chrome.MainWindowHandle, "", settings, checkBoundary: false));
            Console.WriteLine("Capture before input: " + (captured != null));
            async Task<string> Read() => await Task.Run(() => ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            TestInput.Text("yjdjcnb yferb");
            deadline = Environment.TickCount64 + 10000;
            while (await Read() != "новости науки" && Environment.TickCount64 < deadline) await Task.Delay(100);
            var actual = await Read();
            Console.WriteLine("GOOGLE RESULT: " + actual + " | " + status);
            if (actual != "новости науки") throw new Exception("The reported Google search query was not corrected.");
            Console.WriteLine("PASS: Chrome Google search query corrects without submitting the search");
            controller.Undo();
            await Task.Delay(300);
            if (await Read() != "новости yferb" && await Read() != "yjdjcnb yferb")
                throw new Exception("Undo did not restore the original final correction in Google search.");
            Console.WriteLine("PASS: Chrome Google search supports undo");
            TestInput.Chord(0x11, 0x41);
            await Task.Delay(100);
            captured = await Task.Run(() => TextTarget.Capture(chrome.MainWindowHandle, "", settings, checkBoundary: false));
            if (captured != null) throw new Exception("Selected text must block capture in a combo box.");
            Console.WriteLine("PASS: selecting Google query blocks replacement");
            var layout = LoadKeyboardLayout("00000409", 0);
            PostMessage(chrome.MainWindowHandle, 0x50, 0, layout);
            var thread = Native.GetWindowThreadProcessId(chrome.MainWindowHandle, out _);
            deadline = Environment.TickCount64 + 3000;
            while ((Native.GetKeyboardLayout(thread).ToInt64() & 0xffff) != 0x409 && Environment.TickCount64 < deadline)
                await Task.Delay(50);
            if ((Native.GetKeyboardLayout(thread).ToInt64() & 0xffff) != 0x409)
                throw new Exception("Could not select the English layout in the isolated Chrome window.");
            TestInput.Key(8);
            TestInput.PhysicalText("yjdjcnb yferb", "00000409");
            deadline = Environment.TickCount64 + 10000;
            while (await Read() != "новости науки" && Environment.TickCount64 < deadline) await Task.Delay(100);
            if (await Read() != "новости науки") throw new Exception("Physical Chrome typing was not corrected.");
            Console.WriteLine("PASS: physical English keys correct the Google search query after clearing");
            foreach (var prefix in new[] { "ghbrjkmyj", "ghbrjkmy" })
            {
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                TestInput.PhysicalText(prefix, "00000409");
                deadline = Environment.TickCount64 + 1000;
                while (await Read() != prefix && Environment.TickCount64 < deadline) await Task.Delay(20);
                if (await Read() != prefix) throw new Exception("The initial mixed-word fragment was not delivered in Google.");
                TestInput.Chord(0x11, 0x12);
                var russian = LoadKeyboardLayout("00000419", 0);
                PostMessage(chrome.MainWindowHandle, 0x50, 0, russian);
                deadline = Environment.TickCount64 + 1000;
                while ((Native.GetKeyboardLayout(thread).ToInt64() & 0xffff) != 0x419 && Environment.TickCount64 < deadline) await Task.Delay(20);
                TestInput.PhysicalText("о то что можно переключаться прям на лету ", "00000419");
                const string expectedSwitch = "прикольно то что можно переключаться прям на лету ";
                deadline = Environment.TickCount64 + 12000;
                while (await Read() != expectedSwitch && Environment.TickCount64 < deadline) await Task.Delay(50);
                if (await Read() != expectedSwitch) throw new Exception("Mid-word Ctrl+Alt input failed in Google: " + await Read());
                Console.WriteLine("PASS: Google keeps and corrects the first mixed word after Ctrl+Alt: " + prefix);
                PostMessage(chrome.MainWindowHandle, 0x50, 0, layout);
                deadline = Environment.TickCount64 + 1000;
                while ((Native.GetKeyboardLayout(thread).ToInt64() & 0xffff) != 0x409 && Environment.TickCount64 < deadline) await Task.Delay(20);
            }
            TestInput.Chord(0x11, 0x41); TestInput.Key(8);
            TestInput.Text("[jчу");
            deadline = Environment.TickCount64 + 6000;
            while (await Read() != "хочу" && Environment.TickCount64 < deadline) await Task.Delay(100);
            if (await Read() != "хочу") throw new Exception("The mixed word with a leading bracket key was not corrected in Google search: " + await Read() + " | " + status);
            Console.WriteLine("PASS: Google search corrects a mixed word beginning with a Russian letter key");
            await Task.Delay(100);
            var edited = await Task.Run(() => TextTarget.CaptureWord(chrome.MainWindowHandle, settings, false));
            if (edited == null || edited.Edit.Word != "хочу" || edited.Edit.Before.Length != 0 || edited.Edit.After.Length != 0 || edited.Edit.CaretOffset != 4)
                throw new Exception("Google text capture escaped the focused search field: " + edited?.Edit);
            Console.WriteLine("PASS: Google word context excludes surrounding page text and suggestions");
            foreach (var delay in new[] { 0, 40 })
            {
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                foreach (var c in "ntrcn ghj,ysq")
                {
                    TestInput.PhysicalText(c.ToString(), "00000409");
                    if (delay > 0) await Task.Delay(delay);
                }
                deadline = Environment.TickCount64 + 10000;
                while (await Read() != "текст пробный" && Environment.TickCount64 < deadline) await Task.Delay(100);
                if (await Read() != "текст пробный") throw new Exception($"First Google word was skipped at {delay} ms: {await Read()} | {status}");
                Console.WriteLine($"PASS: both Google query words correct at {delay} ms per key");
            }
            TestInput.Chord(0x11, 0x41); TestInput.Key(8);
            TestInput.PhysicalText("ntrcn ", "00000409");
            await Task.Delay(1200);
            TestInput.PhysicalText("ghj,ysq", "00000409");
            deadline = Environment.TickCount64 + 10000;
            while (await Read() != "текст пробный" && Environment.TickCount64 < deadline) await Task.Delay(100);
            if (await Read() != "текст пробный") throw new Exception("The retained first Google word did not use its new corrected neighbour: " + await Read());
            Console.WriteLine("PASS: first Google word corrects after a pause and a newly typed neighbour");
            const string paragraphInput = "Vfvf vskf hfve/ Hfvf ,skf xbcnfz/ Yfnegbkj ntgkjt ktnj/ D cfle cjphtkb ckflrbt z,kjyb b dbiyb/";
            foreach (var delay in new[] { 0, 25, 80 })
            {
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                foreach (var c in "Yfccnegbkj ntgkjt ktnj")
                {
                    TestInput.PhysicalText(c.ToString(), "00000409");
                    if (delay > 0) await Task.Delay(delay);
                }
                deadline = Environment.TickCount64 + 10000;
                while (await Read() != "Наступило теплое лето" && Environment.TickCount64 < deadline) await Task.Delay(100);
                if (await Read() != "Наступило теплое лето") throw new Exception("The duplicated letter was not corrected in Google: " + await Read());
                Console.WriteLine("PASS: Google corrects the duplicated letter after layout conversion at " + delay + " ms per key");
            }
            const string paragraphExpected = "Мама мыла раму. Рама была чистая. Наступило теплое лето. В саду созрели сладкие яблони и вишни.";
            foreach (var delay in new[] { 0, 25, 80 })
            {
                controller.Configure(settings with { Enabled = false });
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                TestInput.Text(paragraphExpected);
                deadline = Environment.TickCount64 + 5000;
                while (await Read() != paragraphExpected && Environment.TickCount64 < deadline) await Task.Delay(50);
                if (await Read() != paragraphExpected) throw new Exception("Could not prepare the sentence prefix in the isolated Chrome field.");
                controller.Configure(settings);
                const string continuation = "Vfktymrbq rjntyjr dtctkj buhfk c ,tksv rke,rjv ybnjr yf ковер.";
                foreach (var c in continuation)
                {
                    if (c >= 'А') TestInput.Text(c.ToString());
                    else TestInput.PhysicalText(c.ToString(), "00000409");
                    if (delay > 0) await Task.Delay(delay);
                }
                const string expectedContinuation = paragraphExpected + "Маленький котенок весело играл с белым клубком ниток на ковер.";
                deadline = Environment.TickCount64 + 20000;
                while (await Read() != expectedContinuation && Environment.TickCount64 < deadline) await Task.Delay(100);
                if (await Read() != expectedContinuation) throw new Exception("Sentence continuation after a dot was not corrected in Chrome: " + await Read());
                Console.WriteLine("PASS: the full continuation after a dot without whitespace corrects in Google at " + delay + " ms per key");
            }
            foreach (var delay in new[] { 0, 25, 80 })
            {
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                foreach (var c in paragraphInput)
                {
                    TestInput.PhysicalText(c.ToString(), "00000409");
                    if (delay > 0) await Task.Delay(delay);
                }
                deadline = Environment.TickCount64 + 30000;
                while (await Read() != paragraphExpected && Environment.TickCount64 < deadline) await Task.Delay(100);
                if (await Read() != paragraphExpected)
                {
                    Console.WriteLine("GOOGLE FOREGROUND: " + (Native.GetForegroundWindow() == chrome.MainWindowHandle) + ", modifiers=" + Native.ModifiersDown());
                    await Task.Run(() =>
                    {
                        var focused = AutomationElement.FocusedElement;
                        Console.WriteLine("GOOGLE FOCUS: " + focused.Current.ControlType.ProgrammaticName + ", " + focused.Current.Name);
                        if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern))
                        {
                            var pattern = (TextPattern)rawPattern;
                            var selection = pattern.GetSelection().Single();
                            var before = pattern.DocumentRange.Clone();
                            before.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, selection, System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
                            var after = pattern.DocumentRange.Clone();
                            after.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, selection, System.Windows.Automation.Text.TextPatternRangeEndpoint.End);
                            Console.WriteLine("GOOGLE CARET: before=" + before.GetText(200) + " | after=" + after.GetText(200));
                        }
                    });
                    throw new Exception("The reported paragraph failed in Google at " + delay + " ms: " + await Read());
                }
                Console.WriteLine("PASS: the full reported paragraph corrects in Google at " + delay + " ms per key");
            }
            foreach (var input in new[] { "ntrcn", "ntrcn " })
            {
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                TestInput.PhysicalText(input, "00000409");
                var expected = input.EndsWith(' ') ? "текст " : "текст";
                deadline = Environment.TickCount64 + 7000;
                while (await Read() != expected && Environment.TickCount64 < deadline) await Task.Delay(100);
                if (await Read() != expected) throw new Exception("Standalone Google word did not correct: " + await Read());
                Console.WriteLine("PASS: standalone Google word corrects without any neighbour: " + input);
                controller.Undo();
                await Task.Delay(300);
                if (await Read() != input) throw new Exception("Standalone Google word undo failed.");
                Console.WriteLine("PASS: standalone Google word undo restores its exact original");
            }
        }
        finally
        {
            if (!chrome.HasExited) { chrome.Kill(entireProcessTree: true); await chrome.WaitForExitAsync(); }
        }
    }
}
