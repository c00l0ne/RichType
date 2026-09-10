using System.Text.RegularExpressions;

namespace Kazrich.Core;

public static class Hyphenation
{
    private static bool Russian(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    // Only one already-verified preceding word can participate in a boundary correction.
    public static string ContextSuffix(string context) =>
        Regex.Match(context, @"(?<!\S)[А-ЯЁа-яё]{1,32} ?$").Value;

    internal static IEnumerable<Match> Words(string text, HashSet<string> ignored) =>
        Regex.Matches(text, @"(?<!\S)[А-ЯЁа-яё]{3,32}(?=$|[\s,.!?;:])").Cast<Match>()
            .Where(m => !ignored.Contains(m.Value) && !m.Value.Skip(1).Any(char.IsUpper));

    public static HashSet<int> Insertions(string text, HashSet<string> ignored) =>
        Words(text, ignored).SelectMany(m => Enumerable.Range(m.Index + 1, m.Length - 1))
            .Where(i => text[i] is not ('ь' or 'ъ' or 'Ь' or 'Ъ')).ToHashSet();

    public static HashSet<int> Boundaries(string text, HashSet<string> ignored)
    {
        var result = new HashSet<int>();
        for (var i = 1; i + 1 < text.Length; i++)
        {
            if (text[i] != ' ' || !Russian(text[i - 1]) || !Russian(text[i + 1])) continue;
            var start = i - 1;
            var end = i + 2;
            while (start > 0 && Russian(text[start - 1])) start--;
            while (end < text.Length && Russian(text[end])) end++;
            if (start > 0 && !char.IsWhiteSpace(text[start - 1]) ||
                end < text.Length && !char.IsWhiteSpace(text[end]) && !",.!?;:".Contains(text[end])) continue;
            var left = text[start..i];
            var right = text[(i + 1)..end];
            if (right[0] is 'ь' or 'ъ' or 'Ь' or 'Ъ') continue;
            if (left.Length > 32 || right.Length is < 2 or > 32 || ignored.Contains(left) || ignored.Contains(right) ||
                left.All(char.IsUpper) || right.All(char.IsUpper)) continue;
            result.Add(i);
        }
        return result;
    }

    internal static (int Start, string Text) PairAt(string text, int boundary)
    {
        var start = boundary - 1;
        var end = boundary + 1;
        while (start > 0 && Russian(text[start - 1])) start--;
        while (end < text.Length && Russian(text[end])) end++;
        return (start, text[start..end]);
    }

    public static string Validate(string original, string? proposed, HashSet<string> ignored, bool allowInsertions = true)
    {
        if (proposed == null || proposed.Length < original.Length) return original;
        var allowed = Boundaries(original, ignored);
        var insertions = allowInsertions ? Insertions(original, ignored) : new HashSet<int>();
        var i = 0;
        var j = 0;
        while (i < original.Length && j < proposed.Length)
        {
            if (proposed[j] == original[i] || proposed[j] == '-' && allowed.Contains(i)) { i++; j++; }
            else if (proposed[j] == '-' && insertions.Remove(i)) j++;
            else return original;
        }
        return i == original.Length && j == proposed.Length ? proposed : original;
    }
}
