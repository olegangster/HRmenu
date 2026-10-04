using Microsoft.AspNetCore.Mvc;
using TZApp.Models;
using TZApp.Services;

namespace TZApp.Controllers
{
    public class AnalyticsController : Controller
    {
        private readonly IDashboardService _dashboard;
        private readonly ILogger<AnalyticsController> _logger;

        public AnalyticsController(IDashboardService dashboard, ILogger<AnalyticsController> logger)
        {
            _dashboard = dashboard;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(CancellationToken ct)
        {
            try
            {
                var all = await _dashboard.LoadAllAsync(ct);
                return View(AnalyticsViewModel.From(all));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося завантажити HR Analytics");
                return View(new AnalyticsViewModel { Error = "Не вдалося завантажити дані з S3: " + ex.Message });
            }
        }
    }
}
