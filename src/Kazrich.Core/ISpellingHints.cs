namespace Kazrich.Core;

/// <summary>Independent spelling evidence. Null means the language or provider is unavailable.</summary>
public sealed record SpellingEvidence(bool HasError, IReadOnlyList<string> Suggestions);

public interface ISpellingHints
{
    Task<SpellingEvidence?> CheckAsync(string word, CancellationToken cancellationToken);
}
