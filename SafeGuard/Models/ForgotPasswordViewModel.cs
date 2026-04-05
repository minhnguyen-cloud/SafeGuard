using System.ComponentModel.DataAnnotations;

namespace SafeGuard.Models
{
    public class ForgotPasswordViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập Email để nhận mã khôi phục.")]
        [EmailAddress]
        public string Email { get; set; }
    }
}