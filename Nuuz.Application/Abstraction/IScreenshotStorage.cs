// Nuuz.Application.Abstraction/IScreenshotStorage.cs
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction
{
    public interface IScreenshotStorage
    {
        /// <summary>Uploads bytes and returns a public or signed URL (or gs:// URI).</summary>
        Task<string> SaveAsync(string feedbackId, byte[] bytes, string mediaType);
    }
}
