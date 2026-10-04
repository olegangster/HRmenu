namespace TZApp.Models
{
    public class SkillStat
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    public class AnalyticsViewModel
    {
        public string? Error { get; set; }

        public List<CandidateAnalysis> Table { get; set; } = new();   
        public List<CandidateAnalysis> ByMatch { get; set; } = new(); 
        public List<SkillStat> TopSkills { get; set; } = new();
        public List<SkillStat> TopMissing { get; set; } = new();

        public int Total { get; set; }
        public int Done { get; set; }
        public int Pending { get; set; }
        public int Failed { get; set; }
        public int HighMatch { get; set; }          // >= 75%
        public double? AvgMatch { get; set; }
        public double? AvgExperience { get; set; }

        public static AnalyticsViewModel From(List<CandidateAnalysis> all)
        {
            var done = all.Where(c => c.Status == AnalysisStatus.Done).ToList();
            var withMatch = done.Where(c => c.MatchPercent.HasValue).ToList();
            var withExp = done.Where(c => c.ExperienceYears.HasValue).ToList();

            var vm = new AnalyticsViewModel
            {
                Total = all.Count,
                Done = done.Count,
                Pending = all.Count(c => c.Status == AnalysisStatus.Pending),
                Failed = all.Count(c => c.Status == AnalysisStatus.Failed),
                HighMatch = withMatch.Count(c => c.MatchPercent!.Value >= 75),
                AvgMatch = withMatch.Count == 0 ? null : withMatch.Average(c => c.MatchPercent!.Value),
                AvgExperience = withExp.Count == 0 ? null : withExp.Average(c => c.ExperienceYears!.Value),
                ByMatch = withMatch.OrderByDescending(c => c.MatchPercent).Take(10).ToList(),
                TopSkills = Count(done.Select(c => c.Skills), 8),
                TopMissing = Count(done.Select(c => c.Missing), 5)
            };

            vm.Table = done.OrderByDescending(c => c.MatchPercent ?? -1)
                .Concat(all.Where(c => c.Status == AnalysisStatus.Pending).OrderByDescending(c => c.Date))
                .Concat(all.Where(c => c.Status == AnalysisStatus.Failed).OrderByDescending(c => c.Date))
                .ToList();

            return vm;
        }

        private static List<SkillStat> Count(IEnumerable<List<string>> lists, int take)
        {
            var map = new Dictionary<string, SkillStat>();
            foreach (var list in lists)
            {
                foreach (var raw in list.Select(s => s.Trim()).Where(s => s.Length > 0)
                             .GroupBy(s => s.ToLowerInvariant()).Select(g => g.First()))
                {
                    string key = raw.ToLowerInvariant();
                    if (map.TryGetValue(key, out var stat)) stat.Count++;
                    else map[key] = new SkillStat { Name = raw, Count = 1 };
                }
            }

            return map.Values.OrderByDescending(s => s.Count).ThenBy(s => s.Name).Take(take).ToList();
        }
    }
}
