using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Kazrich.App;
using Kazrich.Core;

internal static class ChromeFieldsChecks
{
    internal static async Task Run(Form host, string endpoint)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "chrome-fields-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var page = Path.Combine(directory, "fields.html");
        await File.WriteAllTextAsync(page, """
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Kazrich isolated field checks</title>
            <style>body{font:20px sans-serif;margin:24px}input,textarea,[contenteditable]{display:block;font:24px sans-serif;
            box-sizing:border-box;width:650px;margin:8px 0 16px;padding:8px;border:1px solid #888}textarea,[contenteditable]{height:90px}</style>
            <label>Single line<input aria-label="Kazrich single line" spellcheck="false" autocomplete="off"></label>
            <label>Multiple lines<textarea aria-label="Kazrich multiple lines" spellcheck="false"></textarea></label>
            <label>Rich editor<div role="textbox" aria-label="Kazrich rich editor" contenteditable="true" spellcheck="false"></div></label>
            <label>Read only<input aria-label="Kazrich read only" value="ghbdtn" readonly></label>
            <label>Password<input aria-label="Kazrich password" type="password" autocomplete="off"></label>
            </html>
            """);
        var start = new ProcessStartInfo(@"C:\Program Files\Google\Chrome\Application\chrome.exe") { UseShellExecute = false };
        foreach (var argument in new[] { "--user-data-dir=" + Path.Combine(directory, "profile"), "--no-first-run",
            "--no-default-browser-check", "--new-window", "--window-size=900,900", new Uri(page).AbsoluteUri }) start.ArgumentList.Add(argument);
        using var chrome = Process.Start(start)!;
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            Console.WriteLine("PASS: " + message); checks++;
        }
        try
        {
            AutomationElement? root = null;
            nint browserWindow = 0;
            var until = Environment.TickCount64 + 20000;
            while (root == null && Environment.TickCount64 < until)
            {
                await Task.Delay(200); chrome.Refresh();
                if (chrome.MainWindowHandle != 0)
                {
                    browserWindow = chrome.MainWindowHandle;
                    Native.ShowWindow(browserWindow, 5); Native.SetForegroundWindow(browserWindow);
                    root = await Task.Run(() => AutomationElement.FromHandle(browserWindow));
                }
            }
            if (root == null) throw new TimeoutException("The isolated Chrome window did not start.");
            async Task FocusField(AutomationElement field)
            {
                Native.SetForegroundWindow(browserWindow); await Task.Run(field.SetFocus);
                // Chrome can expose several top-level windows in its new profile.
                // Pin the window activated by our known field, not an unrelated
                // window later reported by Process.MainWindowHandle.
                var deadline = Environment.TickCount64 + 2000;
                do
                {
                    var activated = Native.GetForegroundWindow();
                    Native.GetWindowThreadProcessId(activated, out var processId);
                    if (processId == chrome.Id && await Task.Run(() => field.Current.HasKeyboardFocus))
                    { browserWindow = activated; return; }
                    await Task.Delay(25);
                } while (Environment.TickCount64 < deadline);
                throw new InvalidOperationException("The isolated Chrome field did not acquire focus.");
            }
            async Task<AutomationElement> Field(string name)
            {
                AutomationElement? found = null;
                var deadline = Environment.TickCount64 + 10000;
                while (found == null && Environment.TickCount64 < deadline)
                {
                    found = await Task.Run(() => root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "Kazrich " + name)).Cast<AutomationElement>()
                        .FirstOrDefault(value => value.Current.ControlType is var role &&
                            (role == ControlType.Edit || role == ControlType.Document || role == ControlType.ComboBox)));
                    if (found == null) await Task.Delay(100);
                }
                return found ?? throw new InvalidOperationException("Missing local HTML field: " + name);
            }
            async Task<string> Read(AutomationElement field) => await Task.Run(() =>
            {
                if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var raw))
                {
                    var value = TypingQueue.Normalize(((ValuePattern)raw).Current.Value);
                    // An emptied contenteditable can retain a placeholder BR.
                    return field.Current.Name == "Kazrich rich editor" ? value.TrimEnd('\n') : value;
                }
                var text = (TextPattern)field.GetCurrentPattern(TextPattern.Pattern);
                return TypingQueue.Normalize(text.DocumentRange.GetText(-1)).TrimEnd('\n');
            });
            async Task WaitText(AutomationElement field, string expected, int milliseconds = 10000)
            {
                // Rich editors use NBSP to render typed spaces; compare the
                // visible space sequence while keeping exact checks separately.
                string Visible(string value) => value.Replace('\u00a0', ' ');
                var deadline = Environment.TickCount64 + milliseconds;
                while (Visible(await Read(field)) != expected && Environment.TickCount64 < deadline) await Task.Delay(25);
                var actual = await Read(field);
                if (Visible(actual) != expected) throw new InvalidOperationException($"Local HTML text mismatch: expected '{expected}', got '{actual}'.");
            }
            var settings = SettingsFile.Load() with { Backend = "OpenAI", Endpoint = endpoint, Enabled = true };
            using var controller = new CorrectionController(host, settings, acceptSyntheticTestInput: true);
            controller.Status += Console.WriteLine;
            foreach (var name in new[] { "single line", "multiple lines", "rich editor" })
            {
                var field = await Field(name);
                controller.Configure(settings with { Enabled = false });
                await FocusField(field);
                TestInput.Chord(0x11, 0x41); TestInput.Key(8);
                await WaitText(field, ""); controller.Configure(settings);
                TestInput.Text("ghbdtn ");
                await WaitText(field, "привет ");
                var capture = await Task.Run(() => TextTarget.Capture(browserWindow, "привет ", settings));
                Check(capture != null, name + ": correction preserves the ending and exact caret");
                controller.Undo(); await WaitText(field, "ghbdtn ");
                Check(await Task.Run(() => TextTarget.Capture(browserWindow, "ghbdtn ", settings)) != null,
                    name + ": undo restores the original text and caret");
                Check(await Read(field) == "ghbdtn" + (name == "rich editor" ? "\u00a0" : " "),
                    name + ": undo preserves the field's exact trailing-space representation");

                controller.Configure(settings with { Enabled = false });
                TestInput.Chord(0x11, 0x41); TestInput.Key(8); TestInput.Text("прывет");
                await WaitText(field, "прывет"); controller.Configure(settings);
                for (var move = 0; move < 3; move++) TestInput.Key(0x25);
                TestInput.Key(8); TestInput.Text("ы");
                await WaitText(field, "привет");
                var word = await Task.Run(() => TextTarget.CaptureWord(browserWindow, settings, false));
                Check(word?.Edit.Word == "привет" && word.Edit.CaretOffset == 3,
                    name + ": editing a word preserves its interior caret");

                controller.Configure(settings with { Enabled = false });
                TestInput.Chord(0x11, 0x41); TestInput.Key(8); await WaitText(field, "");
                controller.Configure(settings); TestInput.Text("ghbdtn  helllo ");
                await WaitText(field, "ghbdtn  helllo ", 1000);
                var originalSpacing = await Read(field);
                Check(originalSpacing.Replace('\u00a0', ' ') == "ghbdtn  helllo ", name + ": repeated spaces reach the editor before correction");
                await WaitText(field, "привет  hello ");
                Check(await Task.Run(() => TextTarget.Capture(browserWindow, "привет  hello ", settings)) != null,
                    name + ": correction preserves repeated and trailing spaces");
                controller.Undo(); await WaitText(field, "ghbdtn  helllo ");
                Check(await Read(field) == originalSpacing, name + ": undo restores the exact original space characters");

                if (name != "single line")
                {
                    controller.Configure(settings with { Enabled = false });
                    TestInput.Chord(0x11, 0x41); TestInput.Key(8); TestInput.Text("Previous paragraph."); TestInput.Key(0x0D);
                    await Task.Delay(100); controller.Configure(settings); TestInput.Text("ghbdtn ");
                    await WaitText(field, "Previous paragraph.\nпривет ");
                    Check(true, name + ": correction preserves a previous paragraph");
                    controller.Undo(); await WaitText(field, "Previous paragraph.\nghbdtn ");
                    Check(true, name + ": multiline undo restores the exact source");
                }
            }
            var readOnly = await Field("read only");
            await FocusField(readOnly); await Task.Delay(100);
            Check(await Task.Run(() => TextTarget.Capture(browserWindow, "", settings, checkBoundary: false)) == null &&
                await Read(readOnly) == "ghbdtn", "a read-only HTML input blocks capture and retains its contents");
            var password = await Field("password");
            await FocusField(password); await Task.Delay(100);
            Check(password.Current.IsPassword && await Task.Run(() => TextTarget.Capture(browserWindow, "", settings,
                checkBoundary: false)) == null, "an HTML password input blocks capture before reading text");
            Console.WriteLine($"CHROME FIELDS: {checks} checks passed.");
        }
        finally
        {
            if (!chrome.HasExited) { chrome.Kill(entireProcessTree: true); await chrome.WaitForExitAsync(); }
        }
    }
}
