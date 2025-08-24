using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction
{
    public interface IEmbedProbeService
    {
        Task<Nuuz.Application.DTOs.EmbedCheckResult> CheckAsync(string url, CancellationToken ct = default);
    }
}
