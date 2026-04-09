using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using SafeGuard.Filters;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;

namespace SafeGuard.Controllers
{
    // ==========================================
    // CLASS VIEW MODEL ĐÃ ĐƯỢC CHUYỂN RA NGOÀI ĐỂ TRÁNH LỖI GIAO DIỆN
    // ==========================================
    public class TenantAlertVM
    {
        public DateTime TimeStamp { get; set; }
        public double Temperature { get; set; }
        public string Description { get; set; }
        public string AlertType { get; set; } // "fire", "high_temp", "normal"
    }

    [RoleAuthorize(Role = "TENANT")]
    public class TenantController : Controller
    {
        public ActionResult Index() => View();
        public ActionResult NoiQuy() => View();
        public ActionResult ThongTinCaNhan() => View();
        public ActionResult PhanTichAI() => View();

        [HttpGet]
        public async Task<ActionResult> QuanLyPhong()
        {
            if (Session["UserEmail"] != null)
            {
                try
                {
                    var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                    var usersTable = Table.LoadTable(client, "Users");
                    var userItem = await usersTable.GetItemAsync(Session["UserEmail"].ToString());

                    if (userItem != null && userItem.ContainsKey("AssignedRoom"))
                    {
                        string currentRoom = userItem["AssignedRoom"].AsString();

                        if (!string.IsNullOrEmpty(currentRoom) && currentRoom.Contains("-"))
                        {
                            // ĐÃ SỬA: Tách "A-101" thành "A" và "101" để DynamoDB không bị lỗi văng Catch
                            var parts = currentRoom.Split('-');
                            string blockId = parts[0];
                            string roomId = parts[1];

                            var roomTable = Table.LoadTable(client, "Rooms");
                            var roomExist = await roomTable.GetItemAsync(blockId, roomId);

                            if (roomExist == null)
                            {
                                // Xóa Session và xóa cột trong DB
                                Session["AssignedRoom"] = null;
                                userItem.Remove("AssignedRoom");
                                await usersTable.UpdateItemAsync(userItem);

                                TempData["ErrorMessage"] = "Phòng của bạn đã bị Quản lý xóa khỏi hệ thống!";
                            }
                            else
                            {
                                Session["AssignedRoom"] = currentRoom;
                            }
                        }
                        else
                        {
                            Session["AssignedRoom"] = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Lỗi lấy/kiểm tra phòng: " + ex.Message);
                }
            }
            return View();
        }

        // ==========================================
        // LẤY LỊCH SỬ CẢNH BÁO TỪ DYNAMODB
        // ==========================================
        public async Task<ActionResult> LichSuCanhBao()
        {
            var alertList = new List<TenantAlertVM>();
            string myRoomId = Session["AssignedRoom"] != null ? Session["AssignedRoom"].ToString() : "Chưa liên kết";
            ViewBag.RoomId = myRoomId;

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "SafeDorm_History");

                var queryFilter = new QueryFilter("room_id", QueryOperator.Equal, myRoomId);
                var search = table.Query(queryFilter);
                var documentList = await search.GetNextSetAsync();

                foreach (var doc in documentList)
                {
                    double temp = doc["temperature"].AsDouble();
                    DateTime timeStamp;

                    if (doc.ContainsKey("timestamp"))
                    {
                        string timeStr = doc["timestamp"].AsString();
                        if (timeStr.Contains("-"))
                        {
                            DateTime.TryParse(timeStr, out timeStamp);
                        }
                        else
                        {
                            long unixTime = long.Parse(timeStr);
                            timeStamp = DateTimeOffset.FromUnixTimeSeconds(unixTime).ToLocalTime().DateTime;
                        }
                    }
                    else
                    {
                        timeStamp = DateTime.Now;
                    }

                    string type = "normal";
                    string desc = "Mức nhiệt độ phòng ổn định, cảm biến hoạt động tốt.";

                    if (temp >= 50)
                    {
                        type = "fire";
                        desc = "Cảnh báo nguy cơ cháy: Nhiệt độ tăng đột biến cực cao!";
                    }
                    else if (temp >= 38)
                    {
                        type = "high_temp";
                        desc = $"Nhiệt độ tăng cao bất thường ({temp}°C).";
                    }

                    alertList.Add(new TenantAlertVM { TimeStamp = timeStamp, Temperature = temp, Description = desc, AlertType = type });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi lấy dữ liệu Tenant: " + ex.Message);
            }

            var sortedList = alertList.OrderByDescending(x => x.TimeStamp).ToList();
            return View(sortedList);
        }

        // ==========================================
        // NGƯỜI THUÊ NHẬP MÃ THAM GIA PHÒNG
        // ==========================================
        // ==========================================
        // NGƯỜI THUÊ NHẬP MÃ THAM GIA PHÒNG (DÙNG AJAX)
        // ==========================================
        [HttpPost]
        public async Task<JsonResult> XacNhanMaPhong(string inviteCode)
        {
            if (string.IsNullOrEmpty(inviteCode))
                return Json(new { success = false, message = "Vui lòng nhập mã phòng!" });

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var inviteTable = Table.LoadTable(client, "RoomInvites");
                var inviteItem = await inviteTable.GetItemAsync(inviteCode.Trim().ToUpper());

                if (inviteItem == null)
                    return Json(new { success = false, message = "Mã không tồn tại hoặc bạn đã nhập sai!" });

                if (inviteItem["IsUsed"].AsBoolean())
                    return Json(new { success = false, message = "Mã này đã được sử dụng bởi một người khác!" });

                if (inviteItem.ContainsKey("CreatedAt") && inviteItem.ContainsKey("ExpireHours"))
                {
                    DateTime createdAt = DateTime.Parse(inviteItem["CreatedAt"].AsString());
                    int expireHours = inviteItem["ExpireHours"].AsInt();
                    if (DateTime.UtcNow > createdAt.AddHours(expireHours))
                        return Json(new { success = false, message = "Mã kích hoạt này đã hết hạn sử dụng!" });
                }

                string roomId = inviteItem["RoomId"].AsString();
                inviteItem["IsUsed"] = true;
                await inviteTable.UpdateItemAsync(inviteItem);

                if (Session["UserEmail"] != null)
                {
                    string userEmail = Session["UserEmail"].ToString();
                    var usersTable = Table.LoadTable(client, "Users");

                    var userDoc = new Document();
                    userDoc["userID"] = userEmail;
                    userDoc["AssignedRoom"] = roomId;
                    await usersTable.UpdateItemAsync(userDoc);

                    Session["AssignedRoom"] = roomId;
                }

                // Trả về JSON thành công thay vì Redirect
                return Json(new { success = true, message = $"Chúc mừng! Bạn đã gia nhập phòng {roomId} thành công." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        // ==========================================
        // HÀM TẠO DỮ LIỆU GIẢ ĐỂ TEST BIỂU ĐỒ VÀ AI
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> TaoDuLieuGia()
        {
            string roomId = Session["AssignedRoom"] != null ? Session["AssignedRoom"].ToString() : "C-107";
            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "SafeDorm_History");

                // Tạo dữ liệu cho 7 ngày gần nhất
                for (int i = 6; i >= 0; i--)
                {
                    var doc = new Document();
                    doc["room_id"] = roomId;
                    doc["timestamp"] = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd HH:mm:ss");

                    Random rnd = new Random();
                    doc["temperature"] = rnd.Next(25, 32);

                    // Cố tình đẩy nhiệt độ hôm nay lên 45 độ để test AI Báo cháy
                    if (i == 0) doc["temperature"] = 45;

                    await table.PutItemAsync(doc);
                }
                return Content($"Đã tạo thành công 7 ngày dữ liệu giả cho phòng {roomId}! Hãy quay lại trang Phân Tích AI.");
            }
            catch (Exception ex) { return Content("Lỗi: " + ex.Message); }
        }

        // ==========================================
        // HÀM GỌI LÊN AWS LAMBDA (REAL-TIME 100%, KHÔNG CACHE)
        // ==========================================
        private async Task<(string message, string advice)> GetGeminiAIAnalysisRealTime(List<double> recentTemps)
        {
            // THAY LINK NÀY BẰNG LINK API GATEWAY CỦA BẠN:
            string awsApiGatewayUrl = "https://ĐIỀN_LINK_API_GATEWAY_CỦA_BẠN_VÀO_ĐÂY";

            string aiMessage = "Nhiệt độ phòng bạn rất ổn định.";
            string aiAdvice = "Mẹo AI: Hãy tắt bớt thiết bị điện khi ra ngoài.";

            try
            {
                using (var httpClient = new HttpClient())
                {
                    var payload = new { recentTemps = recentTemps };
                    var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(awsApiGatewayUrl, content);

                    // Bắt buộc văng lỗi nếu API Gateway sập hoặc link sai
                    response.EnsureSuccessStatusCode();

                    var responseString = await response.Content.ReadAsStringAsync();
                    dynamic json = JsonConvert.DeserializeObject(responseString);

                    aiMessage = json.message;
                    aiAdvice = json.advice;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi gọi AI Real-time: " + ex.Message);

                // Fallback nếu rớt mạng
                double avgTemp = recentTemps.Any() ? recentTemps.Average() : 0;
                if (avgTemp >= 38)
                {
                    aiMessage = "Nhiệt độ đang NÓNG BẤT THƯỜNG.";
                    aiAdvice = "Mẹo AI: Kiểm tra thiết bị điện ngay lập tức.";
                }
            }

            return (aiMessage, aiAdvice);
        }

        // ==========================================
        // API: LẤY DỮ LIỆU BIỂU ĐỒ VÀ GỌI AI PHÂN TÍCH
        // ==========================================
        [HttpGet]
        public async Task<JsonResult> GetRoomChartData()
        {
            string myRoomId = Session["AssignedRoom"] != null ? Session["AssignedRoom"].ToString() : "";

            if (string.IsNullOrEmpty(myRoomId))
            {
                return Json(new { success = false, message = "Chưa có phòng" }, JsonRequestBehavior.AllowGet);
            }

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "SafeDorm_History");

                var queryFilter = new QueryFilter("room_id", QueryOperator.Equal, myRoomId);
                var search = table.Query(queryFilter);
                var documentList = await search.GetNextSetAsync();

                var logs = new List<dynamic>();
                foreach (var doc in documentList)
                {
                    DateTime timeStamp = DateTime.Now;
                    if (doc.ContainsKey("timestamp"))
                    {
                        string timeStr = doc["timestamp"].AsString();
                        if (timeStr.Contains("-")) DateTime.TryParse(timeStr, out timeStamp);
                        else timeStamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timeStr)).ToLocalTime().DateTime;
                    }

                    logs.Add(new { Time = timeStamp, Temp = doc["temperature"].AsDouble() });
                }

                var chartData = logs.OrderByDescending(x => x.Time).Take(7).OrderBy(x => x.Time).Select(x => new
                {
                    Label = x.Time.ToString("HH:mm dd/MM"),
                    Value = x.Temp
                }).ToList();

                // Ép kiểu double và list
                var listTemps = chartData.Select(x => (double)x.Value).ToList();
                string aiMessage = "Chưa có đủ dữ liệu cảm biến để AI phân tích.";
                string aiAdvice = "Hệ thống đang chờ cảm biến gửi tín hiệu đầu tiên...";

                // Gọi AI Lambda Real-time
                if (listTemps.Count > 0)
                {
                    var geminiResult = await GetGeminiAIAnalysisRealTime(listTemps);
                    aiMessage = geminiResult.message;
                    aiAdvice = geminiResult.advice;
                }

                return Json(new
                {
                    success = true,
                    labels = chartData.Select(c => c.Label),
                    values = chartData.Select(c => c.Value),
                    aiMessage = aiMessage,
                    aiAdvice = aiAdvice
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }
    }
}