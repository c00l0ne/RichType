namespace Kazrich.Core;

public static class KeyboardLayout
{
    private const string Latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Russian = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string LatinShifted = "QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>~";
    private const string RussianShifted = "ЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮЁ";
    public static bool IsLatinLetterKey(char c) => Latin.Contains(c) || LatinShifted.Contains(c);
    public static string Normalize(string word, bool toLatin) => new(word.Select(c =>
        (toLatin ? Russian.Contains(char.ToLowerInvariant(c)) : IsLatinLetterKey(c)) ? ConvertCharacter(c) : c).ToArray());
    public static string Convert(string word) => new(word.Select(ConvertCharacter).ToArray());

    public static IEnumerable<string> ConvertedWords(string original, string replacement, ISet<string> ignored)
    {
        var source = System.Text.RegularExpressions.Regex.Split(original.Trim(), @"\s+");
        var corrected = System.Text.RegularExpressions.Regex.Split(replacement.Trim(), @"\s+");
        if (source.Length != corrected.Length) yield break;
        for (var index = 0; index < source.Length; index++)
        {
            var raw = source[index];
            var word = corrected[index];
            if (raw == word || !CorrectionRules.IsCandidate(word, ignored) || TextBoundary.IsProtected(raw, ignored) ||
                !raw.All(c => CorrectionRules.IsLetter(c) || IsLatinLetterKey(c)) || corrected.Count(value => value == word) != 1) continue;
            if (word == Convert(raw) || word == Normalize(raw, false) || word == Normalize(raw, true)) yield return word;
        }
    }

    public static IEnumerable<string> AdjacentKeyTypos(string word)
    {
        // Physical QWERTY rows, including punctuation keys used for Russian letters.
        string[] rows = ["`qwertyuiop[]", "asdfghjkl;'", "zxcvbnm,."];
        double[] offsets = [-1, .25, .75];
        for (var index = 0; index < word.Length; index++)
        {
            var russian = CorrectionRules.IsRussianLetter(word[index]);
            var key = char.ToLowerInvariant(russian ? ConvertCharacter(word[index]) : word[index]);
            var row = Array.FindIndex(rows, value => value.Contains(key));
            if (row < 0) continue;
            var column = rows[row].IndexOf(key) + offsets[row];
            for (var otherRow = Math.Max(0, row - 1); otherRow <= Math.Min(rows.Length - 1, row + 1); otherRow++)
                for (var otherColumn = 0; otherColumn < rows[otherRow].Length; otherColumn++)
                {
                    var otherKey = rows[otherRow][otherColumn];
                    if (otherKey == key || Math.Abs(otherColumn + offsets[otherRow] - column) > 1) continue;
                    var letter = russian ? ConvertCharacter(otherKey) : otherKey;
                    if (!CorrectionRules.IsLetter(letter)) continue;
                    if (char.IsUpper(word[index])) letter = char.ToUpperInvariant(letter);
                    yield return word[..index] + letter + word[(index + 1)..];
                }
        }
    }

    private static char ConvertCharacter(char c)
    {
        var index = Latin.IndexOf(c);
        if (index >= 0) return Russian[index];
        index = LatinShifted.IndexOf(c);
        if (index >= 0) return RussianShifted[index];
        index = Russian.IndexOf(c);
        if (index >= 0) return Latin[index];
        index = RussianShifted.IndexOf(c);
        return index >= 0 ? LatinShifted[index] : c;
    }
}
