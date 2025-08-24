// Nuuz.Application/Services/ISparkNotesService.cs
namespace Nuuz.Application.Services;

public sealed record SparkNotesResult(string Html, string PlainText);

public interface ISparkNotesService
{
    Task<SparkNotesResult> BuildAsync(string url, string title, string seedText, CancellationToken ct = default);
}
