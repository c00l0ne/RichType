namespace Kazrich.Core;

public sealed record EditedWord(string Word, int CaretOffset, string Before, string After)
{
    public static EditedWord? Around(string left, string right, bool preferRight = false)
    {
        var text = left + right;
        var caret = left.Length;
        var point = caret - 1;
        if (preferRight && right.Length > 0 && !char.IsWhiteSpace(right[0])) point = caret;
        else while (point >= 0 && char.IsWhiteSpace(text[point])) point--;
        if (point < 0 || point >= text.Length) return null;
        var start = point;
        var end = point + 1;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        if (end - start is < 2 or > 32 || caret - end > 2) return null;
        return new(text[start..end], caret - start, text[Math.Max(0, start - 24)..start],
            text[end..Math.Min(text.Length, end + 24)]);
    }

    public int CaretAfter(string replacement) => CaretOffset >= Word.Length
        ? replacement.Length + CaretOffset - Word.Length : Math.Min(CaretOffset, replacement.Length);
}
