using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public sealed record UnifiedNotesResult(RichSignals Rich, SparkNotesResult Spark);

    public interface IUnifiedNotesService
    {
        Task<UnifiedNotesResult> BuildAsync(string url, string title, string seedText, CancellationToken ct = default);
    }
}

