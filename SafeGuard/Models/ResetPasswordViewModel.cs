using System.ComponentModel.DataAnnotations;

namespace SafeGuard.Models
{
    public class ResetPasswordViewModel
    {
        [Required]
        public string Email { get; set; } // Thực chất nó đang chứa Username hoặc Email

        [Required(ErrorMessage = "Vui lòng nhập mã từ email")]
        public string Code { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới")]
        [MinLength(8, ErrorMessage = "Mật khẩu phải từ 8 ký tự")]
        public string NewPassword { get; set; }

        [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu")]
        [Compare("NewPassword", ErrorMessage = "Mật khẩu xác nhận không khớp!")]
        public string ConfirmPassword { get; set; }
    }
}