using System.ComponentModel.DataAnnotations;

namespace TZApp.Models
{
    public class ResumeUploadViewModel
    {
        [Required(ErrorMessage = "Вкажіть ім'я")]
        [StringLength(100, ErrorMessage = "Максимум 100 символів")]
        [Display(Name = "Ім'я та прізвище")]
        public string CandidateName { get; set; } = "";

        [Required(ErrorMessage = "Вкажіть email")]
        [EmailAddress(ErrorMessage = "Некоректний email")]
        [StringLength(150, ErrorMessage = "Максимум 150 символів")]
        [Display(Name = "Email")]
        public string Email { get; set; } = "";

        [Phone(ErrorMessage = "Некоректний номер телефону")]
        [StringLength(30, ErrorMessage = "Максимум 30 символів")]
        [Display(Name = "Телефон")]
        public string? Phone { get; set; }

        [Display(Name = "Резюме (PDF)")]
        public IFormFile? ResumeFile { get; set; }
    }

    public class ResumeItem
    {
        public string Id { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public string Contact { get; set; } = "";
        public string FileName { get; set; } = "";
        public DateTime UploadedAt { get; set; }
        public long Size { get; set; }
    }
}
