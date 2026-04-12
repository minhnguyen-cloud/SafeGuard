using System.ComponentModel.DataAnnotations;

namespace SafeGuard.Models
{
    public class ConfirmSignUpViewModel
    {
        [Required]
        public string Username { get; set; } // Cái này để AWS dùng

        public string DisplayEmail { get; set; } // Cái này để hiện ra màn hình cho đẹp

        [Required(ErrorMessage = "Vui lòng nhập mã xác nhận")]
        public string Code { get; set; }
        public string AdminCode { get; set; }
    }
}