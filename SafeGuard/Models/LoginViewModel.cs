using System.ComponentModel.DataAnnotations;

namespace SafeGuard.Models
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Vui lòng nhập Username hoặc Email.")]
        [Display(Name = "Tài khoản/Email")]
        public string Username { get; set; } // Người dùng nhập cái nào cũng được

        [Required(ErrorMessage = "Vui lòng nhập mật khẩu.")]
        [DataType(DataType.Password)]
        [Display(Name = "Mật khẩu")]
        public string Password { get; set; }

        [Display(Name = "Ghi nhớ đăng nhập")]
        public bool RememberMe { get; set; }
    }
}