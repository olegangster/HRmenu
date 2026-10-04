using Microsoft.AspNetCore.Mvc;
using TZApp.Models;
using TZApp.Services;

namespace TZApp.Controllers
{
    public class DashboardController : Controller
    {
        private readonly IDashboardService _dashboard;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(IDashboardService dashboard, ILogger<DashboardController> logger)
        {
            _dashboard = dashboard;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(CancellationToken ct)
        {
            try
            {
                var list = await _dashboard.LoadAllAsync(ct);
                return View(new DashboardViewModel { Candidates = list });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося завантажити HR Dashboard");
                return View(new DashboardViewModel { Error = "Не вдалося завантажити дані з S3: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Details(string key, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key)) return RedirectToAction(nameof(Index));

            try
            {
                var candidate = await _dashboard.GetAsync(key, ct);
                return candidate == null ? NotFound() : View(candidate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося завантажити деталі {Key}", key);
                return StatusCode(500, "Не вдалося завантажити дані з S3.");
            }
        }
    }
}
