using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using SafeGuard.Filters;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "TENANT")]
    public class TenantController : Controller
    {
        // View Model để truyền dữ liệu ra bảng
        public class TenantAlertVM
        {
            public DateTime TimeStamp { get; set; }
            public double Temperature { get; set; }
            public string Description { get; set; }
            public string AlertType { get; set; } // "fire", "high_temp", "normal"
        }

        public ActionResult Index() => View();
        public ActionResult NoiQuy() => View();
        public ActionResult ThongTinCaNhan() => View();
        public ActionResult PhanTichAI() => View();

        [HttpGet]
        public async Task<ActionResult> QuanLyPhong()
        {
            // Nếu có đăng nhập nhưng Session phòng đang trống, thì tự động xuống DB lấy lên
            if (Session["UserEmail"] != null && Session["AssignedRoom"] == null)
            {
                try
                {
                    var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                    var usersTable = Table.LoadTable(client, "Users");
                    var userItem = await usersTable.GetItemAsync(Session["UserEmail"].ToString());

                    // Nếu trong DB có cột AssignedRoom và không bị rỗng
                    if (userItem != null && userItem.ContainsKey("AssignedRoom") && !string.IsNullOrEmpty(userItem["AssignedRoom"].AsString()))
                    {
                        Session["AssignedRoom"] = userItem["AssignedRoom"].AsString(); // Gán vào Session
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Lỗi lấy phòng: " + ex.Message);
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

            // Lấy phòng từ Session
            string myRoomId = Session["AssignedRoom"] != null ? Session["AssignedRoom"].ToString() : "Chưa liên kết";

            // THÊM DÒNG NÀY ĐỂ TRUYỀN TÊN PHÒNG RA NGOÀI GIAO DIỆN
            ViewBag.RoomId = myRoomId;

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "SafeDorm_History");

                // Lọc lấy dữ liệu của phòng hiện tại
                var queryFilter = new QueryFilter("room_id", QueryOperator.Equal, myRoomId);
                var search = table.Query(queryFilter);
                var documentList = await search.GetNextSetAsync();

                foreach (var doc in documentList)
                {
                    double temp = doc["temperature"].AsDouble();
                    DateTime timeStamp;

                    // ĐÃ SỬA: Logic đọc ngày tháng thông minh (Đọc được cả số cũ và chữ mới)
                    // ĐÃ SỬA: Logic đọc ngày tháng chống lỗi (Đọc được cả số cũ và chữ mới)
                    if (doc.ContainsKey("timestamp"))
                    {
                        string timeStr = doc["timestamp"].AsString();

                        // Nếu chuỗi có chứa dấu "-" (VD: "2026-04-08 15:30:00") thì nó là ngày tháng kiểu mới
                        if (timeStr.Contains("-"))
                        {
                            DateTime.TryParse(timeStr, out timeStamp);
                        }
                        else
                        {
                            // Nếu không có dấu "-", nó là dãy số Unix Timestamp cũ (VD: "1712500000")
                            long unixTime = long.Parse(timeStr);
                            timeStamp = DateTimeOffset.FromUnixTimeSeconds(unixTime).ToLocalTime().DateTime;
                        }
                    }
                    else
                    {
                        timeStamp = DateTime.Now; // Đề phòng lỗi thiếu cột
                    }

                    // Phân loại logic cảnh báo
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

                    alertList.Add(new TenantAlertVM
                    {
                        TimeStamp = timeStamp,
                        Temperature = temp,
                        Description = desc,
                        AlertType = type
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi lấy dữ liệu Tenant: " + ex.Message);
            }

            // Sắp xếp mới nhất lên đầu
            var sortedList = alertList.OrderByDescending(x => x.TimeStamp).ToList();
            return View(sortedList);
        }

        // ==========================================
        // NGƯỜI THUÊ NHẬP MÃ THAM GIA PHÒNG
        // ==========================================
        [HttpPost]
        public async Task<ActionResult> XacNhanMaPhong(string inviteCode)
        {
            if (string.IsNullOrEmpty(inviteCode))
            {
                TempData["ErrorMessage"] = "Vui lòng nhập mã phòng!";
                return RedirectToAction("QuanLyPhong");
            }

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var inviteTable = Table.LoadTable(client, "RoomInvites");
                var inviteItem = await inviteTable.GetItemAsync(inviteCode.Trim().ToUpper());

                if (inviteItem == null)
                {
                    TempData["ErrorMessage"] = "Mã không tồn tại hoặc bạn đã nhập sai!";
                    return RedirectToAction("QuanLyPhong");
                }

                if (inviteItem["IsUsed"].AsBoolean())
                {
                    TempData["ErrorMessage"] = "Mã này đã được sử dụng bởi một người khác!";
                    return RedirectToAction("QuanLyPhong");
                }

                if (inviteItem.ContainsKey("CreatedAt") && inviteItem.ContainsKey("ExpireHours"))
                {
                    DateTime createdAt = DateTime.Parse(inviteItem["CreatedAt"].AsString());
                    int expireHours = inviteItem["ExpireHours"].AsInt();
                    if (DateTime.UtcNow > createdAt.AddHours(expireHours))
                    {
                        TempData["ErrorMessage"] = "Mã kích hoạt này đã hết hạn sử dụng!";
                        return RedirectToAction("QuanLyPhong");
                    }
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

                TempData["SuccessMessage"] = $"Chúc mừng! Bạn đã gia nhập phòng {roomId} thành công.";
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi hệ thống: " + ex.Message; }

            return RedirectToAction("QuanLyPhong");
        }
    }
}