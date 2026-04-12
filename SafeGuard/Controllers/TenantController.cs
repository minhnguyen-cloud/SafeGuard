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
using System.Globalization;

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
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);

                // --- KIỂM TRA XEM PHÒNG ĐÃ BỊ ADMIN XÓA CHƯA ---
                var roomTable = Table.LoadTable(client, "Rooms");
                var roomParts = roomId.Split('-');
                if (roomParts.Length == 2)
                {
                    var roomExist = await roomTable.GetItemAsync(roomParts[0], roomParts[1]);
                    if (roomExist == null)
                    {
                        // Admin đã xóa phòng này -> Xóa session
                        Session["AssignedRoom"] = null;

                        // Xóa luôn dưới DB của Tenant cho chắc ăn
                        if (Session["UserId"] != null)
                        {
                            var usersTable = Table.LoadTable(client, "Users");
                            var userDoc = await usersTable.GetItemAsync(Session["UserId"].ToString());
                            if (userDoc != null)
                            {
                                userDoc.Remove("AssignedRoom");
                                await usersTable.PutItemAsync(userDoc);
                            }
                        }
                        // Trả về trang "Chưa có phòng" ngay lập tức
                        return RedirectToAction("Index");
                    }
                }
                // -------------------------------------------------------------

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
        private readonly Random _random = new Random();

        private async Task<string> GetOrAssignDemoRoomAsync(AmazonDynamoDBClient client, string realUserId)
        {
            var usersTable = Table.LoadTable(client, "Users");
            var roomsTable = Table.LoadTable(client, "Rooms");

            var userDoc = await usersTable.GetItemAsync(realUserId);
            if (userDoc == null)
                throw new Exception("Không tìm thấy người dùng trong hệ thống.");

            // Nếu đã có phòng thật rồi thì giữ nguyên
            if (userDoc.ContainsKey("AssignedRoom") && !string.IsNullOrWhiteSpace(userDoc["AssignedRoom"].AsString()))
            {
                return userDoc["AssignedRoom"].AsString().Trim();
            }

            // Lấy danh sách phòng có sẵn trong bảng Rooms
            var allRooms = await roomsTable.Scan(new ScanFilter()).GetRemainingAsync();

            var availableRooms = allRooms
                .Where(r => r.ContainsKey("BlockId") && r.ContainsKey("RoomId"))
                .Select(r => $"{r["BlockId"].AsString().Trim()}-{r["RoomId"].AsString().Trim()}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!availableRooms.Any())
                throw new Exception("Hiện chưa có phòng nào trong hệ thống để gán dữ liệu mẫu.");

            // Random 1 phòng bất kỳ từ bảng Rooms
            var selectedRoom = availableRooms[_random.Next(availableRooms.Count)];

            userDoc["AssignedRoom"] = selectedRoom;
            userDoc["DemoAssignedRoom"] = true;
            userDoc["DemoAssignedRoomId"] = selectedRoom;
            userDoc["DemoAssignedAt"] = DateTime.UtcNow.ToString("O");

            await usersTable.UpdateItemAsync(userDoc);

            Session["AssignedRoom"] = selectedRoom;

            return selectedRoom;
        }

        private async Task<string> GetCurrentRoomForDemoAsync(AmazonDynamoDBClient client, string realUserId)
        {
            var usersTable = Table.LoadTable(client, "Users");
            var userDoc = await usersTable.GetItemAsync(realUserId);

            if (userDoc == null)
                throw new Exception("Không tìm thấy người dùng.");

            if (userDoc.ContainsKey("AssignedRoom") && !string.IsNullOrWhiteSpace(userDoc["AssignedRoom"].AsString()))
            {
                string existingRoom = userDoc["AssignedRoom"].AsString().Trim();

                // Nếu user đã có phòng thật thì dọn cờ demo cũ để tránh xóa nhầm sau này
                bool needUpdate = false;

                if (userDoc.ContainsKey("DemoAssignedRoom"))
                {
                    userDoc.Remove("DemoAssignedRoom");
                    needUpdate = true;
                }

                if (userDoc.ContainsKey("DemoAssignedAt"))
                {
                    userDoc.Remove("DemoAssignedAt");
                    needUpdate = true;
                }

                if (userDoc.ContainsKey("DemoAssignedRoomId"))
                {
                    userDoc.Remove("DemoAssignedRoomId");
                    needUpdate = true;
                }

                if (needUpdate)
                    await usersTable.UpdateItemAsync(userDoc);

                Session["AssignedRoom"] = existingRoom;
                return existingRoom;
            }

            return await GetOrAssignDemoRoomAsync(client, realUserId);
        }

        private List<double> BuildProfessionalDemoTemperatureSeries()
        {
            // Dữ liệu tăng dần có chủ đích để demo đẹp hơn
            return new List<double> { 29, 30, 31, 33, 35, 37, 39, 42, 45, 50 };
        }

        private string BuildTemperatureDescription(double temp)
        {
            if (temp >= 50)
                return "Mức nguy hiểm cao. Hệ thống khuyến nghị kiểm tra thiết bị điện ngay lập tức.";
            if (temp >= 45)
                return "Nhiệt độ tăng cao bất thường. Cần theo dõi sát tình trạng phòng.";
            if (temp >= 38)
                return "Nhiệt độ bắt đầu vượt ngưỡng cảnh báo. Hệ thống đang tăng cường giám sát.";
            return "Nhiệt độ ổn định, phòng đang ở trạng thái an toàn.";
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

                        if (userDoc.ContainsKey("DemoAssignedRoom"))
                            userDoc.Remove("DemoAssignedRoom");

                        if (userDoc.ContainsKey("DemoAssignedAt"))
                            userDoc.Remove("DemoAssignedAt");

                        if (userDoc.ContainsKey("DemoAssignedRoomId"))
                            userDoc.Remove("DemoAssignedRoomId");

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
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> CreateDemoData()
        {
            if (Session["UserEmail"] == null)
            {
                return Json(new { success = false, message = "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại." });
            }

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                string realUserId = await GetRealUserIdAsync(client, Session["UserEmail"].ToString());

                string roomId = await GetCurrentRoomForDemoAsync(client, realUserId);

                var historyTable = Table.LoadTable(client, "SafeDorm_History");

                // Xóa dữ liệu demo cũ của chính user/phòng này trước khi tạo mới
                var existingDocs = await historyTable.Query(new QueryFilter("room_id", QueryOperator.Equal, roomId)).GetRemainingAsync();

                var oldDemoDocs = existingDocs
                    .Where(d =>
                        d.ContainsKey("isDemo")
                        && d["isDemo"].AsBoolean()
                        && d.ContainsKey("demoUserId")
                        && string.Equals(d["demoUserId"].AsString(), realUserId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var doc in oldDemoDocs)
                {
                    await historyTable.DeleteItemAsync(doc["room_id"].AsString(), doc["timestamp"].AsLong());
                }

                // Tạo chuỗi dữ liệu demo mới
                var temps = BuildProfessionalDemoTemperatureSeries();

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long start = now - ((temps.Count - 1) * 60); // mỗi điểm cách nhau 1 phút

                for (int i = 0; i < temps.Count; i++)
                {
                    var doc = new Document();
                    doc["room_id"] = roomId;
                    doc["timestamp"] = start + (i * 60);
                    doc["temperature"] = temps[i];
                    doc["isDemo"] = true;
                    doc["demoUserId"] = realUserId;
                    doc["source"] = "SIMULATOR";
                    doc["description"] = BuildTemperatureDescription(temps[i]);
                    doc["createdAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

                    await historyTable.PutItemAsync(doc);
                }

                return Json(new
                {
                    success = true,
                    roomId = roomId,
                    points = temps.Count,
                    message = $"Đã tạo {temps.Count} mốc dữ liệu mẫu thành công cho phòng {roomId}."
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể tạo dữ liệu mẫu: " + ex.Message
                });
            }
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> DeleteDemoData()
        {
            if (Session["UserEmail"] == null)
            {
                return Json(new { success = false, message = "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại." });
            }

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                string realUserId = await GetRealUserIdAsync(client, Session["UserEmail"].ToString());

                var usersTable = Table.LoadTable(client, "Users");
                var historyTable = Table.LoadTable(client, "SafeDorm_History");

                var userDoc = await usersTable.GetItemAsync(realUserId);
                if (userDoc == null)
                    return Json(new { success = false, message = "Không tìm thấy thông tin người dùng." });

                string roomId = userDoc.ContainsKey("AssignedRoom") ? userDoc["AssignedRoom"].AsString() : "";

                if (string.IsNullOrWhiteSpace(roomId))
                {
                    return Json(new { success = true, message = "Không có dữ liệu mẫu để xóa." });
                }

                var roomDocs = await historyTable.Query(new QueryFilter("room_id", QueryOperator.Equal, roomId)).GetRemainingAsync();

                var demoDocs = roomDocs
                    .Where(d =>
                        d.ContainsKey("isDemo")
                        && d["isDemo"].AsBoolean()
                        && d.ContainsKey("demoUserId")
                        && string.Equals(d["demoUserId"].AsString(), realUserId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var doc in demoDocs)
                {
                    await historyTable.DeleteItemAsync(doc["room_id"].AsString(), doc["timestamp"].AsLong());
                }

                bool demoAssignedRoom = userDoc.ContainsKey("DemoAssignedRoom") && userDoc["DemoAssignedRoom"].AsBoolean();
                string currentAssignedRoom = userDoc.ContainsKey("AssignedRoom") ? userDoc["AssignedRoom"].AsString() : "";
                string demoAssignedRoomId = userDoc.ContainsKey("DemoAssignedRoomId") ? userDoc["DemoAssignedRoomId"].AsString() : "";

                if (demoAssignedRoom)
                {
                    // Chỉ xóa AssignedRoom nếu phòng hiện tại đúng là phòng demo đã auto gán
                    if (!string.IsNullOrWhiteSpace(currentAssignedRoom) &&
                        !string.IsNullOrWhiteSpace(demoAssignedRoomId) &&
                        string.Equals(currentAssignedRoom, demoAssignedRoomId, StringComparison.OrdinalIgnoreCase))
                    {
                        userDoc.Remove("AssignedRoom");
                        Session["AssignedRoom"] = null;
                    }

                    userDoc.Remove("DemoAssignedRoom");

                    if (userDoc.ContainsKey("DemoAssignedAt"))
                        userDoc.Remove("DemoAssignedAt");

                    if (userDoc.ContainsKey("DemoAssignedRoomId"))
                        userDoc.Remove("DemoAssignedRoomId");

                    await usersTable.UpdateItemAsync(userDoc);
                }

                return Json(new
                {
                    success = true,
                    deletedCount = demoDocs.Count,
                    message = demoDocs.Count > 0
                        ? $"Đã xóa {demoDocs.Count} bản ghi dữ liệu mẫu."
                        : "Không tìm thấy dữ liệu mẫu để xóa."
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể xóa dữ liệu mẫu: " + ex.Message
                });
            }
        }
    }
}