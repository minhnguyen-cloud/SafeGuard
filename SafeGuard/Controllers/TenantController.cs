using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Web;
using SafeGuard.Filters;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;
using Amazon.S3;
using Amazon.S3.Transfer;
using System.IO;
using System.Configuration;
using Amazon.Runtime;

namespace SafeGuard.Controllers
{
    // ==========================================
    // CÁC CLASS VIEW MODEL 
    // ==========================================
    public class TenantAlertVM
    {
        public DateTime TimeStamp { get; set; }
        public double Temperature { get; set; }
        public string Description { get; set; }
        public string AlertType { get; set; } // "fire", "high_temp", "normal"
    }
    public class TenantDashboardVM
    {
        public string RoomName { get; set; }
        public string BlockName { get; set; }
        public double? CurrentTemperature { get; set; }
        public string LastUpdated { get; set; }
        public string StatusMessage { get; set; }
        public bool IsAlert { get; set; }
        public bool HasData { get; set; }
    }

    public class TenantProfileVM
    {
        public string UserID { get; set; }
        public string Username { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string AssignedRoom { get; set; }
        public string AvatarUrl { get; set; }
        public string BlockName { get; set; }
        public string Address { get; set; }
    }

    [RoleAuthorize(Role = "TENANT")]
    public class TenantController : Controller
    {
        [HttpGet]
        public async Task<ActionResult> Index()
        {
            var model = new TenantDashboardVM
            {
                RoomName = "Chưa có",
                BlockName = "N/A",
                HasData = false,
                StatusMessage = "Hệ thống đang chờ kết nối...",
                LastUpdated = "Chưa có dữ liệu"
            };

            if (Session["AssignedRoom"] != null)
            {
                string roomId = Session["AssignedRoom"].ToString();

                if (roomId.Contains("-"))
                {
                    model.RoomName = "Phòng " + roomId.Split('-')[1];
                    model.BlockName = "Dãy " + roomId.Split('-')[0];
                }
                else
                {
                    model.RoomName = "Phòng " + roomId;
                }

                try
                {
                    var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                    var table = Table.LoadTable(client, "SafeDorm_History");

                    var filter = new QueryFilter("room_id", QueryOperator.Equal, roomId);
                    var search = table.Query(filter);
                    var docs = await search.GetNextSetAsync();

                    if (docs.Count > 0)
                    {
                        // Lấy record mới nhất dựa trên thời gian
                        var latestDoc = docs.OrderByDescending(d => d.ContainsKey("timestamp") ? d["timestamp"].AsString() : "").First();

                        model.CurrentTemperature = latestDoc["temperature"].AsDouble();
                        model.HasData = true;

                        // Xử lý chuỗi thời gian (Tránh lỗi do dữ liệu giả và thật khác format)
                        DateTime timeStamp;
                        string timeStr = latestDoc["timestamp"].AsString();
                        if (timeStr.Contains("-"))
                        {
                            DateTime.TryParse(timeStr, out timeStamp);
                        }
                        else
                        {
                            timeStamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timeStr)).ToLocalTime().DateTime;
                        }

                        // Nếu vừa cập nhật trong vòng 5 phút, hiện "Vừa xong", ngược lại hiện giờ cụ thể
                        TimeSpan diff = DateTime.Now - timeStamp;
                        model.LastUpdated = diff.TotalMinutes <= 5 ? "Vừa xong" : timeStamp.ToString("HH:mm - dd/MM");

                        // XỬ LÝ ĐỔI MÀU CẢNH BÁO
                        if (model.CurrentTemperature >= 45)
                        {
                            model.IsAlert = true;
                            model.StatusMessage = "NGUY HIỂM: Nhiệt độ tăng cao bất thường! Vui lòng kiểm tra thiết bị trong phòng ngay.";
                        }
                        else if (model.CurrentTemperature >= 38)
                        {
                            model.IsAlert = true;
                            model.StatusMessage = "CẢNH BÁO: Nhiệt độ đang ở mức cao. Hệ thống AI khuyên bạn chú ý.";
                        }
                        else
                        {
                            model.IsAlert = false;
                            model.StatusMessage = "Hệ thống cảm biến nhiệt độ tại phòng bạn đang hoạt động bình thường và ổn định.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Lỗi lấy nhiệt độ Tenant: " + ex.Message);
                }
            }
            return View(model);
        }
        public ActionResult NoiQuy() => View();
        public ActionResult PhanTichAI() => View();

        // =========================================================
        // HÀM RADAR: TỰ ĐỘNG TÌM ID THẬT CỦA USER ĐỂ TRÁNH TẠO TÀI KHOẢN MA
        // =========================================================
        private async Task<string> GetRealUserIdAsync(AmazonDynamoDBClient client, string sessionValue)
        {
            var usersTable = Table.LoadTable(client, "Users");

            // 1. Thử tìm xem có ai có Khóa chính trùng với Session không (Hợp lệ nếu Session lưu đúng UUID)
            var user = await usersTable.GetItemAsync(sessionValue);
            if (user != null && user.ContainsKey("role")) return sessionValue;

            // 2. Nếu không có, quét bảng tìm ai có username == sessionValue (Để lấy UUID thật)
            var filter = new ScanFilter();
            filter.AddCondition("username", ScanOperator.Equal, sessionValue);
            var search = usersTable.Scan(filter);
            var results = await search.GetNextSetAsync();
            if (results.Count > 0) return results[0]["userID"].AsString();

            // 3. Quét thêm trường hợp đăng nhập bằng email
            var filterEmail = new ScanFilter();
            filterEmail.AddCondition("email", ScanOperator.Equal, sessionValue);
            var searchEmail = usersTable.Scan(filterEmail);
            var resultsEmail = await searchEmail.GetNextSetAsync();
            if (resultsEmail.Count > 0) return resultsEmail[0]["userID"].AsString();

            return sessionValue; // Trả về mặc định nếu không tìm thấy
        }

        // ==========================================
        // 1. TRANG THÔNG TIN CÁ NHÂN & CẬP NHẬT S3
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> ThongTinCaNhan()
        {
            var profile = new TenantProfileVM();
            if (Session["UserEmail"] != null)
            {
                try
                {
                    var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                    // SỬ DỤNG RADAR ĐỂ TÌM ID THẬT
                    string realUserId = await GetRealUserIdAsync(client, Session["UserEmail"].ToString());
                    var table = Table.LoadTable(client, "Users");
                    var user = await table.GetItemAsync(realUserId);

                    if (user != null)
                    {
                        profile.UserID = user.ContainsKey("userID") ? user["userID"].AsString() : realUserId;
                        profile.Username = user.ContainsKey("username") ? user["username"].AsString() : Session["UserEmail"].ToString().Split('@')[0];
                        profile.FullName = user.ContainsKey("fullName") ? user["fullName"].AsString() : profile.Username;

                        profile.Email = user.ContainsKey("email") ? user["email"].AsString() : "";
                        profile.Phone = user.ContainsKey("phone") ? user["phone"].AsString() : "";
                        profile.AssignedRoom = user.ContainsKey("AssignedRoom") && !string.IsNullOrEmpty(user["AssignedRoom"].AsString())
                                               ? user["AssignedRoom"].AsString() : "Chưa có";
                        profile.AvatarUrl = user.ContainsKey("AvatarUrl") ? user["AvatarUrl"].AsString()
                                            : $"https://ui-avatars.com/api/?name={profile.FullName}&background=dc3545&color=fff&size=200";

                        // TỰ ĐỘNG TÌM ĐỊA CHỈ KHU TRỌ
                        profile.BlockName = "Chưa xác định";
                        profile.Address = "Chưa có dữ liệu địa chỉ";

                        if (profile.AssignedRoom != "Chưa có" && profile.AssignedRoom.Contains("-"))
                        {
                            string blockId = profile.AssignedRoom.Split('-')[0];
                            var facTable = Table.LoadTable(client, "Facilities");
                            var filter = new ScanFilter();
                            filter.AddCondition("PK", ScanOperator.Equal, "BLOCK");
                            filter.AddCondition("BlockId", ScanOperator.Equal, blockId);
                            var search = facTable.Scan(filter);
                            var facList = await search.GetRemainingAsync();

                            if (facList.Count > 0)
                            {
                                profile.BlockName = facList[0].ContainsKey("BlockName") ? facList[0]["BlockName"].AsString() : "Khu " + blockId;
                                profile.Address = facList[0].ContainsKey("Address") ? facList[0]["Address"].AsString() : "Chưa cập nhật địa chỉ";
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Lỗi lấy Profile: " + ex.Message); }
            }
            return View(profile);
        }

        [HttpPost]
        public async Task<JsonResult> CapNhatThongTin(string fullName, string phone, string emailInput, HttpPostedFileBase avatarFile)
        {
            if (Session["UserEmail"] == null) return Json(new { success = false, message = "Vui lòng đăng nhập lại!" });

            string s3AvatarUrl = null;

            try
            {
                var dbClient = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                // SỬ DỤNG RADAR ĐỂ TÌM ID THẬT
                string realUserId = await GetRealUserIdAsync(dbClient, Session["UserEmail"].ToString());

                // UPLOAD ẢNH S3
                if (avatarFile != null && avatarFile.ContentLength > 0)
                {
                    string accessKey = ConfigurationManager.AppSettings["AWS_ACCESS_KEY"];
                    string secretKey = ConfigurationManager.AppSettings["AWS_SECRET_KEY"];
                    string bucketName = ConfigurationManager.AppSettings["AWS_S3_BUCKET"];
                    string fileName = $"avatars/{realUserId}_{DateTime.Now.Ticks}{Path.GetExtension(avatarFile.FileName)}";

                    var credentials = new BasicAWSCredentials(accessKey, secretKey);
                    using (var s3Client = new AmazonS3Client(credentials, Amazon.RegionEndpoint.APSoutheast1))
                    {
                        var transferUtility = new TransferUtility(s3Client);
                        using (var stream = avatarFile.InputStream)
                        {
                            var uploadRequest = new TransferUtilityUploadRequest
                            {
                                InputStream = stream,
                                Key = fileName,
                                BucketName = bucketName,
                                CannedACL = S3CannedACL.PublicRead
                            };
                            await transferUtility.UploadAsync(uploadRequest);
                        }
                        s3AvatarUrl = $"https://{bucketName}.s3.ap-southeast-1.amazonaws.com/{fileName}";
                    }
                }

                // CẬP NHẬT DYNAMODB (VÀO ĐÚNG TÀI KHOẢN GỐC)
                var table = Table.LoadTable(dbClient, "Users");
                var user = await table.GetItemAsync(realUserId);

                if (user != null)
                {
                    user["fullName"] = fullName;
                    user["phone"] = phone;
                    if (!string.IsNullOrEmpty(emailInput)) user["email"] = emailInput;
                    if (s3AvatarUrl != null) user["AvatarUrl"] = s3AvatarUrl;

                    await table.UpdateItemAsync(user);
                    return Json(new { success = true, message = "Cập nhật thông tin thành công!", newAvatar = s3AvatarUrl });
                }
                return Json(new { success = false, message = "Không tìm thấy user trong hệ thống." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        // ==========================================
        // 2. QUẢN LÝ PHÒNG 
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> QuanLyPhong()
        {
            if (Session["UserEmail"] != null)
            {
                try
                {
                    var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                    string realUserId = await GetRealUserIdAsync(client, Session["UserEmail"].ToString());
                    var usersTable = Table.LoadTable(client, "Users");
                    var userItem = await usersTable.GetItemAsync(realUserId);

                    if (userItem != null && userItem.ContainsKey("AssignedRoom"))
                    {
                        string currentRoom = userItem["AssignedRoom"].AsString();

                        if (!string.IsNullOrEmpty(currentRoom) && currentRoom.Contains("-"))
                        {
                            var parts = currentRoom.Split('-');
                            var roomTable = Table.LoadTable(client, "Rooms");
                            var roomExist = await roomTable.GetItemAsync(parts[0], parts[1]);

                            if (roomExist == null)
                            {
                                Session["AssignedRoom"] = null;
                                userItem.Remove("AssignedRoom");
                                await usersTable.PutItemAsync(userItem);
                                TempData["ErrorMessage"] = "Phòng của bạn đã bị Quản lý xóa khỏi hệ thống!";
                            }
                            else
                            {
                                Session["AssignedRoom"] = currentRoom;
                            }
                        }
                        else Session["AssignedRoom"] = null;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Lỗi: " + ex.Message); }
            }
            return View();
        }

        // ==========================================
        // 3. XÁC NHẬN MÃ PHÒNG BẰNG AJAX
        // ==========================================
        [HttpPost]
        public async Task<JsonResult> XacNhanMaPhong(string inviteCode)
        {
            if (string.IsNullOrEmpty(inviteCode)) return Json(new { success = false, message = "Vui lòng nhập mã phòng!" });

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var inviteTable = Table.LoadTable(client, "RoomInvites");
                var inviteItem = await inviteTable.GetItemAsync(inviteCode.Trim().ToUpper());

                if (inviteItem == null) return Json(new { success = false, message = "Mã không tồn tại hoặc sai!" });
                if (inviteItem["IsUsed"].AsBoolean()) return Json(new { success = false, message = "Mã đã được sử dụng!" });

                if (inviteItem.ContainsKey("CreatedAt") && inviteItem.ContainsKey("ExpireHours"))
                {
                    DateTime createdAt = DateTime.Parse(inviteItem["CreatedAt"].AsString());
                    if (DateTime.UtcNow > createdAt.AddHours(inviteItem["ExpireHours"].AsInt()))
                        return Json(new { success = false, message = "Mã kích hoạt đã hết hạn!" });
                }

                string roomId = inviteItem["RoomId"].AsString();
                inviteItem["IsUsed"] = true;
                await inviteTable.UpdateItemAsync(inviteItem);

                if (Session["UserEmail"] != null)
                {
                    string realUserId = await GetRealUserIdAsync(client, Session["UserEmail"].ToString());
                    var usersTable = Table.LoadTable(client, "Users");

                    var userDoc = await usersTable.GetItemAsync(realUserId);
                    if (userDoc != null)
                    {
                        userDoc["AssignedRoom"] = roomId;
                        await usersTable.UpdateItemAsync(userDoc);
                    }
                    else // Đề phòng lỗi sâu
                    {
                        var newDoc = new Document();
                        newDoc["userID"] = realUserId;
                        newDoc["AssignedRoom"] = roomId;
                        await usersTable.UpdateItemAsync(newDoc);
                    }
                    Session["AssignedRoom"] = roomId;
                }
                return Json(new { success = true, message = $"Gia nhập phòng {roomId} thành công." });
            }
            catch (Exception ex) { return Json(new { success = false, message = "Lỗi: " + ex.Message }); }
        }

        // ==========================================
        // 4. LẤY LỊCH SỬ CẢNH BÁO
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
                var search = table.Query(new QueryFilter("room_id", QueryOperator.Equal, myRoomId));
                var documentList = await search.GetNextSetAsync();

                foreach (var doc in documentList)
                {
                    double temp = doc["temperature"].AsDouble();
                    DateTime timeStamp = doc.ContainsKey("timestamp")
                        ? (doc["timestamp"].AsString().Contains("-")
                            ? DateTime.Parse(doc["timestamp"].AsString())
                            : DateTimeOffset.FromUnixTimeSeconds(long.Parse(doc["timestamp"].AsString())).ToLocalTime().DateTime)
                        : DateTime.Now;

                    string type = temp >= 50 ? "fire" : (temp >= 38 ? "high_temp" : "normal");
                    string desc = temp >= 50 ? "Cảnh báo nguy cơ cháy: Nhiệt độ tăng đột biến!"
                                : (temp >= 38 ? $"Nhiệt độ tăng cao ({temp}°C)." : "Mức nhiệt độ ổn định.");

                    alertList.Add(new TenantAlertVM { TimeStamp = timeStamp, Temperature = temp, Description = desc, AlertType = type });
                }
            }
            catch (Exception) { }

            return View(alertList.OrderByDescending(x => x.TimeStamp).ToList());
        }

        // ==========================================
        // 5. GỌI AWS LAMBDA (AI REAL-TIME)
        // ==========================================
        private async Task<(string message, string advice)> GetGeminiAIAnalysisRealTime(List<double> recentTemps)
        {
            string awsApiGatewayUrl = "https://ulj2dtb59k.execute-api.ap-southeast-1.amazonaws.com/default/SafeGuard_Tenant_Analyzer";
            string aiMessage = "Nhiệt độ phòng bạn rất ổn định.";
            string aiAdvice = "Mẹo AI: Hãy tắt bớt thiết bị điện khi ra ngoài.";

            try
            {
                using (var httpClient = new HttpClient())
                {
                    var content = new StringContent(JsonConvert.SerializeObject(new { recentTemps = recentTemps }), Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(awsApiGatewayUrl, content);
                    response.EnsureSuccessStatusCode();

                    var responseString = await response.Content.ReadAsStringAsync();
                    dynamic json = JsonConvert.DeserializeObject(responseString);
                    aiMessage = json.message; aiAdvice = json.advice;
                }
            }
            catch (Exception)
            {
                if ((recentTemps.Any() ? recentTemps.Average() : 0) >= 38)
                {
                    aiMessage = "Nhiệt độ đang NÓNG BẤT THƯỜNG.";
                    aiAdvice = "Mẹo AI: Kiểm tra thiết bị điện ngay lập tức.";
                }
            }
            return (aiMessage, aiAdvice);
        }

        // ==========================================
        // 6. LẤY DỮ LIỆU BIỂU ĐỒ VÀ GỌI AI
        // ==========================================
        [HttpGet]
        public async Task<JsonResult> GetRoomChartData()
        {
            string myRoomId = Session["AssignedRoom"] != null ? Session["AssignedRoom"].ToString() : "";
            if (string.IsNullOrEmpty(myRoomId)) return Json(new { success = false, message = "Chưa có phòng" }, JsonRequestBehavior.AllowGet);

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "SafeDorm_History");
                var documentList = await table.Query(new QueryFilter("room_id", QueryOperator.Equal, myRoomId)).GetNextSetAsync();

                var logs = documentList.Select(doc => new {
                    Time = doc.ContainsKey("timestamp") ? (doc["timestamp"].AsString().Contains("-") ? DateTime.Parse(doc["timestamp"].AsString()) : DateTimeOffset.FromUnixTimeSeconds(long.Parse(doc["timestamp"].AsString())).ToLocalTime().DateTime) : DateTime.Now,
                    Temp = doc["temperature"].AsDouble()
                }).ToList();

                var chartData = logs.OrderByDescending(x => x.Time).Take(7).OrderBy(x => x.Time).Select(x => new { Label = x.Time.ToString("HH:mm dd/MM"), Value = x.Temp }).ToList();

                string aiMessage = "Chưa đủ dữ liệu AI."; string aiAdvice = "Chờ cảm biến gửi tín hiệu...";
                var listTemps = chartData.Select(x => (double)x.Value).ToList();
                if (listTemps.Count > 0)
                {
                    var gemini = await GetGeminiAIAnalysisRealTime(listTemps);
                    aiMessage = gemini.message; aiAdvice = gemini.advice;
                }

                return Json(new { success = true, labels = chartData.Select(c => c.Label), values = chartData.Select(c => c.Value), aiMessage = aiMessage, aiAdvice = aiAdvice }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }, JsonRequestBehavior.AllowGet); }
        }

        // ==========================================
        // 7. TẠO DỮ LIỆU GIẢ 
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> TaoDuLieuGia()
        {
            string roomId = Session["AssignedRoom"] != null ? Session["AssignedRoom"].ToString() : "C-107";
            try
            {
                var table = Table.LoadTable(new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1), "SafeDorm_History");
                for (int i = 6; i >= 0; i--)
                {
                    var doc = new Document { ["room_id"] = roomId, ["timestamp"] = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd HH:mm:ss"), ["temperature"] = i == 0 ? 45 : new Random().Next(25, 32) };
                    await table.PutItemAsync(doc);
                }
                return Content($"Tạo dữ liệu cho {roomId} thành công!");
            }
            catch (Exception ex) { return Content("Lỗi: " + ex.Message); }
        }
    }
}