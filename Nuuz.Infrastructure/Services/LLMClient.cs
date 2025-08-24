using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public interface ILLMClient
    {
        Task<string> GenerateAsync(string systemPrompt, string userPrompt, double temperature = 0.7, int maxOutputTokens = 1000, CancellationToken ct = default);
    }

    public sealed class OpenAIChatClient : ILLMClient
    {
        private readonly HttpClient _http;
        private readonly string _model;

        public OpenAIChatClient(HttpClient httpClient, string apiKey, string model = "gpt-4o-mini")
        {
            _http = httpClient;
            _http.BaseAddress = new Uri("https://api.openai.com/v1/");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _model = model;
        }

        public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, double temperature = 0.7, int maxOutputTokens = 1000, CancellationToken ct = default)
        {
            var body = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature,
                max_tokens = maxOutputTokens
            };

            var json = JsonSerializer.Serialize(body);
            using var resp = await _http.PostAsync("chat/completions", new StringContent(json, Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);

            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return content ?? string.Empty;
        }
    }
}
