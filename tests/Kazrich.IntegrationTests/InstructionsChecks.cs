using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Kazrich.App;
using Kazrich.Core;

internal static class InstructionsChecks
{
    internal static void Run()
    {
        using var dialog = new InstructionsForm(new Settings { SpellingChoiceInstructions = "custom candidate instruction", EnglishSpellingInstructions = "custom English instruction" });
        var output = Path.GetFullPath("artifacts/instructions-qa");
        Directory.CreateDirectory(output);
        dialog.Shown += async (_, _) =>
        {
            try
            {
                var root = (TableLayoutPanel)dialog.Controls[0];
                var tabs = root.Controls.OfType<TabControl>().Single();
                if (tabs.TabPages.Count != 8 || dialog.SpellingChoiceInstructions != "custom candidate instruction" || dialog.EnglishSpellingInstructions != "custom English instruction")
                    throw new InvalidOperationException("Instructions did not preserve custom settings.");
                foreach (var size in new[] { new Size(800, 620), new Size(640, 450) })
                {
                    dialog.Size = size;
                    for (var index = 0; index < tabs.TabPages.Count; index++)
                    {
                        tabs.SelectedIndex = index;
                        await Task.Delay(50);
                        var content = (TableLayoutPanel)tabs.SelectedTab!.Controls[0];
                        var box = content.Controls.OfType<TextBox>().Single();
                        if (box.ClientSize.Height < 90 || box.ClientSize.Width < 450 || box.TextLength == 0)
                            throw new InvalidOperationException("Instruction editor became unusable at " + size + ": " + tabs.SelectedTab.Text);
                        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                        bitmap.Save(Path.Combine(output, $"{size.Width}-{index}.png"), ImageFormat.Png);
                    }
                }
                var buttons = root.Controls.OfType<FlowLayoutPanel>().Single();
                buttons.Controls.OfType<Button>().Single(button => button.Text == "Вернуть стандартные").PerformClick();
                if (dialog.SpellingChoiceInstructions != Settings.DefaultSpellingChoiceInstructions ||
                    dialog.WordValidityInstructions != Settings.DefaultWordValidityInstructions || dialog.Spelling != Settings.DefaultSpellingInstructions ||
                    dialog.EnglishSpellingInstructions != Settings.DefaultEnglishSpellingInstructions)
                    throw new InvalidOperationException("Reset did not restore all instructions.");
                Console.WriteLine("PASS: eight instruction editors retain custom text, remain usable at both window sizes, and reset together.");
                Console.WriteLine("INSTRUCTIONS QA: " + output);
            }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally { dialog.Close(); }
        };
        Application.Run(dialog);
    }
}
