using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Automation;
using Kazrich.Core;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Kazrich.App;

internal sealed class MainForm : Form
{
    private readonly TextBox endpoint = new(), apps = new(), ignored = new();
    private readonly ComboBox model = new();
    private readonly ComboBox backend = new();
    private readonly ComboBox compute = new();
    private readonly Label currentModel = new();
    private readonly NumericUpDown threads = new(), delay = new(), timeout = new();
    private readonly CheckBox cpuOnly = new(), enabled = new(), fixLayout = new(), fixHyphens = new();
    private readonly Label status = new(), testResult = new();
    private readonly Label readiness = new();
    private readonly System.Windows.Forms.Timer readinessTimer = new() { Interval = 1000 };
    private bool externalReady;
    private readonly Button check = new(), save = new(), instructions = new();
    private readonly ManagedModel managed = new();
    private CancellationTokenSource? setupCancellation;
    private readonly WpfTextBox typing = new();
    private readonly NotifyIcon tray = new();
    private readonly ToolStripMenuItem pauseItem = new();
    private CorrectionController? controller;
    private Settings settings;
    private readonly Action<Settings> writeSettings;
    private bool exit, loading;
    private readonly bool startHidden;
    private readonly CancellationTokenSource closing = new();

    internal MainForm(Settings initialSettings, bool startHidden, Action<Settings>? writeSettings = null)
    {
        settings = initialSettings;
        this.writeSettings = writeSettings ?? SettingsFile.Save;
        this.startHidden = startHidden;
        Text = "RichType • локальный автокорректор";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(780, 780);
        MinimumSize = new Size(720, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 251);
        Icon = SystemIcons.Information;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(root);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 49, 81), Padding = new Padding(22, 8, 22, 8), RowCount = 2, ColumnCount = 1 };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "RichType", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 24), Dock = DockStyle.Fill }, 0, 0);
        heading.Controls.Add(new Label { Text = "Русский + English · исправление слов на вашем компьютере", ForeColor = Color.FromArgb(197, 211, 234), Dock = DockStyle.Fill }, 0, 1);
        root.Controls.Add(heading, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 10, 20, 10) };
        root.Controls.Add(scroll, 0, 1);
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14, 8, 14, 8), BackColor = Color.White };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(form);
        var row = 0;
        void Add(string title, Control control, int height = 36)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            form.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            control.Dock = control is NumericUpDown or ComboBox ? DockStyle.Left : DockStyle.Fill;
            if (control is NumericUpDown number) { number.Width = 100; number.TextAlign = HorizontalAlignment.Right; }
            if (control is ComboBox choice) { choice.Width = 320; choice.FlatStyle = FlatStyle.Flat; }
            control.Margin = new Padding(3, 5, 3, 3);
            form.Controls.Add(control, 1, row++);
        }
        void Wide(Control control, int height)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            control.Dock = DockStyle.Fill;
            form.Controls.Add(control, 0, row++);
            form.SetColumnSpan(control, 2);
        }
        enabled.Text = "Исправлять автоматически";
        enabled.AutoSize = true;
        enabled.CheckedChanged += (_, _) =>
        {
            if (loading || controller == null) return;
            settings = settings with { Enabled = enabled.Checked };
            ReconfigureController();
            UpdateTray();
            RefreshReadiness();
            if (!TrySaveSettings(out var saveError)) status.Text = saveError;
        };
        Wide(enabled, 33);
        readiness.Font = new Font("Segoe UI Semibold", 11);
        readiness.Padding = new Padding(10, 0, 10, 0);
        readiness.TextAlign = ContentAlignment.MiddleLeft;
        readiness.Margin = new Padding(0, 0, 0, 8);
        Wide(readiness, 48);

        model.Items.AddRange(["qwen3.5:0.8b", "qwen3.5:2b"]);
        currentModel.TextAlign = ContentAlignment.MiddleLeft;
        Add("Модель", currentModel);
        compute.DropDownStyle = ComboBoxStyle.DropDownList;
        compute.Items.AddRange(["Видеокарта NVIDIA (GPU)", "Процессор (CPU)"]);
        var computeRow = row;
        Add("Вычисления", compute);
        threads.Minimum = 1; threads.Maximum = 64;
        delay.Minimum = 0; delay.Maximum = 2000; delay.Increment = 50;
        timeout.Minimum = 1; timeout.Maximum = 120;
        var threadsRow = row;
        Add("Потоки процессора", threads);
        Add("Пауза проверки, мс", delay);
        var advanced = new CheckBox { Name = "AdvancedSettings", Text = "Дополнительные настройки", AutoSize = true };
        Wide(advanced, 34);
        backend.DropDownStyle = ComboBoxStyle.DropDownList;
        backend.Items.AddRange(["Встроенный (автоматически)", "Ollama (внешний)", "Другой локальный сервер"]);
        var backendRow = row;
        Add("Движок", backend);
        var addressRow = row;
        Add("Адрес сервера", endpoint);
        var modelRow = row;
        Add("Другая модель", model);
        fixLayout.Text = "Исправлять неверную раскладку";
        var layoutRow = row;
        Add("Раскладка RU / EN", fixLayout);
        fixHyphens.Text = "Проверять пробелы и дефисы";
        var hyphensRow = row;
        Add("Написание", fixHyphens);
        var timeoutRow = row;
        Add("Ожидание ответа, сек", timeout);
        var appsRow = row;
        Add("Приложения", apps);
        ignored.Multiline = true;
        ignored.ScrollBars = ScrollBars.Vertical;
        var ignoredRow = row;
        Add("Не исправлять эти слова", ignored, 58);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        save.Text = "Применить"; save.AutoSize = true; save.Height = 32;
        save.BackColor = Color.FromArgb(43, 91, 184); save.ForeColor = Color.White; save.FlatStyle = FlatStyle.Flat;
        save.Click += (_, _) => ApplySettings();
        check.Text = "Проверить модель";
        check.AutoSize = true; check.Height = 32;
        check.Click += async (_, _) =>
        {
            if (setupCancellation != null) { setupCancellation.Cancel(); return; }
            await CheckModel();
        };
        var help = new Button { Text = "Справка", AutoSize = true, Height = 32 };
        help.Click += (_, _) => MessageBox.Show(this,
            "1. Выберите «Видеокарта NVIDIA (GPU)» или «Процессор (CPU)» в поле «Вычисления».\n\n" +
            "2. Нажмите «Применить». Если модель ещё не установлена — «Скачать и запустить». Текущая модель указана на основном экране.\n\n" +
            "3. После сообщения «Модель готова» печатайте в поле проверки или добавленных приложениях.\n\n" +
            "При следующих запусках движок и модель включаются автоматически. Команды и Ollama не нужны. Вычисления идут на CPU или NVIDIA GPU.\n\n" +
            "«Дополнительные настройки» нужны для смены модели или подключения своего сервера. «Движок» — программа, которая запускает модель; обычно оставьте встроенный. Потоки процессора отображаются только при выборе CPU.\n\n" +
            "Пароли, выделенный текст, неизвестные поля, числа и CamelCase пропускаются. Смешанные алфавиты проверяются при включённой коррекции раскладки.\n" +
            "Модель проверяет законченные слова в фоне, пока вы печатаете дальше. Очередь — до 2048 символов, проверка частями до 160.\n" +
            "После паузы в наборе от 0,35 с проверяется и последнее слово без пробела. К этой паузе добавляется время работы модели.\n" +
            "Enter завершает слово в многострочном поле. Если сообщение уже отправлено и поле очищено, замена пропускается.\n" +
            "Если вернуться к старому слову и изменить его, оно проверяется целиком после короткой паузы, без нового пробела.\n" +
            "Закрытие окна сворачивает программу в трей. Выход — через меню значка.",
            "Запуск RichType", MessageBoxButtons.OK, MessageBoxIcon.Information);
        instructions.Text = "Инструкции ИИ"; instructions.AutoSize = true; instructions.Height = 32;
        instructions.Click += (_, _) => BeginInvoke((Action)(async () =>
        {
            using var editor = new InstructionsForm(settings);
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            settings = settings with { SpellingInstructions = editor.Spelling, LayoutInstructions = editor.LayoutInstructions,
                EnglishSpellingInstructions = editor.EnglishSpellingInstructions,
                PhraseLayoutInstructions = editor.PhraseLayoutInstructions, ContextLayoutInstructions = editor.ContextLayoutInstructions,
                WordValidityInstructions = editor.WordValidityInstructions,
                SpellingChoiceInstructions = editor.SpellingChoiceInstructions,
                HyphenInstructions = editor.HyphenInstructions };
            TrySaveSettings(out var saveError);
            ReconfigureController();
            status.Text = saveError ?? "Новые инструкции применены • кэш очищен";
            if (settings.Backend == "Managed" && managed.IsInstalled(settings.Model, settings.CpuOnly) && !managed.IsRunning(settings))
                await RunManaged(false, false);
            if (saveError != null && !IsDisposed) status.Text = saveError;
        }));
        buttons.Controls.AddRange([save, check, instructions, help]);
        foreach (Button button in buttons.Controls)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = button == save ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(219, 226, 235);
            button.Padding = new Padding(8, 0, 8, 0);
            button.Margin = new Padding(0, 4, 8, 4);
        }
        Wide(buttons, 46);
        testResult.Text = "Первый запуск: скачайте модель. Дальше она запускается автоматически.";
        testResult.ForeColor = Color.FromArgb(65, 82, 105);
        testResult.Font = new Font("Segoe UI", 9);
        Wide(testResult, 36);

        Wide(new Label { Text = "Проверка автокоррекции", Font = new Font("Segoe UI Semibold", 11) }, 28);
        typing.AcceptsReturn = true;
        typing.TextWrapping = System.Windows.TextWrapping.Wrap;
        typing.FontSize = 17;
        typing.Padding = new System.Windows.Thickness(10);
        typing.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        AutomationProperties.SetAutomationId(typing, "KazrichTypingArea");
        AutomationProperties.SetName(typing, "Поле проверки автокорректора");
        var host = new ElementHost { Child = typing };
        Wide(host, 150);
        Wide(new Label { Text = "Напечатайте текст. Последнее исправление можно отменить: Ctrl+Alt+Z.", ForeColor = Color.FromArgb(80, 93, 111), Font = new Font("Segoe UI", 9) }, 32);

        status.Dock = DockStyle.Fill;
        status.Padding = new Padding(20, 6, 16, 0);
        status.BackColor = Color.FromArgb(239, 243, 248);
        status.Text = "Готов к настройке";
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = status.BackColor };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(new Label { Text = "Ctrl+Alt+Z — отменить замену     ·     Ctrl+Alt+F8 — пауза", Dock = DockStyle.Fill,
            Padding = new Padding(20, 0, 16, 0), ForeColor = Color.FromArgb(93, 107, 125), Font = new Font("Segoe UI", 9) }, 0, 1);
        root.Controls.Add(footer, 0, 2);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть RichType", null, (_, _) => ShowWindow());
        pauseItem.Click += (_, _) => enabled.Checked = !enabled.Checked;
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => { exit = true; Close(); });
        tray.Icon = Icon;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.Visible = true;
        void SetRowVisible(int rowIndex, bool visible, int height = 36)
        {
            form.RowStyles[rowIndex].Height = visible ? height : 0;
            form.GetControlFromPosition(0, rowIndex)!.Visible = visible;
            form.GetControlFromPosition(1, rowIndex)!.Visible = visible;
        }
        void RefreshSettingsView()
        {
            SetRowVisible(backendRow, advanced.Checked);
            SetRowVisible(modelRow, advanced.Checked);
            SetRowVisible(addressRow, advanced.Checked && SelectedBackend != "Managed");
            SetRowVisible(timeoutRow, advanced.Checked);
            SetRowVisible(layoutRow, advanced.Checked);
            SetRowVisible(hyphensRow, advanced.Checked);
            SetRowVisible(appsRow, advanced.Checked);
            SetRowVisible(ignoredRow, advanced.Checked, 58);
            SetRowVisible(computeRow, SelectedBackend is "Managed" or "Ollama");
            SetRowVisible(threadsRow, cpuOnly.Checked && (SelectedBackend is "Managed" or "Ollama"));
            currentModel.Text = model.Text switch { "qwen3.5:2b" => "Qwen 3.5 · 2B", "qwen3.5:0.8b" => "Qwen 3.5 · 0.8B", _ => model.Text };
            RefreshCheckCaption();
        }
        backend.SelectedIndexChanged += (_, _) => RefreshSettingsView();
        advanced.CheckedChanged += (_, _) => RefreshSettingsView();
        model.TextChanged += (_, _) => RefreshSettingsView();
        compute.SelectedIndexChanged += (_, _) => cpuOnly.Checked = compute.SelectedIndex == 1;
        cpuOnly.CheckedChanged += (_, _) => { compute.SelectedIndex = cpuOnly.Checked ? 1 : 0; RefreshSettingsView(); };
        FillSettings();
        compute.SelectedIndex = cpuOnly.Checked ? 1 : 0;
        RefreshSettingsView();
        RefreshReadiness();
        readinessTimer.Tick += (_, _) => RefreshReadiness();
        readinessTimer.Start();
        UpdateTray();
        Load += (_, _) =>
        {
            try
            {
                controller = new CorrectionController(this, EffectiveSettings);
                controller.Status += message => { if (!IsDisposed) status.Text = message; };
                controller.PausedAfterInputFailure += () => enabled.Checked = false;
                status.Text = settings.Backend == "Managed" ? "Подготовка локальной модели" : settings.Enabled ? "Включён • ожидаю слово" : "На паузе";
                var undoOk = Native.RegisterHotKey(Handle, 1, 0x4003, 0x5A);
                var pauseOk = Native.RegisterHotKey(Handle, 2, 0x4003, 0x77);
                if (!undoOk || !pauseOk) status.Text = "Одна из горячих клавиш занята другой программой.";
            }
            catch (System.ComponentModel.Win32Exception e) { status.Text = e.Message; }
        };
        Shown += async (_, _) =>
        {
            if (startHidden) Hide();
            if (settings.Backend == "Managed")
            {
                if (managed.IsInstalled(settings.Model, settings.CpuOnly)) await RunManaged(false, false);
                else { status.Text = "Для первого запуска нажмите «Скачать и запустить»."; RefreshReadiness(); }
            }
        };
        FormClosing += (_, e) =>
        {
            if (!exit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            closing.Cancel();
            readinessTimer.Dispose();
            controller?.Dispose();
            managed.Dispose();
            Native.UnregisterHotKey(Handle, 1);
            Native.UnregisterHotKey(Handle, 2);
            tray.Visible = false;
            tray.Dispose();
        };
    }

    private void FillSettings()
    {
        loading = true;
        enabled.Checked = settings.Enabled;
        backend.SelectedIndex = settings.Backend == "Managed" ? 0 : settings.Backend == "Ollama" ? 1 : 2;
        endpoint.Text = settings.Endpoint;
        model.Text = settings.Model;
        threads.Value = settings.CpuThreads;
        cpuOnly.Checked = settings.CpuOnly;
        fixLayout.Checked = settings.FixKeyboardLayout;
        fixHyphens.Checked = settings.FixHyphens;
        delay.Value = settings.DelayMs;
        timeout.Value = settings.TimeoutSeconds;
        apps.Text = settings.AllowedApps;
        ignored.Text = settings.IgnoredWords;
        loading = false;
    }
    private string SelectedBackend => backend.SelectedIndex == 0 ? "Managed" : backend.SelectedIndex == 1 ? "Ollama" : "OpenAI";
    private Settings EffectiveSettings => settings.Backend != "Managed" ? settings : managed.IsRunning(settings)
        ? managed.ConnectionSettings(settings) : settings with { Backend = "OpenAI", Endpoint = "http://127.0.0.1:1", Enabled = false };
    private void ReconfigureController() => controller?.Configure(EffectiveSettings);
    private void RefreshReadiness()
    {
        var busy = setupCancellation != null;
        var ready = settings.Backend == "Managed" ? managed.IsRunning(settings) : externalReady;
        readiness.Text = busy ? ready ? "Проверка модели…" : "Подготовка модели…" : ready
            ? "Модель готова · " + (settings.Backend == "Managed" ? settings.CpuOnly ? "CPU" : "NVIDIA GPU" : "локальный сервер") +
                (settings.Enabled ? "" : " · автокоррекция на паузе")
            : settings.Backend == "Managed" ? "Модель не готова к работе" : "Подключение к модели не проверено";
        readiness.ForeColor = busy ? Color.FromArgb(137, 90, 17) : ready ? Color.FromArgb(24, 108, 75) : Color.FromArgb(89, 102, 121);
        readiness.BackColor = busy ? Color.FromArgb(255, 247, 224) : ready ? Color.FromArgb(232, 247, 239) : Color.FromArgb(239, 243, 248);
    }
    private void RefreshCheckCaption()
    {
        if (setupCancellation != null) return;
        var modelId = model.Text;
        var available = modelId is "qwen3.5:0.8b" or "qwen3.5:2b";
        check.Text = SelectedBackend == "Managed" && available && !managed.IsInstalled(modelId, cpuOnly.Checked)
            ? "Скачать и запустить" : SelectedBackend == "Managed" && !managed.IsRunning(settings with { Model = modelId, CpuOnly = cpuOnly.Checked })
                ? "Запустить" : "Проверить связь";
        if (SelectedBackend == "Managed" && available && !managed.IsInstalled(modelId, cpuOnly.Checked))
            testResult.Text = cpuOnly.Checked
                ? modelId == "qwen3.5:2b" ? "Первая загрузка: ≈1,30 ГБ. Затем можно работать без интернета." : "Первая загрузка: ≈0,83 ГБ. Затем можно работать без интернета."
                : "Для NVIDIA GPU: CUDA-движок ≈0,54 ГБ плюс выбранная модель. Повторная загрузка модели не нужна.";
    }
    private bool ApplySettings(bool startInstalled = true)
    {
        try
        {
            var updated = settings with { Endpoint = SelectedBackend == "Managed" ? "http://localhost:11434" : endpoint.Text.Trim().TrimEnd('/'), Model = model.Text.Trim(),
                Backend = SelectedBackend, CpuThreads = (int)threads.Value,
                CpuOnly = cpuOnly.Checked, FixKeyboardLayout = fixLayout.Checked, FixHyphens = fixHyphens.Checked,
                DelayMs = (int)delay.Value, TimeoutSeconds = (int)timeout.Value,
                AllowedApps = apps.Text, IgnoredWords = ignored.Text, Enabled = enabled.Checked };
            updated.Validate();
            if (updated.Backend != settings.Backend || updated.Endpoint != settings.Endpoint || updated.Model != settings.Model) externalReady = false;
            writeSettings(updated);
            settings = updated;
            if (settings.Backend != "Managed" || !managed.IsRunning(settings)) managed.Stop();
            ReconfigureController();
            UpdateTray();
            status.Text = "Настройки сохранены";
            RefreshCheckCaption();
            RefreshReadiness();
            if (startInstalled && settings.Backend == "Managed" && managed.IsInstalled(settings.Model, settings.CpuOnly) && !managed.IsRunning(settings))
                _ = RunManaged(false, false);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, e.Message, "Проверьте настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
    }
    private async Task CheckModel()
    {
        if (!ApplySettings(false)) return;
        if (settings.Backend == "Managed") { await RunManaged(true, true); return; }
        externalReady = false;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        setupCancellation = cancel;
        SetBusy(true);
        testResult.Text = "Загрузка и проверка модели…";
        try
        {
            // The test allows time for the first cold load. Normal typing uses the configured timeout.
            using var testClient = new ModelClient(settings with { TimeoutSeconds = 90 });
            var watch = Stopwatch.StartNew();
            var result = await testClient.CorrectAsync("првиет", cancel.Token);
            externalReady = true;
            var elapsed = watch.ElapsedMilliseconds;
            if (!IsDisposed) testResult.Text = result.Replacement == "привет"
                ? $"Модель отвечает: првиет → привет · {elapsed} мс (включая загрузку)."
                : $"Сервер отвечает, но тестовая опечатка не исправлена ({elapsed} мс). Попробуйте другую модель.";
        }
        catch (OperationCanceledException) { if (!IsDisposed) testResult.Text = cancel.IsCancellationRequested ? "Проверка отменена." : "Модель не ответила за 90 секунд."; }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        { if (!IsDisposed) testResult.Text = settings.Backend == "Ollama" ? "Нет ответа от Ollama. Можно выбрать автоматический локальный режим." : "Локальный движок не отвечает. Проверьте его адрес или выберите автоматический режим."; }
        finally { setupCancellation = null; if (!IsDisposed) SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        backend.Enabled = model.Enabled = compute.Enabled = threads.Enabled = save.Enabled = endpoint.Enabled = instructions.Enabled = !busy;
        check.Text = busy ? "Отменить" : "Проверить модель";
        if (!busy) RefreshCheckCaption();
        RefreshReadiness();
    }
    private async Task RunManaged(bool install, bool test)
    {
        if (setupCancellation != null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        setupCancellation = cancel;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (!IsDisposed && !closing.IsCancellationRequested)
                    testResult.Text = text.StartsWith("Локальная модель готова", StringComparison.Ordinal)
                        ? "Работает локально на вашем компьютере." : text;
            });
            await managed.PrepareAndStartAsync(settings, install, progress, cancel.Token);
            ReconfigureController();
            status.Text = settings.Enabled ? "Можно печатать. Здесь появятся сообщения об исправлениях." : "Автокоррекция приостановлена.";
            if (test)
            {
                using var testClient = new ModelClient(EffectiveSettings with { TimeoutSeconds = 90 });
                var watch = Stopwatch.StartNew();
                var result = await testClient.CorrectAsync("прывет", cancel.Token);
                testResult.Text = result.Replacement == "привет" ? $"Готово: прывет → привет • {watch.ElapsedMilliseconds} мс • работает локально"
                    : "Модель работает, но тестовая опечатка пропущена. Попробуйте модель 2B.";
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) testResult.Text = "Подготовка отменена. Скачивание можно продолжить той же кнопкой."; }
        catch (Exception e) when (e is System.IO.IOException or System.Net.Http.HttpRequestException or InvalidOperationException or
            UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { if (!IsDisposed) { testResult.Text = "Не удалось подготовить модель: " + e.Message; status.Text = "Модель не готова. Повторите подготовку."; } }
        finally { setupCancellation = null; if (!IsDisposed) SetBusy(false); }
    }
    private bool TrySaveSettings(out string? error)
    {
        try { writeSettings(settings); error = null; return true; }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        { error = "Изменения применены, но настройки не сохранены: " + e.Message; return false; }
    }
    private void UpdateTray()
    {
        tray.Text = settings.Enabled ? "RichType • автокоррекция включена" : "RichType • пауза";
        pauseItem.Text = settings.Enabled ? "Поставить на паузу" : "Включить автокоррекцию";
    }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312)
        {
            if (message.WParam == 1) controller?.Undo();
            if (message.WParam == 2) enabled.Checked = !enabled.Checked;
        }
        base.WndProc(ref message);
    }
}
