using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace SafeGuard.Models
{
    public class HomeController : Controller
    {
        // GET: Home
        public ActionResult Index()
        {
            return View();
        }

        // THÊM HÀM NÀY VÀO ĐỂ TẠO TRANG TÍNH NĂNG
        public ActionResult Features()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG LỢI ÍCH
        public ActionResult Benefits()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG TIN TỨC
        public ActionResult News()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG LIÊN HỆ
        // 1. Hiển thị trang Liên hệ
        [HttpGet]
        public ActionResult Contact()
        {
            return View();
        }

        // 2. Nhận dữ liệu từ AJAX và gửi mail
        [HttpPost]
        public async Task<JsonResult> SendContact(string name, string email, string phone, string subject, string message)
        {
            try
            {
                // TÀI KHOẢN DUY NHẤT VỪA LÀM NGƯỜI GỬI VỪA LÀM NGƯỜI NHẬN
                string myEmail = "adminsafeguard@gmail.com";

                // MẬT KHẨU ỨNG DỤNG CỦA GOOGLE (Bắt buộc)
                string myAppPassword = "ofgtexdtngkjtjep";

                var mail = new MailMessage(myEmail, myEmail);
                mail.Subject = "SAFEGUARD - LIÊN HỆ MỚI: " + subject;
                mail.IsBodyHtml = true;

                // Đóng gói nội dung HTML đẹp mắt
                mail.Body = $@"
                    <div style='font-family: Arial, sans-serif; border: 1px solid #ddd; padding: 20px; border-radius: 10px; max-width: 600px;'>
                        <h2 style='color: #0d6efd; border-bottom: 2px solid #0d6efd; padding-bottom: 10px;'>Yêu cầu hỗ trợ SafeGuard</h2>
                        <table style='width: 100%; border-collapse: collapse;'>
                            <tr><td style='width: 150px; padding: 5px;'><b>Họ tên khách:</b></td><td>{name}</td></tr>
                            <tr><td style='padding: 5px;'><b>Email liên hệ:</b></td><td>{email}</td></tr>
                            <tr><td style='padding: 5px;'><b>Số điện thoại:</b></td><td>{phone}</td></tr>
                            <tr><td style='padding: 5px;'><b>Chủ đề:</b></td><td>{subject}</td></tr>
                        </table>
                        <div style='margin-top: 20px; padding: 15px; background: #f8f9fa; border-left: 5px solid #ffc107;'>
                            <b>Nội dung tin nhắn:</b><br>{message}
                        </div>
                    </div>";

                // Giao thức gửi mail bằng SMTP của Gmail
                using (var smtp = new SmtpClient("smtp.gmail.com", 587))
                {
                    smtp.EnableSsl = true;
                    smtp.UseDefaultCredentials = false;
                    smtp.Credentials = new NetworkCredential(myEmail, myAppPassword);
                    await smtp.SendMailAsync(mail);
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        // THÊM HÀM NÀY CHO TRANG THIẾT BỊ
        public ActionResult Devices()
        {
            return View();
        }
        public ActionResult SafetyRegulations()
        {
            ViewBag.Message = "Quy định Phòng cháy Chữa cháy tại Nhà trọ SafeGuard";
            return View();
        }
    }

}