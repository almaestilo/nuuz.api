using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public interface ITextEmbedder
    {
        Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    }
}

