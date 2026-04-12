using SafeGuard.Models;
using SafeGuard.Services;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace SafeGuard.Controllers
{
    public class ChatController : Controller
    {
        private readonly ChatbotService _chatbotService = new ChatbotService();

        [HttpPost]
        public async Task<JsonResult> Ask(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return Json(new { success = false, reply = "Vui lòng nhập câu hỏi." });
            }

            // Lấy role của người dùng hiện tại (nếu có đăng nhập)
            string role = Session["Role"] != null ? Session["Role"].ToString() : "guest";

            var result = await _chatbotService.AskAsync(new ChatRequestViewModel
            {
                Question = question,
                Role = role
            });

            return Json(new
            {
                success = result.Success,
                reply = result.Reply,
                source = result.Source
            });
        }
    }
}