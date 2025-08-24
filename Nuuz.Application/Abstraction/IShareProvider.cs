using Nuuz.Domain.Entities;

namespace Nuuz.Application.Services;

public record ProviderLimits(int MaxChars, int UrlWeight, bool SupportsImages);

public record PrepareShareResult(
    string ShareId,
    string Title,
    string TextTemplate,
    string[] Hashtags,
    string ShortUrl,
    ProviderLimits Limits,
    bool Connected);

public record PostShareResult(
    string Status,
    string? ProviderPostId,
    string? ProviderPermalink,
    string? Error = null);

public interface IShareProvider
{
    string Key { get; } // "twitter"
    ProviderLimits Limits { get; }

    Task<string> ConnectStartAsync(string userId, string? redirectTo, CancellationToken ct = default);
    Task<bool> ConnectCallbackAsync(string state, string code, CancellationToken ct = default);

    Task<PrepareShareResult> PrepareAsync(string userId, string articleId, CancellationToken ct = default);
    Task<PostShareResult> PostAsync(string userId, string shareId, string text, CancellationToken ct = default);

}

public interface ISecretConnectShareProvider : IShareProvider
{
    Task<bool> ConnectWithSecretAsync(string userId, string identifier, string secret, CancellationToken ct = default);
}
