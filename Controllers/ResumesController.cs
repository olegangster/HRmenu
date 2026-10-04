using Microsoft.AspNetCore.Mvc;
using TZApp.Models;
using TZApp.Services;

namespace TZApp.Controllers
{
    public class ResumesController : Controller
    {
        private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

        private readonly IResumeStorageService _storage;
        private readonly ILogger<ResumesController> _logger;

        public ResumesController(IResumeStorageService storage, ILogger<ResumesController> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        // ===== Сторінка кандидата =====

        [HttpGet]
        public IActionResult Upload() => View(new ResumeUploadViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxFileSize + 1024 * 1024)]
        public async Task<IActionResult> Upload(ResumeUploadViewModel model, CancellationToken ct)
        {
            var file = model.ResumeFile;

            if (file == null || file.Length == 0)
            {
                ModelState.AddModelError(nameof(model.ResumeFile), "Оберіть PDF файл резюме");
            }
            else
            {
                if (file.Length > MaxFileSize)
                    ModelState.AddModelError(nameof(model.ResumeFile), "Файл завеликий (максимум 10 МБ)");

                if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                    ModelState.AddModelError(nameof(model.ResumeFile), "Дозволені лише файли у форматі PDF");
            }

            if (!ModelState.IsValid)
                return View(model);

            try
            {
                // Файл йде в S3 напряму зі Stream — без збереження на сервері
                await using var stream = file!.OpenReadStream();

                if (!await HasPdfSignatureAsync(stream, ct))
                {
                    ModelState.AddModelError(nameof(model.ResumeFile), "Файл не є коректним PDF");
                    return View(model);
                }

                string contact = string.IsNullOrWhiteSpace(model.Phone)
                    ? model.Email.Trim()
                    : $"{model.Email.Trim()}; {model.Phone.Trim()}";

                await _storage.UploadAsync(stream, model.CandidateName.Trim(), contact,
                    Path.GetFileName(file.FileName), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося завантажити резюме в S3");
                ModelState.AddModelError("", "Не вдалося надіслати резюме. Спробуйте пізніше.");
                return View(model);
            }

            TempData["Success"] = "Дякуємо! Ваше резюме успішно надіслано.";
            return RedirectToAction(nameof(Upload));
        }

        // ===== Сторінка HR =====

        [HttpGet]
        public async Task<IActionResult> List(CancellationToken ct)
        {
            try
            {
                var items = await _storage.ListAsync(ct);
                return View(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося отримати список резюме");
                ViewBag.Error = "Не вдалося завантажити список резюме з S3: " + ex.Message;
                return View(new List<ResumeItem>());
            }
        }

        [HttpGet]
        public Task<IActionResult> Open(string id, CancellationToken ct) => RedirectToPresigned(id, false, ct);

        [HttpGet]
        public Task<IActionResult> Download(string id, CancellationToken ct) => RedirectToPresigned(id, true, ct);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id, CancellationToken ct)
        {
            try
            {
                if (await _storage.DeleteAsync(id, ct))
                    TempData["Success"] = "Резюме видалено з S3.";
                else
                    TempData["Error"] = "Некоректний ідентифікатор резюме.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося видалити резюме {Id}", id);
                TempData["Error"] = "Не вдалося видалити резюме: " + ex.Message;
            }

            return RedirectToAction(nameof(List));
        }

        // ===== Допоміжні методи =====

        private async Task<IActionResult> RedirectToPresigned(string id, bool download, CancellationToken ct)
        {
            string? url = await _storage.GetPresignedUrlAsync(id, download, ct);
            return url == null ? NotFound() : Redirect(url);
        }

        private static async Task<bool> HasPdfSignatureAsync(Stream stream, CancellationToken ct)
        {
            var header = new byte[5];
            int read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            stream.Seek(0, SeekOrigin.Begin);

            return read == 5 && header[0] == '%' && header[1] == 'P' && header[2] == 'D'
                && header[3] == 'F' && header[4] == '-';
        }
    }
}
