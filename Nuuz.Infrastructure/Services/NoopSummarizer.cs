////using Nuuz.Application.Services;
////using System;
////using System.Collections.Generic;
////using System.Linq;
////using System.Text;
////using System.Threading.Tasks;

////namespace Nuuz.Infrastructure.Services;
////public sealed class NoopSummarizer : IAISummarizer
////{
////    public Task<(string summary, string vibe, string[] tags)> SummarizeAsync(string title, string text)
////        => Task.FromResult((
////            summary: text.Length > 280 ? text[..280] + "…" : text,
////            vibe: "Neutral",
////            tags: Array.Empty<string>()
////        ));
////}
