namespace Kazrich.Core;

public static class CorrectionRules
{
    public static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
        or >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
    public static bool IsRussianLetter(char c) => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';

    public static bool IsCandidate(string word, ISet<string> ignored) =>
        word.Length is >= 3 and <= 32 && word.All(IsLetter) && !ignored.Contains(word) &&
        !word.All(char.IsUpper) && !word.Skip(1).Any(char.IsUpper) &&
        word.All(c => IsRussianLetter(c) == IsRussianLetter(word[0]));

    public static bool IsMixedCandidate(string word, ISet<string> ignored) =>
        word.Length is >= 2 and <= 32 && IsLetter(word[^1]) &&
        word.All(c => IsLetter(c) || KeyboardLayout.IsLatinLetterKey(c)) && !ignored.Contains(word) &&
        !word.All(char.IsUpper) && !word.Skip(1).Any(char.IsUpper) &&
        word.Any(KeyboardLayout.IsLatinLetterKey) && word.Any(IsRussianLetter);

    public static string? Validate(string original, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = raw.Trim();
        if (result.Length is < 2 or > 32 || !result.All(IsLetter)) return null;
        if (!result.All(c => IsRussianLetter(c) == IsRussianLetter(original[0]))) return null;
        if (result.Equals(original, StringComparison.OrdinalIgnoreCase)) return original;
        // Limit gratuitous inflection changes, e.g. thnaks -> thank instead of thanks.
        var limit = original.Length > 10 ? 2 : 1;
        if (DamerauDistance(original.ToLowerInvariant(), result.ToLowerInvariant()) > limit) return null;
        result = result.ToLowerInvariant();
        return char.IsUpper(original[0]) ? char.ToUpperInvariant(result[0]) + result[1..] : result;
    }

    public static int DamerauDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}

public sealed class WordTracker
{
    private string token = "";
    private bool blocked;
    public void Reset() { token = ""; blocked = false; }
    public void Backspace()
    {
        if (token.Length > 0) token = token[..^1];
        else blocked = true; // Editing unknown text: wait for the next boundary.
    }
    public string? Feed(char c)
    {
        if (c == ' ' || ",.!?;:".Contains(c))
        {
            var result = blocked ? null : token;
            Reset();
            return result;
        }
        if (!CorrectionRules.IsLetter(c) || token.Length >= 32) blocked = true;
        if (!blocked) token += c;
        return null;
    }
}
