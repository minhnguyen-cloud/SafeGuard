using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace SafeGuard.Controllers
{
    public class ApiController : Controller
    {
        // 1. DÁN API KEY CỦA BẠN VÀO ĐÂY (Trong thực tế nên giấu vào Web.config)
        private const string GeminiApiKey = "AIzaSyB7uFZmkf0nkx-MYVkdleL4I0NijMPzZWw";   
        // ✅ MỚI - dùng gemini-2.0-flash (miễn phí, nhanh)
        private const string GeminiApiUrl =
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + GeminiApiKey; public async Task<JsonResult> AskGemini(string question)
        {
            if (string.IsNullOrEmpty(question))
            {
                return Json(new { success = false, reply = "Hỏi gì đi chứ Trí!" });
            }

            try
            {
                using (var client = new HttpClient())
                {
                    // 3. Cấu trúc dữ liệu yêu cầu theo chuẩn của Google Gemini API
                    var payload = new
                    {
                        contents = new[]
                        {
                            new { parts = new[] { new { text = question } } }
                        }
                    };

                    var jsonPayload = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // 4. Gọi API lên Google
                    var response = await client.PostAsync(GeminiApiUrl, content);
                    var jsonResponse = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        // 5. Phân tích kết quả JSON trả về để lấy câu trả lời
                        dynamic result = JsonConvert.DeserializeObject(jsonResponse);
                        string apiReply = result.candidates[0].content.parts[0].text;

                        return Json(new { success = true, reply = apiReply });
                    }
                    // SỬA LẠI ĐOẠN ELSE THÀNH CODE NÀY:
                    else
                    {
                        // Đọc thẳng nguyên nhân chi tiết mà Google báo lỗi (biến jsonResponse đã có sẵn ở trên)
                        return Json(new { success = false, reply = "Chi tiết lỗi từ Google: " + jsonResponse });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, reply = "Sự cố hệ thống: " + ex.Message });
            }
        }
    }
}