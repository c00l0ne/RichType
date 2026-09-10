using Kazrich.Core;

internal static class QualityCases
{
    internal sealed record Case(string Group, string Input, string Expected, string[]? AcceptedAlternatives = null);

    internal static IEnumerable<Case> All()
    {
        foreach (var (input, expected) in new[] {
            ("cnegjh e yjdbxrjd", "ступор у новичков"),
            ("Cnegjh e yjdbxrjd", "Ступор у новичков"),
            ("cnegjh e yjdbxrjd.", "ступор у новичков."),
            ("ступор e новичков", "ступор у новичков"),
            ("cnegjh  e  yjdbxrjd", "ступор  у  новичков"),
            ("ступор у новичков", "ступор у новичков"),
            ("[jns ,s gj", "хотя бы по"),
            ("{jns ,s gj", "Хотя бы по"),
            ("[jns  ,s  gj", "хотя  бы  по"),
            ("[jns ,s gj dhtvtyb", "хотя бы по времени"),
            ("[jnz ,s gj", "хотя бы по"),
            ("хотя бы по", "хотя бы по"), ("хоты бы по", "хоты бы по"),
            ("[jns", "хоты"), ("хоты", "хоты"),
            (",s", "бы"), (";t", "же"), ("<s", "Бы"), (":t", "Же"),
            (",s,", "бы,"), (";t.", "же."), (",x", ",x"), (",S", ",S"),
            ("hello, world", "hello, world"), ("e", "e"), ("E", "E"),
            ("level e is available", "level e is available"),
            ("хоты_бы", "хоты_бы"), ("https://example.com/[jns", "https://example.com/[jns"),
            ("[jns@example.com", "[jns@example.com"),
            ("дела идут хорошо", "дела идут хорошо"),
            (KeyboardLayout.Convert("дела идут хорошо"), "дела идут хорошо"),
            (KeyboardLayout.Convert("я делаю работу"), "я делаю работу"),
            (KeyboardLayout.Convert("у него три дела"), "у него три дела"),
            (KeyboardLayout.Convert("мой кот на диване"), "мой кот на диване"),
            (KeyboardLayout.Convert("это тёплое лето"), "это тёплое лето")
        }) yield return new("reported-short-words", input, expected);
        foreach (var (input, expected) in new[] {
            ("Pyftnt kb ds, xnj nfrjt", "Знаете ли вы, что такое"),
            ("pyftnt kb ds, xnj nfrjt", "знаете ли вы, что такое"),
            ("Pyftnt kb ds", "Знаете ли вы"),
            ("Знаете kb вы, что такое", "Знаете ли вы, что такое"),
            ("Pyftnt kb ds, xnj nfrjt bkk.pbz", "Знаете ли вы, что такое иллюзия"),
            ("Можете kb вы", "Можете ли вы"),
            ("Vj;tnt kb ds", "Можете ли вы"),
            ("kb", "kb"), ("KB", "KB"), ("64 kb", "64 kb"), ("hello kb world", "hello kb world"),
            ("размер файла 64 kb", "размер файла 64 kb"),
            ("Знаете ли вы, что такое иллюзия", "Знаете ли вы, что такое иллюзия"),
            ("Bkk.pbz", "Иллюзия"), ("bkk.pbz", "иллюзия"),
            ("Bp-pf xhtpdsxfqyj", "Из-за чрезвычайно"),
            ("Bp-pf", "Из-за"), ("bp-pf", "из-за"), ("bp-pf.", "из-за."),
            ("bp-pf,", "из-за,"), ("bp-pf!", "из-за!"), ("(Bp-pf)", "(Из-за)"),
            ("из-pf", "из-за"), ("bp-за", "из-за"),
            ("well-known", "well-known"), ("self-contained", "self-contained"),
            ("hello-world", "hello-world"), ("so-so", "so-so"), ("to-do", "to-do"),
            ("x-y", "x-y"), ("BP-PF", "BP-PF"), ("Bp-Pf", "Bp-Pf"),
            ("bp-pf@example.com", "bp-pf@example.com"), ("https://example.com/bp-pf", "https://example.com/bp-pf"),
            ("user_bp-pf", "user_bp-pf"), ("bp-pf.com", "bp-pf.com"),
            ("C:\\temp\\bp-pf", "C:\\temp\\bp-pf"), ("bp-pf123", "bp-pf123")
        }) yield return new("reported-compounds", input, expected);
        foreach (var word in new[] { "из-под", "по-моему", "по-русски", "кое-как", "кто-то", "что-нибудь",
            "чего-нибудь", "по-прежнему", "по-настоящему", "научно-технический", "северо-запад", "кое-кто", "бизнес-план" })
        {
            yield return new("reported-compounds", word, word);
            yield return new("reported-compounds", KeyboardLayout.Convert(word), word);
        }
        foreach (var (input, expected) in new[] {
            ("ghbdtn helllo", "привет hello"), ("ghbdtn  helllo ", "привет  hello "),
            ("привет helllo", "привет hello"), ("helllo привет", "hello привет"),
            ("првиет hello", "привет hello"), ("hello првиет", "hello привет"),
            ("my favourite coluor", "my favourite colour"), ("my favorite coluor", "my favorite color"),
            ("ghbdtn recieve", "привет receive"), ("recieve привет", "receive привет"),
            ("ghbdtn hello", "привет hello"), ("hello ghbdtn", "hello привет"),
            ("hello world", "hello world"), ("привет мир", "привет мир"),
            ("ghbdtn  hello", "привет  hello"), ("hello  првиет", "hello  привет"),
            ("colour  ghbdtn", "colour  привет"), ("hello  colour", "hello  colour"),
            ("ghbdtn\thelllo", "привет\thello"), ("ghbdtn\nhelllo", "привет\nhello"),
            (KeyboardLayout.Convert("Натупило теплое лето"), "Наступило теплое лето"),
            (KeyboardLayout.Convert("прывет мир"), "привет мир")
        }) yield return new("phrase-language", input, expected);
        // With no dialect cue, coluor can be a transposition in British colour
        // or one inserted letter in American color. Both require one edit.
        yield return new("phrase-language", "ghbdtn coluor", "привет colour", ["привет color"]);
        yield return new("phrase-language", "coluor ghbdtn", "colour привет", ["color привет"]);
        var russian = new[] {
            "сегодня", "завтра", "вчера", "пожалуйста", "спасибо", "здравствуйте", "работает", "работают",
            "программа", "программы", "проверка", "проверки", "исправление", "исправления", "настройки",
            "клавиатура", "раскладка", "полностью", "доводим", "бюджета", "клубком", "прикольно",
            "замены", "дела", "теплое", "тёплое", "еще", "ещё", "все", "всё", "начинается",
            "пойдем", "пойдём", "прибыли", "объявление", "объект", "жизнь", "хочу", "буду", "юбка",
            "съезд", "подъезд", "счастье", "деревья", "семья", "молодец", "здорово", "быстро",
            "медленно", "удобно", "сложно", "просто", "обычно", "всегда", "иногда", "никогда",
            "сейчас", "потом", "раньше", "позже", "кошка", "собака", "книга", "машина", "окно",
            "алгоритм", "интерфейс", "процессор", "видеокарта", "модель", "нейросеть", "компьютер",
            "класс", "касса", "ссора", "рассказ", "суббота", "аллея", "профессия", "территория",
            "сессия", "комиссия", "коллекция", "аппарат", "прогресс", "эффект", "аккуратно",
            "сорян", "кринж", "лол", "имхо", "стрим", "стримить", "гуглить", "дефолт"
        };
        var english = new[] {
            "hello", "world", "today", "tomorrow", "yesterday", "please", "thanks", "sorry", "welcome",
            "computer", "keyboard", "language", "program", "software", "hardware", "settings", "window",
            "button", "message", "server", "client", "model", "network", "memory", "thread", "process",
            "function", "variable", "constant", "return", "while", "foreach", "public", "private",
            "coffee", "letter", "book", "success", "address", "different", "available", "necessary",
            "really", "usually", "quickly", "correct", "spelling", "typing", "test", "text", "python",
            "rhythm", "myths", "go", "we", "you", "they", "this", "that", "these", "those"
        };
        foreach (var word in russian.Concat(english)) yield return new("correct", word, word);
        foreach (var text in new[] {
            "Настройки программы сохранены.", "Спасибо, все работает!", "Сегодня хорошая погода.",
            "Как дела? Всё хорошо.", "Просто проверяю скорость замены.", "Мне нравится новый интерфейс.",
            "сорян, это кринж лол", "Please leave this sentence unchanged.", "Hello, world!", "We go.",
            "Это test для mixed language.", "user@example.com", "https://example.com/a?text=ghbdtn",
            "C:\\Users\\test\\file.txt", "./src/app.cs", "user_name", "HelloWorld", "XMLHttpRequest",
            "GPT-5", "NL100", "192.168.1.1", "2026-09-10", "1.2.3", "x = y + z;", "don't", "can't",
            "как-то", "когда-нибудь", "что-либо", "из-за", "из-под", "по-русски", "AI GPU CPU DNS HTML"
        }) yield return new("protected", text, text);
        foreach (var word in russian.Where(word => word.Length >= 3 && !new[] { "сорян", "кринж", "лол", "имхо" }.Contains(word)))
            // "tot" is itself an English word; the context suite separately
            // checks its Russian reading when a Russian neighbour appears.
            yield return word == "еще" ? new("ambiguity", "tot", "tot") : new("layout", KeyboardLayout.Convert(word), word);
        foreach (var word in new[] { "полностью", "знаю", "долю", "молю", "клуб", "голубь", "бюджет", "хочу", "жизнь", "привет", "дела", "замены", "спасибо", "сегодня", "вы", "мы", "ты", "он", "она", "они" })
            foreach (var punctuation in new[] { ",", ".", "!", "?", ";", "...", "?!" })
                yield return new("punctuation", KeyboardLayout.Convert(word) + punctuation, word + punctuation);
        foreach (var word in new[] { "полностью", "прикольно", "начинается", "объявление", "клавиатура", "исправление", "программа", "бюджета", "хочу", "жизнь", "буду", "юбка" })
            for (var split = 1; split < word.Length; split++)
            {
                var mixed = word[..split] + KeyboardLayout.Convert(word[split..]);
                // A Cyrillic-only token with a final dot contains no evidence
                // that the dot was a letter. Preserve it as punctuation.
                yield return mixed == "полность." ? new("ambiguity", mixed, "полностью.") : new("mixed", mixed, word);
                yield return new("mixed", KeyboardLayout.Convert(word[..split]) + word[split..], word);
            }
        foreach (var (input, expected) in new[] {
            ("доволдим", "доводим"), ("полностьюю", "полностью"), ("севодня", "сегодня"),
            ("правельный", "правильный"), ("ошыбка", "ошибка"), ("пожалуста", "пожалуйста"),
            ("здраствуйте", "здравствуйте"), ("интиресно", "интересно"), ("потмоу", "потому"),
            ("спосибо", "спасибо"), ("праметр", "параметр"), ("исправленеи", "исправление"),
            ("клавитура", "клавиатура"), ("интерестно", "интересно"), ("Насступило", "Наступило"),
            ("прикольноо", "прикольно"), ("првиет", "привет"), ("прывет", "привет"),
            ("helllo", "hello"), ("helloo", "hello"), ("thnaks", "thanks"), ("recieve", "receive"),
            ("teh", "the"), ("adress", "address"), ("seperate", "separate"), ("definately", "definitely"),
            ("tommorow", "tomorrow"), ("becuase", "because"), ("wierd", "weird"), ("langauge", "language"),
            ("occured", "occurred"), ("untill", "until"), ("realyl", "really"), ("writting", "writing")
        })
        {
            yield return new("typo", input, expected);
            if (!input.Any(char.IsAsciiLetter))
            {
                var mapped = KeyboardLayout.Convert(input);
                // The same keys spell a complete layout word plus a period.
                // Retaining that period avoids deleting an intentional stop.
                yield return mapped == "gjkyjcnm.." ? new("ambiguity", mapped, "полностью.") : new("layout-typo", mapped, expected);
            }
        }
        // These inputs are physical Shift-key results, written independently
        // of KeyboardLayout.Convert so a broken mapping cannot hide in the corpus.
        foreach (var (keyed, proper) in new[] {
            (":bpym", "Жизнь"), ("{jxe", "Хочу"), ("\"nj", "Это"), ("<elen", "Будут"),
            (">,rf", "Юбка"), ("~krf", "Ёлка"), (":fhrj", "Жарко"), ("{jhjij", "Хорошо"),
            ("<.l;tn", "Бюджет"), (">yjcnm", "Юность"), ("{jkjlyj", "Холодно"), ("<scnhj", "Быстро") })
        {
            yield return new("case", proper, proper);
            foreach (var ending in new[] { "", ".", "!" }) yield return new("case", keyed + ending, proper + ending);
            for (var split = 1; split < proper.Length; split++)
            {
                yield return new("case", proper[..split] + keyed[split..], proper);
                var mixed = keyed[..split] + proper[split..];
                // An opening quote followed by the correctly spelled word "то"
                // is indistinguishable from a single wrong-layout Э. Retain the
                // quote when the model cannot confirm either interpretation.
                yield return mixed == "\"то" ? new("ambiguity", mixed, mixed) : new("case", mixed, proper);
            }
        }
        foreach (var preserved in new[] { "HELLO", "ПРИВЕТ", "GHBDTN", "JSON", "XML", "<body>", "</body>",
            "{hello}", "\"hello\"", "\"привет\"", "'hello'", "a < b", "x > y", "HelloWorld", "YouTube", "JavaScript" })
            yield return new("case", preserved, preserved);
        foreach (var (opening, closing) in new[] { ("\"", "\""), ("'", "'"), ("[", "]"), ("{", "}"),
            ("(", ")"), ("«", "»"), ("“", "”"), ("‘", "’") })
            foreach (var (source, corrected) in new[] { ("hello", "hello"), ("привет", "привет"),
                ("helllo", "hello"), ("првиет", "привет"), ("ghbdtn", "привет"), ("пойдеv", "пойдем") })
            {
                yield return new("wrappers", opening + source, opening + corrected);
                yield return new("wrappers", source + closing, corrected + closing);
                yield return new("wrappers", opening + source + closing, opening + corrected + closing);
            }
        foreach (var (source, corrected) in new[] {
            ("say \"helllo", "say \"hello"), ("это [првиет", "это [привет"),
            ("\"'helllo", "\"'hello"), ("ghbdtn'.", "привет'."),
            ("don't", "don't"), ("don't!", "don't!"), ("can't", "can't"), ("we're", "we're"),
            ("C:\\hello\\file.txt", "C:\\hello\\file.txt"), ("<body>", "<body>"),
            ("'[jxe", "'хочу"), ("\":bpym", "\"Жизнь"), ("(,.l;tnf", "(бюджета") })
            yield return new("wrappers", source, corrected);
        // A second vocabulary was chosen after the first corpus was passing.
        // Errors are generated mechanically rather than copied from fixes.
        var freshRussian = new[] { "велосипед", "мороженое", "космонавт", "путешествие", "согласование",
            "разрешение", "подключение", "оповещение", "собеседник", "приложение", "количество", "человек",
            "необходимо", "качество", "занятие", "мышление", "предложение", "внимание", "исключение",
            "информация", "ответственный", "безопасность", "участник", "английский", "русский", "программист",
            "воскресенье", "жираф", "хирург", "ёлочка" };
        var freshEnglish = new[] { "beautiful", "important", "example", "question", "answer", "information",
            "application", "connection", "document", "language", "conversation", "education", "experience",
            "development", "understand", "remember", "tomorrow", "through", "enough", "thought" };
        foreach (var word in freshRussian.Concat(freshEnglish))
        {
            yield return new("expanded-correct", word, word);
            foreach (var at in new[] { 0, word.Length / 2, word.Length - 1 }.Distinct())
            {
                var repeated = word.Insert(at, word[at].ToString());
                // A generator knows its source word, but a reader cannot infer
                // that history. Both through (deletion) and thorough (transpose)
                // are one edit from this isolated input. Context cases below
                // require the appropriate distinct reading instead.
                yield return repeated == "throough" ? new("ambiguity", repeated, word, ["thorough"]) :
                    new("expanded-repeat", repeated, word);
            }
            var middle = word.Length / 2;
            if (word[middle - 1] != word[middle])
                yield return new("expanded-transpose", word[..(middle - 1)] + word[middle] + word[middle - 1] + word[(middle + 1)..], word);
        }
        foreach (var word in freshRussian)
        {
            var keyed = KeyboardLayout.Convert(word);
            yield return new("expanded-layout", keyed, word);
            foreach (var split in new[] { 1, word.Length / 2, word.Length - 1 }.Distinct())
            {
                yield return new("expanded-mixed", word[..split] + keyed[split..], word);
                yield return new("expanded-mixed", keyed[..split] + word[split..], word);
            }
        }
        foreach (var (input, expected) in new[] {
            ("walk throough the door", "walk through the door"), ("a throough check", "a thorough check"),
            ("this is a throough check", "this is a thorough check"), ("please go throough this", "please go through this"),
            ("a seveer warning", "a severe warning"), ("please seveer the connection", "please sever the connection"),
            ("through", "through"), ("thorough", "thorough"), ("severe", "severe"), ("sever", "sever") })
            yield return new("spelling-context", input, expected);
        foreach (var word in new[] { "colour", "color", "colours", "colors", "favourite", "favorite",
            "favour", "favor", "favourable", "favorable", "centre", "center", "centres", "centers",
            "theatre", "theater", "metre", "meter", "litre", "liter", "travelling", "traveling",
            "travelled", "traveled", "cancelled", "canceled", "jewellery", "jewelry", "grey", "gray",
            "organise", "organize", "organised", "organized", "organisation", "organization",
            "realise", "realize", "realised", "realized", "analyse", "analyze", "analysed", "analyzed",
            "defence", "defense", "licence", "license", "practise", "practice", "programme", "program",
            "neighbour", "neighbor", "neighbours", "neighbors", "rumour", "rumor", "honour", "honor",
            "behaviour", "behavior", "maths", "math" })
            yield return new("regional-correct", word, word);
        foreach (var (input, expected) in new[] { ("coluor", "colour"), ("faovurite", "favourite"),
            ("cenrte", "centre"), ("thetare", "theatre"), ("orgnaise", "organise"),
            ("realsie", "realise"), ("neihgbour", "neighbour"), ("behavoiur", "behaviour") })
            yield return new("regional-typo", input, expected);
        foreach (var word in new[] { "Aalto", "Pranav", "Kartik", "Nadya", "Nikolai", "Yaroslav",
            "Dmitriy", "Artyom", "Alina", "Kira", "Aarav", "OpenAI", "smol", "bruh", "сорян", "кринж", "лол", "имхо" })
            yield return new("regional-preserve", word, word);
        // A short brand with an intentional doubled letter can have exactly
        // the same spelling as an ordinary repeated-letter typo. The input
        // alone does not establish intent; explicit exclusions are tested in Core.
        yield return new("ambiguity", "Carrd", "Carrd", ["Card"]);
        foreach (var (input, expected) in new[] {
            ("я ltkf. работу", "я делаю работу"), ("как ltkf.", "как дела."),
            ("это моя ;bpym.", "это моя жизнь."), ("я наслаждаюсь ;bpym.", "я наслаждаюсь жизнью"),
            ("я наслаждаюсь ;bpym..", "я наслаждаюсь жизнью."), ("я vjk. о помощи", "я молю о помощи"),
            ("я отдаю свою ljk.", "я отдаю свою долю"), ("я восхищаюсь >yjcnm.", "я восхищаюсь Юностью"),
            ("это >yjcnm.", "это Юность."), ("я дела. работу", "я дела. работу"),
            ("я делаю работу", "я делаю работу"), ("это моя жизнь.", "это моя жизнь.") })
            yield return new("terminal-context", input, expected);
    }
}
