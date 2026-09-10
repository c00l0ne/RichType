namespace Kazrich.Core;

public sealed record TypingBatch(string Text, int Consumed, bool Unfinished = false)
{
    public TypingBatch DeferUnchangedShortWord(string replacement)
    {
        // Keep an unresolved final short word available when a correction has
        // already changed its neighbours. A line break ends that opportunity.
        if (Consumed != Text.Length || Text.Contains('\n') || replacement == Text) return this;
        var end = Text.Length;
        while (end > 0 && char.IsWhiteSpace(Text[end - 1])) end--;
        var start = end;
        while (start > 0 && CorrectionRules.IsLetter(Text[start - 1])) start--;
        if (end - start is < 1 or > 2 || start == 0 || !char.IsWhiteSpace(Text[start - 1]) ||
            !Text[..start].Any(CorrectionRules.IsLetter) ||
            end - start == 2 && Text[start..end].All(char.IsUpper)) return this;
        var suffix = Text[start..];
        var replacementStart = replacement.Length - suffix.Length;
        if (!replacement.EndsWith(suffix, StringComparison.Ordinal) || replacementStart <= 0 ||
            !char.IsWhiteSpace(replacement[replacementStart - 1])) return this;
        return new(Text[..start], start);
    }
}

// All offsets use normalized newlines, matching one caret step per line break.
public sealed class TypingQueue
{
    public const int Capacity = 2048;
    public string Text { get; private set; } = "";
    private bool blocked;
    public void Reset() { Text = ""; blocked = false; }
    public void Invalidate() { Text = ""; blocked = true; }
    public void Backspace()
    {
        if (Text.Length > 0) Text = Text[..^1];
        else Invalidate();
    }
    public bool Append(char c)
    {
        if (c == '\r') c = '\n';
        if (blocked) { if (char.IsWhiteSpace(c)) Reset(); return true; }
        if (Text.Length >= Capacity || !(c is >= ' ' and <= '~' || char.IsWhiteSpace(c) || TextBoundary.IsSign(c) || CorrectionRules.IsLetter(c)))
        { Invalidate(); return false; }
        Text += c;
        return true;
    }
    public TypingBatch? Next(int limit = 160, bool includeUnfinished = false, bool hasContext = false)
    {
        var shortMixed = Text.Trim().Length == 2 && Text.Any(char.IsAsciiLetter) && Text.Any(CorrectionRules.IsRussianLetter);
        var shortKeyboard = Text.Trim().Length == 2 && Text.Any(char.IsAsciiLetter) &&
            Text.Trim().Any(c => !CorrectionRules.IsLetter(c) && KeyboardLayout.IsLatinLetterKey(c)) &&
            KeyboardLayout.Normalize(Text.Trim(), false).All(CorrectionRules.IsRussianLetter);
        var shortWithContext = hasContext && Text.Trim().Length is 1 or 2 && Text.Trim().All(CorrectionRules.IsLetter);
        if (includeUnfinished && Text.Length <= limit && !Text.Contains('\n') && (Text.Trim().Length >= 3 || shortMixed || shortKeyboard || shortWithContext))
            return new TypingBatch(Text, Text.Length, !char.IsWhiteSpace(Text[^1]));
        var boundary = -1;
        for (var i = 0; i < Math.Min(Text.Length, limit); i++)
        {
            if (Text[i] == '\n')
                return new TypingBatch(Text[..i], i + 1); // Never reinject Enter into a chat.
            // A trailing hyphen can start another component of this word.
            // Let the idle check mark it unfinished so later input is read whole.
            if (Text[i] == '-' && i > 0 && CorrectionRules.IsLetter(Text[i - 1]) && i + 1 == Text.Length) continue;
            // A Latin punctuation key can still be a Russian letter. Do not
            // finish that token until a real delimiter or the idle timer arrives.
            if (char.IsWhiteSpace(Text[i]) || TextBoundary.IsSign(Text[i]) && !KeyboardLayout.IsLatinLetterKey(Text[i]) &&
                (i + 1 == Text.Length || char.IsWhiteSpace(Text[i + 1]))) boundary = i;
        }
        if (boundary < 0) return null;
        var end = boundary + 1;
        // A one-letter layout error needs a neighbour: keep an isolated short word
        // at the back of the queue rather than permanently accepting it too early.
        var trimmed = Text[..end].TrimEnd();
        var wordStart = LastWhitespace(trimmed) + 1;
        var lastWord = trimmed[wordStart..];
        // If the final key can be either a letter or punctuation, one completed
        // neighbour supplies grammatical context. An idle batch also resolves
        // the token when the user stops without typing that neighbour.
        if (Text.Length <= limit && lastWord.Length > 0 && lastWord.Any(char.IsAsciiLetter) &&
            TextBoundary.IsSign(lastWord[^1]) && KeyboardLayout.IsLatinLetterKey(lastWord[^1]))
            end = wordStart;
        if (lastWord.Length is 1 or 2 && lastWord.All(CorrectionRules.IsLetter))
        {
            var previous = trimmed[..wordStart].TrimEnd();
            var previousWord = previous[(LastWhitespace(previous) + 1)..];
            var hasNeighbour = previousWord.Length > 0 && previousWord.All(CorrectionRules.IsLetter) &&
                previousWord.Any(char.IsAsciiLetter) == lastWord.Any(char.IsAsciiLetter);
            if (!hasNeighbour && !(hasContext && wordStart == 0)) end = wordStart;
        }
        return end == 0 ? null : new TypingBatch(Text[..end], end);
    }
    public bool Matches(TypingBatch batch) => Text.Length >= batch.Consumed && Text.StartsWith(batch.Text, StringComparison.Ordinal) &&
        (batch.Text.Length == 0 || char.IsWhiteSpace(batch.Text[^1]) || batch.Consumed > batch.Text.Length ||
            Text.Length == batch.Text.Length || char.IsWhiteSpace(Text[batch.Text.Length]));
    public void Consume(TypingBatch batch)
    {
        if (!Matches(batch)) throw new InvalidOperationException("Набранный текст изменился.");
        Text = Text[batch.Consumed..];
    }
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static int LastWhitespace(string text)
    {
        for (var index = text.Length - 1; index >= 0; index--) if (char.IsWhiteSpace(text[index])) return index;
        return -1;
    }
}
