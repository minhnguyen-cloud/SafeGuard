using Newtonsoft.Json;
using SafeGuard.Models;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SafeGuard.Services
{
    public class ChatbotService
    {
        // DÁN URL CỦA BẠN VÀO ĐÂY (Nhớ giữ lại phần /chat/ask ở đuôi)
        private readonly string _apiGatewayUrl = "https://lpj8gmhldh.execute-api.ap-southeast-1.amazonaws.com/chat/ask";

        public async Task<ChatResponseViewModel> AskAsync(ChatRequestViewModel request)
        {
            using (var client = new HttpClient())
            {
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Bắn dữ liệu lên AWS
                var response = await client.PostAsync(_apiGatewayUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new ChatResponseViewModel
                    {
                        Success = false,
                        Reply = "Không thể kết nối với máy chủ AI: " + responseBody
                    };
                }

                return JsonConvert.DeserializeObject<ChatResponseViewModel>(responseBody);
            }
        }
    }
}