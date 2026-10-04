using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TZApp.Models;

namespace TZApp.Services
{
    public interface IDashboardService
    {
        Task<List<CandidateAnalysis>> LoadAllAsync(CancellationToken ct = default);
        Task<CandidateAnalysis?> GetAsync(string key, CancellationToken ct = default);
    }

    /// <summary>
    /// Збирає дані для HR Dashboard: читає Analysis_*.json (їх створює Lambda),
    /// а резюме без результату показує зі статусом "Очікує аналізу".
    /// </summary>
    public class DashboardService : IDashboardService
    {
        private const string AnalysisPrefix = "Analysis_";
        private static readonly string[] ResumePrefixes = { "Resumes/", "Resume_" };

        private readonly IAmazonS3 _s3;
        private readonly string _bucket;

        public DashboardService(IAmazonS3 s3, IConfiguration config)
        {
            _s3 = s3;
            _bucket = config["Aws:BucketName"] ?? "bucket-homework-12345";
        }

        public async Task<List<CandidateAnalysis>> LoadAllAsync(CancellationToken ct = default)
        {
            var analysisObjs = (await ListAsync(AnalysisPrefix, ct))
                .Where(o => o.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();

            var resumeObjs = new List<S3Object>();
            foreach (var p in ResumePrefixes)
                resumeObjs.AddRange((await ListAsync(p, ct)).Where(o => !o.Key.EndsWith("/")));

            var analyses = (await Task.WhenAll(analysisObjs.Select(o => ReadAnalysisAsync(o, ct))))
                .Where(a => a != null).Select(a => a!).ToList();

            var result = new List<CandidateAnalysis>(analyses);
            var matchedResumeKeys = new HashSet<string>();

            // прив'язуємо результати до резюме
            foreach (var a in analyses)
            {
                var resume = resumeObjs.FirstOrDefault(r => SameFile(r.Key, a.ResumeFile));
                if (resume != null)
                {
                    a.ResumeKey = resume.Key;
                    a.ResumeId = ResumeIdFromKey(resume.Key);
                    matchedResumeKeys.Add(resume.Key);
                }
            }

            // резюме без результату -> "Очікує аналізу"
            var pending = await Task.WhenAll(resumeObjs
                .Where(r => !matchedResumeKeys.Contains(r.Key))
                .Select(r => ToPendingAsync(r, ct)));
            result.AddRange(pending);

            return result.OrderByDescending(c => c.Date).ToList();
        }

        public async Task<CandidateAnalysis?> GetAsync(string key, CancellationToken ct = default)
        {
            if (key.StartsWith(AnalysisPrefix) && key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var all = await LoadAllAsync(ct);
                return all.FirstOrDefault(c => c.AnalysisKey == key);
            }

            if (ResumePrefixes.Any(p => key.StartsWith(p)))
            {
                var all = await LoadAllAsync(ct);
                return all.FirstOrDefault(c => c.Id == key || c.ResumeKey == key);
            }

            return null;
        }

        // ===== S3 =====

        private async Task<List<S3Object>> ListAsync(string prefix, CancellationToken ct)
        {
            var list = new List<S3Object>();
            string? token = null;
            do
            {
                var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = token
                }, ct);

                list.AddRange(resp.S3Objects ?? new List<S3Object>());
                token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
            }
            while (token != null);
            return list;
        }

        private async Task<CandidateAnalysis?> ReadAnalysisAsync(S3Object obj, CancellationToken ct)
        {
            try
            {
                using var resp = await _s3.GetObjectAsync(_bucket, obj.Key, ct);
                using var reader = new StreamReader(resp.ResponseStream, Encoding.UTF8);
                string json = await reader.ReadToEndAsync(ct);
                return Parse(json, obj.Key, obj.LastModified.GetValueOrDefault().ToLocalTime());
            }
            catch
            {
                return null;
            }
        }

        private async Task<CandidateAnalysis> ToPendingAsync(S3Object obj, CancellationToken ct)
        {
            var c = new CandidateAnalysis
            {
                Id = obj.Key,
                ResumeKey = obj.Key,
                ResumeId = ResumeIdFromKey(obj.Key),
                ResumeFile = Path.GetFileName(obj.Key),
                Status = AnalysisStatus.Pending,
                Date = obj.LastModified.GetValueOrDefault().ToLocalTime()
            };

            try
            {
                var meta = await _s3.GetObjectMetadataAsync(_bucket, obj.Key, ct);
                string? name = Meta(meta.Metadata, "candidatename");
                if (!string.IsNullOrWhiteSpace(name)) c.Name = name;
                string? original = Meta(meta.Metadata, "originalfilename");
                if (!string.IsNullOrWhiteSpace(original)) c.ResumeFile = original;
                string? task = Meta(meta.Metadata, "taskkey");
                if (!string.IsNullOrWhiteSpace(task)) c.TaskKey = task;
            }
            catch (AmazonS3Exception) { }

            return c;
        }

        // ===== Парсинг JSON від Lambda (терпимий до різних назв полів) =====

        private static CandidateAnalysis Parse(string json, string key, DateTime fallbackDate)
        {
            var c = new CandidateAnalysis
            {
                Id = key,
                AnalysisKey = key,
                RawJson = json,
                Date = fallbackDate,
                Status = AnalysisStatus.Done
            };

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var a = root;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("analysis", out var inner) && inner.ValueKind == JsonValueKind.Object)
                    a = inner;

                var sources = new[] { a, root };

                c.TaskKey = FirstStr(sources, "taskKey");
                c.ResumeFile = FirstStr(sources, "resumeFile");

                string first = FirstStr(sources, "firstName") ?? "";
                string last = FirstStr(sources, "lastName") ?? "";
                string full = $"{first} {last}".Trim();
                if (string.IsNullOrEmpty(full)) full = FirstStr(sources, "candidateName", "name", "fullName") ?? "";
                if (!string.IsNullOrEmpty(full)) c.Name = full;

                c.Skills = FirstList(sources, "skills", "foundSkills");
                c.Confirmed = FirstList(sources, "confirmedRequirements", "matchedRequirements", "requirementsMet", "confirmed");
                c.Missing = FirstList(sources, "missingRequirements", "unconfirmedRequirements", "notFoundRequirements",
                    "requirementsNotFound", "missing");
                c.Strengths = FirstList(sources, "strengths");
                c.Gaps = FirstList(sources, "gaps");

                // якщо окремого списку "не знайдено" немає, показуємо gaps
                if (c.Missing.Count == 0) c.Missing = new List<string>(c.Gaps);

                c.Verdict = FirstStr(sources, "verdict");
                c.Summary = FirstStr(sources, "summary", "aiSummary", "shortSummary") ?? c.Verdict;
                c.ExperienceSummary = FirstStr(sources, "experienceSummary");
                c.ExperienceYears = FirstNum(sources, "experienceYears");
                c.MatchPercent = FirstNum(sources, "matchPercent", "match");

                if (DateTime.TryParse(FirstStr(sources, "analyzedAt"), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    c.Date = dt.ToLocalTime();

                c.Error = FirstStr(sources, "error");
                if (!string.IsNullOrWhiteSpace(c.Error)) c.Status = AnalysisStatus.Failed;
            }
            catch (JsonException)
            {
                c.Status = AnalysisStatus.Failed;
                c.Error = "Не вдалося прочитати JSON з результатом аналізу.";
            }

            return c;
        }

        private static string? FirstStr(JsonElement[] sources, params string[] names)
        {
            foreach (var el in sources)
                foreach (var n in names)
                    if (el.ValueKind == JsonValueKind.Object &&
                        el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(p.GetString()))
                        return p.GetString();
            return null;
        }

        private static double? FirstNum(JsonElement[] sources, params string[] names)
        {
            foreach (var el in sources)
                foreach (var n in names)
                    if (el.ValueKind == JsonValueKind.Object &&
                        el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number &&
                        p.TryGetDouble(out var d))
                        return d;
            return null;
        }

        private static List<string> FirstList(JsonElement[] sources, params string[] names)
        {
            foreach (var el in sources)
                foreach (var n in names)
                {
                    if (el.ValueKind != JsonValueKind.Object ||
                        !el.TryGetProperty(n, out var p) || p.ValueKind != JsonValueKind.Array)
                        continue;

                    var list = new List<string>();
                    foreach (var item in p.EnumerateArray())
                    {
                        string? s = ItemToString(item);
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                    }
                    return list;
                }
            return new List<string>();
        }

        // елемент списку може бути рядком або об'єктом {requirement, evidence}
        private static string? ItemToString(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.String) return item.GetString();
            if (item.ValueKind != JsonValueKind.Object) return null;

            string? main = null, extra = null;
            foreach (var n in new[] { "requirement", "name", "title", "text", "skill" })
                if (item.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String) { main = p.GetString(); break; }
            foreach (var n in new[] { "evidence", "reason", "comment", "details" })
                if (item.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String) { extra = p.GetString(); break; }

            if (main == null) return null;
            return string.IsNullOrWhiteSpace(extra) ? main : $"{main} — {extra}";
        }

        // ===== Допоміжні =====

        private static string Norm(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            try { s = Uri.UnescapeDataString(s); } catch { }
            return s.Replace('\\', '/').Trim().ToLowerInvariant();
        }

        private static bool SameFile(string resumeKey, string? analysisResume)
        {
            string a = Norm(analysisResume);
            if (a == "") return false;
            string r = Norm(resumeKey);
            return a == r || Path.GetFileName(a) == Path.GetFileName(r);
        }

        private static string? ResumeIdFromKey(string key)
        {
            if (!key.StartsWith("Resumes/")) return null;
            string id = Path.GetFileNameWithoutExtension(key);
            return Guid.TryParseExact(id, "N", out _) ? id : null;
        }

        private static string? Meta(MetadataCollection m, string name)
        {
            string full = "x-amz-meta-" + name;
            if (!m.Keys.Contains(full)) return null;
            var v = m[full];
            return string.IsNullOrEmpty(v) ? null : Uri.UnescapeDataString(v);
        }
    }
}
