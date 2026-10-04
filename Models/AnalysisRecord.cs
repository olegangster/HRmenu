namespace TZApp.Models
{
    public class AnalysisRecord
    {
        public string Key { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? TaskKey { get; set; }
        public string? ResumeFile { get; set; }
        public string? CandidateName { get; set; }

        public string? Error { get; set; }

        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public List<string> Skills { get; set; } = new();
        public double? ExperienceYears { get; set; }
        public string? ExperienceSummary { get; set; }
        public double? MatchPercent { get; set; }
        public List<string> Strengths { get; set; } = new();
        public List<string> Gaps { get; set; } = new();
        public string? Verdict { get; set; }

        public string RawJson { get; set; } = "";
    }
}