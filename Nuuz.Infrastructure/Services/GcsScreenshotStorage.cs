// Nuuz.Infrastructure.Services/GcsScreenshotStorage.cs
using Google.Cloud.Storage.V1;
using Nuuz.Application.Abstraction;
using System;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    public class GcsScreenshotStorage : IScreenshotStorage
    {
        private readonly StorageClient _client;
        private readonly string _bucket;
        private readonly bool _makePublic;

        public GcsScreenshotStorage(StorageClient client, string bucketName, bool makePublic = false)
        {
            _client = client;
            _bucket = bucketName ?? throw new ArgumentNullException(nameof(bucketName));
            _makePublic = makePublic;
        }

        public async Task<string> SaveAsync(string feedbackId, byte[] bytes, string mediaType)
        {
            // Object name: feedback/{yyyy}/{MM}/{dd}/{feedbackId}.{ext}
            var ext = GuessExtension(mediaType);
            var now = DateTime.UtcNow;
            var objectName = $"feedback/{now:yyyy}/{now:MM}/{now:dd}/{feedbackId}{ext}";

            using var ms = new System.IO.MemoryStream(bytes);
            var obj = await _client.UploadObjectAsync(_bucket, objectName, mediaType, ms);

            if (_makePublic)
            {
                // Requires bucket IAM: "allUsers" -> "Storage Object Viewer" or equivalent
                await _client.UpdateObjectAsync(new Google.Apis.Storage.v1.Data.Object
                {
                    Bucket = obj.Bucket,
                    Name = obj.Name,
                    Acl = new[] { new Google.Apis.Storage.v1.Data.ObjectAccessControl { Entity = "allUsers", Role = "READER" } }
                });
                return $"https://storage.googleapis.com/{obj.Bucket}/{obj.Name}";
            }

            // Return gs:// by default (you can sign URLs at read time if needed)
            return $"gs://{obj.Bucket}/{obj.Name}";
        }

        private static string GuessExtension(string mediaType) =>
            mediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ""
            };
    }
}
