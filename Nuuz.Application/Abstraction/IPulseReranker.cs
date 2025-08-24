using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public interface IPulseReranker
    {
        public sealed record Input(
            string Id,
            string Title,
            string SourceId,
            DateTimeOffset PublishedAt,
            string? Summary,
            IReadOnlyList<string> Tags);

        public sealed record Choice(
            string Id,
            double Score,              // 0..1 importance (relative)
            IReadOnlyList<string> Reasons);

        Task<IReadOnlyList<Choice>> RerankAsync(
            IReadOnlyList<Input> items,
            int topK,
            CancellationToken ct = default);
    }
}
