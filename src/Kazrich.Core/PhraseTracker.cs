namespace Kazrich.Core;

// Retain unfinished work when the user types faster than inference.
public sealed class PhraseTracker
{
    private string text = "";
    private bool blocked;
    public void Reset() { text = ""; blocked = false; }
    public void Backspace() { if (text.Length > 0) text = text[..^1]; else blocked = true; }
    public string? Feed(char c)
    {
        if (blocked) { if (c == ' ') Reset(); return null; }
        if (text.Length >= 160) { Reset(); blocked = c != ' '; return null; }
        text += c;
        return c == ' ' || ",.!?;:".Contains(c) ? text : null;
    }
    public void Replaced(string replacement) => text = replacement;
}
