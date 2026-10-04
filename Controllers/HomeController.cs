using Microsoft.AspNetCore.Mvc;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Text;
using System.Text.Json;
using TZApp.Models;

namespace TZApp.Controllers
{
    public class HomeController : Controller
    {
        private readonly string bucketName = "bucket-homework-12345";
        private readonly IAmazonS3 _s3Client;

        public HomeController(IConfiguration config)
        {
            var accessKey = config["Aws:AccessKey"]
                ?? throw new InvalidOperationException("Aws:AccessKey не задан");
            var secretKey = config["Aws:SecretKey"]
                ?? throw new InvalidOperationException("Aws:SecretKey не задан");

            _s3Client = new AmazonS3Client(accessKey, secretKey, RegionEndpoint.EUCentral1);
        }

        [HttpGet]
        public IActionResult Menu() => View();

        [HttpGet]
        public IActionResult Index() => View();

        [HttpPost]
        public async Task<IActionResult> SaveText(string tzText)
        {
            if (string.IsNullOrWhiteSpace(tzText))
            {
                ViewBag.Message = "Текст ТЗ порожній!";
                return View("Index");
            }

            string fileName = $"TZ_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string localPath = Path.Combine(Path.GetTempPath(), fileName);

            await System.IO.File.WriteAllTextAsync(localPath, tzText, Encoding.UTF8);
            await UploadToS3(localPath, fileName);

            ViewBag.Message = $"Файл \"{fileName}\" успішно збережено та завантажено в S3!";
            return View("Index");
        }

        [HttpPost]
        public async Task<IActionResult> SaveFile(IFormFile tzFile)
        {
            if (tzFile == null || tzFile.Length == 0)
            {
                ViewBag.Message = "Файл не вибрано!";
                return View("Index");
            }

            string fileName = $"TZ_{DateTime.Now:yyyyMMdd_HHmmss}_{tzFile.FileName}";
            string localPath = Path.Combine(Path.GetTempPath(), fileName);

            using (var stream = new FileStream(localPath, FileMode.Create))
            {
                await tzFile.CopyToAsync(stream);
            }

            await UploadToS3(localPath, fileName);

            ViewBag.Message = $"Файл \"{fileName}\" успішно завантажено в S3!";
            return View("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Candidates()
        {
            var tasks = new List<(string Key, DateTime Date)>();

            try
            {
                var response = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucketName });

                foreach (var obj in response.S3Objects)
                {
                    if (obj.Key.StartsWith("TZ_"))
                        tasks.Add((obj.Key, obj.LastModified.GetValueOrDefault()));
                }
            }
            catch (Exception)
            {
            }

            ViewBag.Tasks = tasks.OrderByDescending(t => t.Date).ToList();
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> TaskDetail(string key)
        {
            if (string.IsNullOrEmpty(key)) return RedirectToAction("Candidates");

            ViewBag.TaskKey = key;
            ViewBag.TzText = await ReadTaskText(key, "Не вдалося завантажити текст завдання з S3. Можливо, це не текстовий формат.");
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> UploadResume(string taskKey, string candidateName, IFormFile resumeFile)
        {
            ViewBag.TaskKey = taskKey;
            ViewBag.TzText = await ReadTaskText(taskKey, "Не вдалося завантажити текст завдання.");

            if (resumeFile == null || resumeFile.Length == 0)
            {
                ViewBag.Message = "Будь ласка, оберіть файл резюме!";
                return View("TaskDetail");
            }

            string safeName = string.IsNullOrWhiteSpace(candidateName) ? "Unknown" : candidateName.Replace(" ", "_");
            string safeTaskKey = string.IsNullOrWhiteSpace(taskKey) ? "Task" : taskKey.Replace(".txt", "");

            string fileName = $"Resume_{safeName}_for_{safeTaskKey}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(resumeFile.FileName)}";
            string localPath = Path.Combine(Path.GetTempPath(), fileName);

            using (var stream = new FileStream(localPath, FileMode.Create))
            {
                await resumeFile.CopyToAsync(stream);
            }

            // metadata (URL-кодування, бо S3 приймає в metadata тільки ASCII)
            var metadata = new Dictionary<string, string>
            {
                ["taskkey"] = Uri.EscapeDataString(taskKey ?? ""),
                ["candidatename"] = Uri.EscapeDataString(candidateName ?? "")
            };

            // Після цього S3 сам запускає Lambda — аналіз іде автоматично
            await UploadToS3(localPath, fileName, metadata);

            ViewBag.Message = "Дякуємо! Ваше резюме успішно надіслано. Воно буде автоматично проаналізоване.";
            return View("TaskDetail");
        }

        // ===== Результати аналізу (читаємо Analysis_*.json, які створила Lambda) =====

        [HttpGet]
        public async Task<IActionResult> Results()
        {
            var results = new List<AnalysisRecord>();

            try
            {
                var list = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    Prefix = "Analysis_"
                });

                foreach (var obj in list.S3Objects.Where(o => o.Key.EndsWith(".json")))
                {
                    try
                    {
                        using var response = await _s3Client.GetObjectAsync(bucketName, obj.Key);
                        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
                        string json = await reader.ReadToEndAsync();

                        var record = ParseRecord(json, obj.Key);
                        if (record.CreatedAt == default)
                            record.CreatedAt = obj.LastModified.GetValueOrDefault();

                        results.Add(record);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Не вдалося завантажити результати з S3: " + ex.Message;
            }

            return View(results.OrderByDescending(r => r.CreatedAt).ToList());
        }

        [HttpGet]
        public async Task<IActionResult> DownloadAnalysis(string key)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith("Analysis_") || !key.EndsWith(".json"))
                return BadRequest();

            try
            {
                using var response = await _s3Client.GetObjectAsync(bucketName, key);
                using var ms = new MemoryStream();
                await response.ResponseStream.CopyToAsync(ms);
                return File(ms.ToArray(), "application/json", key);
            }
            catch
            {
                return NotFound();
            }
        }

        // ===== Допоміжні методи =====

        private static AnalysisRecord ParseRecord(string json, string key)
        {
            var rec = new AnalysisRecord { Key = key, RawJson = json };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            rec.TaskKey = GetStr(root, "taskKey");
            rec.ResumeFile = GetStr(root, "resumeFile");
            rec.CandidateName = GetStr(root, "candidateName");

            if (DateTime.TryParse(GetStr(root, "analyzedAt"), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                rec.CreatedAt = dt.ToLocalTime();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("analysis", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                rec.Error = GetStr(a, "error");
                rec.FirstName = GetStr(a, "firstName");
                rec.LastName = GetStr(a, "lastName");
                rec.Skills = GetList(a, "skills");
                rec.ExperienceYears = GetNum(a, "experienceYears");
                rec.ExperienceSummary = GetStr(a, "experienceSummary");
                rec.MatchPercent = GetNum(a, "matchPercent");
                rec.Strengths = GetList(a, "strengths");
                rec.Gaps = GetList(a, "gaps");
                rec.Verdict = GetStr(a, "verdict");
            }

            return rec;
        }

        private static string? GetStr(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
            return null;
        }

        private static double? GetNum(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number &&
                p.TryGetDouble(out var d))
                return d;
            return null;
        }

        private static List<string> GetList(JsonElement el, string name)
        {
            var list = new List<string>();
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in p.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                        list.Add(s);
            }
            return list;
        }

        private async Task<string> ReadTaskText(string key, string errorText)
        {
            try
            {
                using var response = await _s3Client.GetObjectAsync(bucketName, key);
                using var reader = new StreamReader(response.ResponseStream);
                return await reader.ReadToEndAsync();
            }
            catch
            {
                return errorText;
            }
        }

        private async Task UploadToS3(string localPath, string fileName, Dictionary<string, string>? metadata = null)
        {
            using var transferUtility = new TransferUtility(_s3Client);

            var request = new TransferUtilityUploadRequest
            {
                BucketName = bucketName,
                Key = fileName,
                FilePath = localPath
            };

            if (metadata != null)
                foreach (var kv in metadata)
                    request.Metadata[kv.Key] = kv.Value;

            await transferUtility.UploadAsync(request);
            System.IO.File.Delete(localPath);
        }
    }
}