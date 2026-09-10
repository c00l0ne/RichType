using System.Drawing;
using System.Windows.Forms;
using Kazrich.Core;

namespace Kazrich.App;

internal sealed class InstructionsForm : Form
{
    private readonly TextBox spelling = new(), englishSpelling = new(), layout = new(), phraseLayout = new(), contextLayout = new(), hyphens = new(), wordValidity = new(), spellingChoice = new();
    internal string WordValidityInstructions => wordValidity.Text.Trim();
    internal string Spelling => spelling.Text.Trim();
    internal string EnglishSpellingInstructions => englishSpelling.Text.Trim();
    internal string LayoutInstructions => layout.Text.Trim();
    internal string PhraseLayoutInstructions => phraseLayout.Text.Trim();
    internal string ContextLayoutInstructions => contextLayout.Text.Trim();
    internal string HyphenInstructions => hyphens.Text.Trim();
    internal string SpellingChoiceInstructions => spellingChoice.Text.Trim();
    internal InstructionsForm(Settings settings)
    {
        Text = "Инструкции ИИ";
        Size = new Size(800, 620);
        MinimumSize = new Size(640, 450);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(root);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Изменения применятся сразу, кэш очистится. Длинная инструкция может вызвать автоматическую перезагрузку модели. Просите только результат или выбранную строку, без пояснений." }, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill, Multiline = true };
        root.Controls.Add(tabs, 0, 1);
        foreach (var (title, description, box) in new[] {
            ("Опечатки RU", "Русское слово или смешанный контекст. Ответ — исправленный текст.", spelling),
            ("Опечатки EN", "Английское слово или контекст. Сохраняется исходный вариант английского написания.", englishSpelling),
            ("Раскладка", "Варианты раскладки и написания слова. Ответ — точная копия выбранного варианта.", layout),
            ("Группа слов", "Несколько слов одной раскладки. Ответ — точная копия выбранной строки.", phraseLayout),
            ("Контекст", "Одно слово с соседями. Ответ — точная копия выбранной строки.", contextLayout),
            ("Пробелы и дефисы", "Раздельное и дефисное написание. Буквы и порядок слов сохраняются.", hyphens),
            ("Распознавание", "Распознавание отдельного слова. Ответ — ДА или НЕТ.", wordValidity),
            ("Варианты опечатки", "В первой строке исходное слово, затем варианты. Ответ — одна выбранная строка.", spellingChoice) })
        {
            box.Dock = DockStyle.Fill; box.Multiline = true; box.ScrollBars = ScrollBars.Vertical;
            box.MaxLength = 8000; box.AcceptsReturn = true;
            var page = new TabPage(title) { Padding = new Padding(10) };
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label { Dock = DockStyle.Fill, Text = description }, 0, 0);
            content.Controls.Add(box, 0, 1);
            page.Controls.Add(content); tabs.TabPages.Add(page);
        }
        spelling.Text = settings.SpellingInstructions;
        englishSpelling.Text = settings.EnglishSpellingInstructions;
        layout.Text = settings.LayoutInstructions;
        phraseLayout.Text = settings.PhraseLayoutInstructions;
        contextLayout.Text = settings.ContextLayoutInstructions;
        hyphens.Text = settings.HyphenInstructions;
        wordValidity.Text = settings.WordValidityInstructions;
        spellingChoice.Text = settings.SpellingChoiceInstructions;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Применить сейчас", AutoSize = true, Height = 33 };
        save.Click += (_, _) =>
        {
            if (Spelling.Length == 0 || EnglishSpellingInstructions.Length == 0 || LayoutInstructions.Length == 0 || PhraseLayoutInstructions.Length == 0 || ContextLayoutInstructions.Length == 0 || HyphenInstructions.Length == 0 || WordValidityInstructions.Length == 0 || SpellingChoiceInstructions.Length == 0)
            { MessageBox.Show(this, "Инструкции не должны быть пустыми."); return; }
            DialogResult = DialogResult.OK; Close();
        };
        var cancel = new Button { Text = "Отмена", AutoSize = true, Height = 33, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Вернуть стандартные", AutoSize = true, Height = 33 };
        reset.Click += (_, _) => { spelling.Text = Settings.DefaultSpellingInstructions; layout.Text = Settings.DefaultLayoutInstructions; phraseLayout.Text = Settings.DefaultPhraseLayoutInstructions; contextLayout.Text = Settings.DefaultContextLayoutInstructions; hyphens.Text = Settings.DefaultHyphenInstructions; };
        buttons.Controls.AddRange([save, cancel, reset]);
        reset.Click += (_, _) => wordValidity.Text = Settings.DefaultWordValidityInstructions;
        reset.Click += (_, _) => spellingChoice.Text = Settings.DefaultSpellingChoiceInstructions;
        reset.Click += (_, _) => englishSpelling.Text = Settings.DefaultEnglishSpellingInstructions;
        root.Controls.Add(buttons, 0, 2);
        CancelButton = cancel;
    }
}
