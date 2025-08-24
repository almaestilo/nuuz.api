namespace Nuuz.Application.Services;

public interface IShareService
{
    string ComposeWithLimits(string template, string shortUrl, IEnumerable<string> hashtags, ProviderLimits limits);
}
