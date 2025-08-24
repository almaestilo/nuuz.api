namespace Nuuz.Application.Services;

public interface IInterestMatcher
{
    /// <summary>
    /// Returns system interest IDs (not names) that match the given content.
    /// Max 10 IDs (Firestore array-contains-any limit).
    /// </summary>
    Task<List<string>> MatchAsync(string title, string? text);
}
