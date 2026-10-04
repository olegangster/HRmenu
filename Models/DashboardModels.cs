namespace TZApp.Models
{
    public static class AnalysisStatus
    {
        public const string Done = "Проаналізовано";
        public const string Pending = "Очікує аналізу";
        public const string Failed = "Помилка аналізу";
    }

    public class CandidateAnalysis
    {
        public string Id { get; set; } = "";           
        public string? AnalysisKey { get; set; }
        public string? ResumeKey { get; set; } 
        public string? ResumeId { get; set; }

        public string Name { get; set; } = "Невідомий кандидат";
        public string Status { get; set; } = AnalysisStatus.Pending;
        public string? Error { get; set; }

        public string? TaskKey { get; set; }
        public string? ResumeFile { get; set; }
        public DateTime Date { get; set; }

        public List<string> Skills { get; set; } = new();
        public List<string> Confirmed { get; set; } = new();
        public List<string> Missing { get; set; } = new();
        public List<string> Strengths { get; set; } = new();
        public List<string> Gaps { get; set; } = new();

        public string? Summary { get; set; }
        public string? Verdict { get; set; }
        public string? ExperienceSummary { get; set; }
        public double? ExperienceYears { get; set; }
        public double? MatchPercent { get; set; }

        public string RawJson { get; set; } = "";
    }

    public class DashboardViewModel
    {
        public List<CandidateAnalysis> Candidates { get; set; } = new();
        public string? Error { get; set; }

        public int Total => Candidates.Count;
        public int Done => Candidates.Count(c => c.Status == AnalysisStatus.Done);
        public int Pending => Candidates.Count(c => c.Status == AnalysisStatus.Pending);
        public int Failed => Candidates.Count(c => c.Status == AnalysisStatus.Failed);
        public double? AvgMatch
        {
            get
            {
                var v = Candidates.Where(c => c.Status == AnalysisStatus.Done && c.MatchPercent.HasValue)
                                  .Select(c => c.MatchPercent!.Value).ToList();
                return v.Count == 0 ? null : v.Average();
            }
        }
    }
}
