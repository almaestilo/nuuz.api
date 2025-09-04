using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services
{
    public sealed class OpenAITextEmbedder : ITextEmbedder
    {
        private readonly HttpClient _http;
        private readonly string _model;

        public OpenAITextEmbedder(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            var baseUrl = cfg["OpenAI:BaseUrl"] ?? "https://api.openai.com";
            var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey missing");
            _model = cfg["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";

            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            if (!_http.DefaultRequestHeaders.Contains("Authorization"))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var payload = new
            {
                model = _model,
                input = text ?? string.Empty
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("v1/embeddings", content, ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray();
            return arr.Select(x => x.GetSingle()).ToArray();
        }
    }
}

