using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Kazrich.App;
using Kazrich.Core;

internal static class MainLifecycleChecks
{
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }
    private static async Task WaitFor(Func<bool> condition, int milliseconds = 30000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < until) await Task.Delay(25);
        if (!condition()) throw new TimeoutException("The isolated settings UI did not finish its operation.");
    }
    internal static void Run()
    {
        var saved = new List<Settings>();
        using var form = new MainForm(new Settings { Enabled = false, CpuOnly = true }, false, saved.Add);
        var managed = Field<ManagedModel>(form, "managed");
        var instructions = Field<Button>(form, "instructions");
        var becameBusy = false;
        instructions.EnabledChanged += (_, _) => becameBusy |= !instructions.Enabled;
        int ProcessId() => Field<Process>(managed, "process").Id;
        async Task EditInstruction(string? text, string field = "spelling")
        {
            becameBusy = false;
            var beforeSaveCount = saved.Count;
            instructions.PerformClick();
            await WaitFor(() => Application.OpenForms.OfType<InstructionsForm>().Any(), 5000);
            var editor = Application.OpenForms.OfType<InstructionsForm>().Single();
            var root = (TableLayoutPanel)editor.Controls[0];
            var buttons = root.Controls.OfType<FlowLayoutPanel>().Single();
            if (text == null) buttons.Controls.OfType<Button>().Single(button => button.Text == "Вернуть стандартные").PerformClick();
            else Field<TextBox>(editor, field).Text = text;
            buttons.Controls.OfType<Button>().Single(button => button.Text == "Применить сейчас").PerformClick();
            await WaitFor(() => saved.Count > beforeSaveCount && instructions.Enabled && managed.IsRunning(Field<Settings>(form, "settings")));
        }
        form.Shown += async (_, _) =>
        {
            try
            {
                await WaitFor(() => instructions.Enabled && managed.IsRunning(Field<Settings>(form, "settings")));
                Check(becameBusy, "the instruction editor is disabled while the managed model starts");
                var enabled = Field<CheckBox>(form, "enabled");
                enabled.Checked = true;
                Check(Field<Settings>(form, "settings").Enabled && Field<CorrectionController>(form, "controller").Settings.Enabled && saved[^1].Enabled,
                    "the automatic correction checkbox immediately updates the controller and saved settings");
                enabled.Checked = false;
                Check(!Field<Settings>(form, "settings").Enabled && !Field<CorrectionController>(form, "controller").Settings.Enabled && !saved[^1].Enabled,
                    "pausing by checkbox immediately updates the controller and saved settings");
                Field<TextBox>(form, "ignored").Text = "lifecycleword";
                Field<Button>(form, "save").PerformClick();
                Check(Field<Settings>(form, "settings").IgnoredWords == "lifecycleword", "the explicit settings save applies the edited exclusion list");
                var firstProcess = ProcessId();
                var longInstruction = string.Concat(Enumerable.Repeat("Сохраняй исходное слово без изменений. ", 150)).TrimEnd();
                await EditInstruction(longInstruction);
                Console.WriteLine($"Long-instruction UI state: saved={saved[^1].SpellingInstructions.Length}/{longInstruction.Length}, busy={becameBusy}, process={firstProcess}/{ProcessId()}, context={saved[^1].RequiredContextTokens()}");
                Check(saved[^1].SpellingInstructions == longInstruction && becameBusy && ProcessId() != firstProcess,
                    "applying a long instruction through the dialog saves it and restarts the managed model automatically");
                Check(!Field<Settings>(form, "settings").Enabled, "instruction reload preserves the user's paused state");
                Check(Field<Settings>(form, "settings").IgnoredWords == "lifecycleword" &&
                    Field<CorrectionController>(form, "controller").Settings.SpellingInstructions == longInstruction,
                    "editing instructions retains the latest settings and updates the active model client");
                var largeProcess = ProcessId();
                await EditInstruction(longInstruction.Replace("слово", "текст"));
                Check(ProcessId() == largeProcess && !becameBusy,
                    "an instruction change within the allocated context applies without restarting the model");
                await EditInstruction(null);
                Check(saved[^1].SpellingInstructions == Settings.DefaultSpellingInstructions && becameBusy && ProcessId() != largeProcess,
                    "restoring defaults through the dialog reloads the model with the smaller context");
                var beforeEnglish = ProcessId();
                const string englishInstruction = "Correct English typos and preserve regional spelling. Return only the result.";
                await EditInstruction(englishInstruction, "englishSpelling");
                Check(saved[^1].EnglishSpellingInstructions == englishInstruction &&
                    Field<CorrectionController>(form, "controller").Settings.EnglishSpellingInstructions == englishInstruction &&
                    saved[^1].SpellingInstructions == Settings.DefaultSpellingInstructions,
                    "the English instruction applies and saves independently of Russian spelling");
                Check(ProcessId() == beforeEnglish && !becameBusy, "a short English instruction applies without reloading the model");
                Check(saved.Count == 7, "the isolated settings flow performs exactly the seven requested saves");
            }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                typeof(MainForm).GetField("exit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
                form.Close();
            }
        };
        Application.Run(form);
    }

    internal static void RunSaveFailure()
    {
        var attempted = 0;
        using var form = new MainForm(new Settings { Backend = "OpenAI", Enabled = false }, false,
            _ => { attempted++; throw new IOException("isolated write failure"); });
        form.Shown += async (_, _) =>
        {
            try
            {
                Field<Button>(form, "instructions").PerformClick();
                await WaitFor(() => Application.OpenForms.OfType<InstructionsForm>().Any(), 5000);
                var editor = Application.OpenForms.OfType<InstructionsForm>().Single();
                const string custom = "Return only the unchanged word.";
                Field<TextBox>(editor, "spelling").Text = custom;
                const string customEnglish = "Preserve English regional spelling and return only the word.";
                Field<TextBox>(editor, "englishSpelling").Text = customEnglish;
                ((TableLayoutPanel)editor.Controls[0]).Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                    .Single(button => button.Text == "Применить сейчас").PerformClick();
                await WaitFor(() => Field<Settings>(form, "settings").SpellingInstructions == custom, 5000);
                Check(attempted == 1 && Field<Label>(form, "status").Text.Contains("не сохранены") &&
                    Field<CorrectionController>(form, "controller").Settings.SpellingInstructions == custom &&
                    Field<CorrectionController>(form, "controller").Settings.EnglishSpellingInstructions == customEnglish,
                    "a failed instruction save remains visible while the instruction applies to the current session");
                Field<CheckBox>(form, "enabled").Checked = true;
                Check(attempted == 2 && Field<Settings>(form, "settings").Enabled && Field<Label>(form, "status").Text.Contains("не сохранены"),
                    "a failed pause-state save is reported without pretending persistence succeeded");
            }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                typeof(MainForm).GetField("exit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
                form.Close();
            }
        };
        Application.Run(form);
    }
}
