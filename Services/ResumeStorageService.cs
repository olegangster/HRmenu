using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using TZApp.Models;

namespace TZApp.Services
{
    public interface IResumeStorageService
    {
        Task<string> UploadAsync(Stream pdf, string candidateName, string contact, string originalFileName, CancellationToken ct = default);
        Task<List<ResumeItem>> ListAsync(CancellationToken ct = default);
        Task<string?> GetPresignedUrlAsync(string id, bool download, CancellationToken ct = default);
        Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    }

    public class ResumeStorageService : IResumeStorageService
    {
        public const string Prefix = "Resumes/";
        private const string MetaPrefix = "x-amz-meta-";
        private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(5);

        private readonly IAmazonS3 _s3;
        private readonly string _bucket;

        public ResumeStorageService(IAmazonS3 s3, IConfiguration config)
        {
            _s3 = s3;
            _bucket = config["Aws:BucketName"] ?? "bucket-homework-12345";
        }

        // id = Guid у форматі "N" (32 hex-символи) -> ключ в S3: Resumes/<id>.pdf
        private static string KeyFor(string id) => $"{Prefix}{id}.pdf";

        private static bool IsValidId(string? id) =>
            Guid.TryParseExact(id, "N", out _);

        /// <summary>Завантаження PDF в S3 напряму зі Stream (без збереження на диск сервера).</summary>
        public async Task<string> UploadAsync(Stream pdf, string candidateName, string contact,
            string originalFileName, CancellationToken ct = default)
        {
            // Унікальне ім'я: GUID гарантує, що резюме різних кандидатів не перезапишуть одне одного
            string id = Guid.NewGuid().ToString("N");

            var request = new TransferUtilityUploadRequest
            {
                BucketName = _bucket,
                Key = KeyFor(id),
                InputStream = pdf,
                ContentType = "application/pdf",
                AutoCloseStream = false
            };

            // S3 metadata приймає тільки ASCII -> URL-кодуємо
            request.Metadata["candidatename"] = Uri.EscapeDataString(candidateName);
            request.Metadata["contact"] = Uri.EscapeDataString(contact);
            request.Metadata["originalfilename"] = Uri.EscapeDataString(originalFileName);

            using var transfer = new TransferUtility(_s3);
            await transfer.UploadAsync(request, ct);

            return id;
        }

        public async Task<List<ResumeItem>> ListAsync(CancellationToken ct = default)
        {
            var objects = new List<S3Object>();
            string? token = null;

            do
            {
                var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = Prefix,
                    ContinuationToken = token
                }, ct);

                objects.AddRange((response.S3Objects ?? new List<S3Object>())
                    .Where(o => o.Key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)));

                token = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (token != null);

            var items = await Task.WhenAll(objects.Select(o => ToItemAsync(o, ct)));

            return items
                .Where(i => i != null)
                .Select(i => i!)
                .OrderByDescending(i => i.UploadedAt)
                .ToList();
        }

        private async Task<ResumeItem?> ToItemAsync(S3Object obj, CancellationToken ct)
        {
            string id = Path.GetFileNameWithoutExtension(obj.Key);
            if (!IsValidId(id)) return null;

            var item = new ResumeItem
            {
                Id = id,
                FileName = Path.GetFileName(obj.Key),
                CandidateName = "—",
                UploadedAt = obj.LastModified.GetValueOrDefault().ToLocalTime(),
                Size = obj.Size.GetValueOrDefault()
            };

            try
            {
                var meta = await _s3.GetObjectMetadataAsync(_bucket, obj.Key, ct);
                item.CandidateName = GetMeta(meta.Metadata, "candidatename") ?? "—";
                item.Contact = GetMeta(meta.Metadata, "contact") ?? "";
                item.FileName = GetMeta(meta.Metadata, "originalfilename") ?? item.FileName;
            }
            catch (AmazonS3Exception)
            {
                // якщо metadata не вдалося прочитати — показуємо те, що є
            }

            return item;
        }

        /// <summary>Тимчасове (Presigned) посилання на відкриття або завантаження резюме.</summary>
        public async Task<string?> GetPresignedUrlAsync(string id, bool download, CancellationToken ct = default)
        {
            if (!IsValidId(id)) return null;
            string key = KeyFor(id);

            GetObjectMetadataResponse meta;
            try
            {
                meta = await _s3.GetObjectMetadataAsync(_bucket, key, ct);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            string original = GetMeta(meta.Metadata, "originalfilename") ?? $"{id}.pdf";
            string disposition = download ? "attachment" : "inline";

            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(UrlLifetime)
            };
            request.ResponseHeaderOverrides.ContentType = "application/pdf";
            request.ResponseHeaderOverrides.ContentDisposition =
                $"{disposition}; filename=\"resume.pdf\"; filename*=UTF-8''{Uri.EscapeDataString(original)}";

            return _s3.GetPreSignedURL(request);
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            if (!IsValidId(id)) return false;
            await _s3.DeleteObjectAsync(_bucket, KeyFor(id), ct);
            return true;
        }

        private static string? GetMeta(MetadataCollection metadata, string name)
        {
            string full = MetaPrefix + name;
            if (!metadata.Keys.Contains(full)) return null;
            var value = metadata[full];
            return string.IsNullOrEmpty(value) ? null : Uri.UnescapeDataString(value);
        }
    }
}
