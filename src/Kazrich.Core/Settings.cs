using System.Text.Json;

namespace Kazrich.Core;

public sealed record Settings
{
    public string Endpoint { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen3.5:2b";
    public string Backend { get; init; } = "Managed";
    public int CpuThreads { get; init; } = 2;
    public bool CpuOnly { get; init; } = true;
    public bool FixKeyboardLayout { get; init; } = true;
    public bool FixHyphens { get; init; } = true;
    internal const string PreviousHyphenInstructions = "Исправь только дефисное написание в сочетании русских слов. Если между словами нужен дефис, замени пробел на дефис. Иначе верни исходный текст. Не меняй буквы, порядок слов или пунктуацию. Верни только результат.";
    public const string DefaultHyphenInstructions = "Исправь орфографию: слитное, раздельное и дефисное написание. Не меняй буквы и порядок слов. Верни только исправленный текст.";
    public string HyphenInstructions { get; init; } = DefaultHyphenInstructions;
    internal const string PreviousSpellingInstructions = "Исправь опечатки в русском или английском слове. Верни только исправленное слово, без пояснений. Если слово написано правильно, не меняй его. Не переводи.";
    public const string DefaultSpellingInstructions = "Исправь только опечатки в русском или английском слове или тексте. Сохраняй часть речи, число, род, падеж и время. Правильно написанные слова оставляй без изменений. Не переводи и не перефразируй. Верни только результат.";
    public const string DefaultEnglishSpellingInstructions = DefaultSpellingInstructions + " Accept both British and American spelling. Preserve the original regional spelling.";
    public const string DefaultLayoutInstructions = "Which of these two strings is a real Russian or English word? Return only that word.";
    public string SpellingInstructions { get; init; } = DefaultSpellingInstructions;
    public string EnglishSpellingInstructions { get; init; } = DefaultEnglishSpellingInstructions;
    public const string DefaultSpellingChoiceInstructions = "В первой строке исходное слово, ниже варианты исправления. Выбери верное написание с минимальным изменением исходного слова. Сохраняй часть речи, число, род, падеж и время. Разговорные слова оставляй без изменений. Верни только выбранную строку.";
    public string SpellingChoiceInstructions { get; init; } = DefaultSpellingChoiceInstructions;
    public string LayoutInstructions { get; init; } = DefaultLayoutInstructions;
    internal const string PreviousWordValidityInstructions = "Существует ли такое обычное русское или английское слово? Ответь только ДА или НЕТ. Не считай случайный набор букв именем или сокращением.";
    public const string DefaultWordValidityInstructions = "Is this a correctly spelled Russian or English word? Include inflected forms, pronouns, articles and other function words. Do not count random letters as a name or abbreviation. Reply only ДА or НЕТ.";
    public string WordValidityInstructions { get; init; } = DefaultWordValidityInstructions;
    internal const string PreviousPhraseLayoutInstructions = "Выбери осмысленный текст из двух вариантов. Скопируй выбранный вариант точно, символ в символ. Верни только выбранный вариант.";
    public const string DefaultPhraseLayoutInstructions = PreviousPhraseLayoutInstructions + " В одном варианте отдельное слово могло быть набрано в неверной раскладке клавиатуры. Учитывай язык соседних слов и грамматическую связность всей фразы.";
    public string PhraseLayoutInstructions { get; init; } = DefaultPhraseLayoutInstructions;
    public const string DefaultContextLayoutInstructions = "Даны две строки. Одна содержит слово в неверной раскладке клавиатуры. Выбери строку с осмысленным сочетанием слов. Обычные сокращения русских слов допустимы. Ответ должен полностью совпадать с одной из строк. Не объединяй строки. Не добавляй пояснения.";
    public string ContextLayoutInstructions { get; init; } = DefaultContextLayoutInstructions;
    public int DelayMs { get; init; } = 250;
    public int TimeoutSeconds { get; init; } = 8;
    public bool Enabled { get; init; } = true;
    public string AllowedApps { get; init; } = "notepad, chrome, msedge, firefox, telegram";
    public string IgnoredWords { get; init; } = "казрич, казричу, kazrich, richtype, бет, рейз, колл, фолд, префлоп, постфлоп, пуш, стек, рег, фиш, покер, оллин";

    public void Validate()
    {
        if (AllowedApps == null || IgnoredWords == null)
            throw new ArgumentException("Списки приложений и исключений не должны иметь значение null.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("Адрес должен вести на локальный сервер, например http://localhost:11434, без пути и пароля.");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 160)
            throw new ArgumentException("Укажите имя модели (до 160 символов).");
        if (Backend is not ("Managed" or "Ollama" or "OpenAI")) throw new ArgumentException("Неизвестный тип сервера.");
        if (Backend == "Managed" && Model is not ("qwen3.5:0.8b" or "qwen3.5:2b"))
            throw new ArgumentException("Для автоматического запуска выберите Qwen 0.8B или 2B.");
        if (string.IsNullOrWhiteSpace(SpellingInstructions) || string.IsNullOrWhiteSpace(EnglishSpellingInstructions) || string.IsNullOrWhiteSpace(LayoutInstructions) ||
            SpellingInstructions.Length > 8000 || EnglishSpellingInstructions.Length > 8000 || LayoutInstructions.Length > 8000 ||
            string.IsNullOrWhiteSpace(PhraseLayoutInstructions) || PhraseLayoutInstructions.Length > 8000 ||
            string.IsNullOrWhiteSpace(ContextLayoutInstructions) || ContextLayoutInstructions.Length > 8000 ||
            string.IsNullOrWhiteSpace(HyphenInstructions) || HyphenInstructions.Length > 8000 ||
            string.IsNullOrWhiteSpace(WordValidityInstructions) || WordValidityInstructions.Length > 8000)
            throw new ArgumentException("Инструкции ИИ не должны быть пустыми или длиннее 8000 символов каждая.");
        if (string.IsNullOrWhiteSpace(SpellingChoiceInstructions) || SpellingChoiceInstructions.Length > 8000)
            throw new ArgumentException("Инструкция выбора написания не должна быть пустой или длиннее 8000 символов.");
        if (CpuThreads is < 1 or > 64 || DelayMs is < 0 or > 2000 || TimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("Проверьте потоки CPU, паузу и время ожидания.");
    }

    public HashSet<string> WordSet() => Split(IgnoredWords);
    public HashSet<string> AppSet() => Split(AllowedApps);
    public int RequiredContextTokens()
    {
        // A token cannot contain less than one UTF-8 byte. Reserve space for
        // the bounded word comparisons and answer, then use a server-friendly
        // power of two. Long editable instructions must not be silently cut.
        var instructionBytes = new[] { SpellingInstructions, EnglishSpellingInstructions, LayoutInstructions, PhraseLayoutInstructions,
            ContextLayoutInstructions, HyphenInstructions, WordValidityInstructions, SpellingChoiceInstructions }
            .Max(System.Text.Encoding.UTF8.GetByteCount);
        return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1024, instructionBytes + 512));
    }
    private static HashSet<string> Split(string text) => new(text.Split([',', ';', '\r', '\n'],
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
}

public static class SettingsFile
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kazrich");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();
        return Parse(File.ReadAllText(FilePath));
    }
    public static Settings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement.Deserialize<Settings>() ?? new Settings();
        if (settings.SpellingInstructions == Settings.PreviousSpellingInstructions)
            settings = settings with { SpellingInstructions = Settings.DefaultSpellingInstructions };
        if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.TryGetProperty(nameof(Settings.EnglishSpellingInstructions), out _))
            settings = settings with { EnglishSpellingInstructions = settings.SpellingInstructions == Settings.DefaultSpellingInstructions
                ? Settings.DefaultEnglishSpellingInstructions : settings.SpellingInstructions };
        if (settings.PhraseLayoutInstructions == Settings.PreviousPhraseLayoutInstructions)
            settings = settings with { PhraseLayoutInstructions = Settings.DefaultPhraseLayoutInstructions };
        if (settings.HyphenInstructions == Settings.PreviousHyphenInstructions)
            settings = settings with { HyphenInstructions = Settings.DefaultHyphenInstructions };
        if (settings.WordValidityInstructions == Settings.PreviousWordValidityInstructions)
            settings = settings with { WordValidityInstructions = Settings.DefaultWordValidityInstructions };
        settings.Validate();
        return settings;
    }
    public static void Save(Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
