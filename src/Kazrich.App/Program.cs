using System;
using System.Threading;
using System.Windows.Forms;
using Kazrich.Core;

namespace Kazrich.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(true, "Local\\Kazrich.Autocorrect", out var first);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!first)
        {
            MessageBox.Show("RichType уже работает. Откройте его значок рядом с часами.", "RichType");
            return;
        }
        Settings settings;
        try { settings = SettingsFile.Load(); }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show("Не удалось прочитать настройки. Загружены значения по умолчанию.\n" + e.Message, "RichType");
            settings = new Settings();
        }
        Application.Run(new MainForm(settings, Array.Exists(args, a => a == "--tray")));
    }
}
