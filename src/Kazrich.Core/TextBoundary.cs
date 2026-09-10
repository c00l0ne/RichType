using System.Globalization;
using System.Text.RegularExpressions;

namespace Kazrich.Core;

public static class TextBoundary
{
    private static readonly HashSet<string> TopLevelDomains = LoadDomains();
    private static HashSet<string> LoadDomains()
    {
        using var stream = typeof(TextBoundary).Assembly.GetManifestResourceStream("Kazrich.Core.Data.tlds-alpha-by-domain.txt")!;
        using var reader = new StreamReader(stream);
        return new HashSet<string>(reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')), StringComparer.OrdinalIgnoreCase);
    }
    public static bool IsSign(char c) => char.IsPunctuation(c) || char.IsSymbol(c);
    public static bool IsOpening(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation || c is '\"' or '\'';
    public static bool IsClosing(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation || c is '\"' or '\'';
    public static bool IsInternalJoiner(string text, int index) => text[index] is '-' or '‐' or '‑' or '\'' or '’' &&
        index > 0 && index + 1 < text.Length && CorrectionRules.IsLetter(text[index - 1]) && CorrectionRules.IsLetter(text[index + 1]);
    public static bool IsProtected(string token, ISet<string>? ignored = null)
    {
        if (ignored?.Contains(token) == true) return true;
        var value = token.Trim('(', ')', '[', ']', '{', '}', '«', '»', '‹', '›', '\"', '\'', '“', '”', '‘', '’');
        if (value.Contains('@') || value.Contains('\\') || value.Contains('_')) return true;
        if (value.Contains('/')) return true;
        if (Regex.IsMatch(value, @"(?:\p{L}\d|\d\p{L})") || Regex.IsMatch(value, @"^\d+(?:\.\d+)+[.,;:!?]?$")) return true;
        var host = value.TrimEnd('.', ',', ';', ':', '!', '?');
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length == 0 || label.Any(c => !char.IsLetterOrDigit(c) && c != '-'))) return false;
        try { return TopLevelDomains.Contains(new IdnMapping().GetAscii(labels[^1])); }
        catch (ArgumentException) { return false; }
    }
    public static bool IsStart(string text, int index, ISet<string>? ignored = null)
    {
        if (index <= 0 || index >= text.Length) return false;
        if (char.IsWhiteSpace(text[index - 1])) return true;
        if (!IsSign(text[index - 1])) return false;
        var start = index - 1;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = index;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var token = text[start..end];
        if (IsProtected(token, ignored)) return false;
        return !IsInternalJoiner(token, index - start - 1);
    }
}
