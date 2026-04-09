using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using SafeGuard.Filters;
using SafeGuard.Models;
using SafeGuard.Controllers.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "ADMIN")]
    public class AdminController : Controller
    {
        private AmazonDynamoDBClient GetClient() => new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);

        // ==========================================
        // 1. TỔNG QUAN (DASHBOARD)
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> Index()
        {
            var client = GetClient();
            try
            {
                var roomsTable = Table.LoadTable(client, "Rooms");
                var allRoomsDocs = await roomsTable.Scan(new ScanFilter()).GetNextSetAsync();
                ViewBag.TotalRooms = allRoomsDocs.Count;

                var usersTable = Table.LoadTable(client, "Users");
                var tenantFilter = new ScanFilter();
                tenantFilter.AddCondition("role", ScanOperator.Equal, "TENANT");
                var activeTenants = await usersTable.Scan(tenantFilter).GetNextSetAsync();
                ViewBag.ActiveRooms = activeTenants.Count;

                var historyTable = Table.LoadTable(client, "SafeDorm_History");
                long tenMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
                var historyFilter = new ScanFilter();
                historyFilter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, tenMinsAgo);
                var recentLogs = await historyTable.Scan(historyFilter).GetNextSetAsync();

                var onlineRoomIds = recentLogs.Select(doc => doc["room_id"].AsString()).Distinct().ToList();
                ViewBag.OnlineDevices = onlineRoomIds.Count;

                ViewBag.CurrentAlerts = recentLogs.Count(doc => doc["temperature"].AsDouble() >= 38);

                // 5. TRẠNG THÁI GẦN ĐÂY
                ViewBag.RecentActivities = activeTenants
                    .Where(u => u.ContainsKey("AssignedRoom") && !string.IsNullOrEmpty(u["AssignedRoom"].AsString()))
                    .OrderByDescending(u => u.ContainsKey("createdAt") ? u["createdAt"].AsString() : "")
                    .Take(4)
                    .Select(u => new RecentActivityVM
                    {
                        Name = u.ContainsKey("fullName") ? u["fullName"].AsString() : "Sinh viên",
                        Room = u["AssignedRoom"].AsString(),
                        Time = u.ContainsKey("createdAt") ? DateTime.Parse(u["createdAt"].AsString()).ToString("HH:mm - dd/MM") : ""
                    }).ToList();

                // 6. BẢNG CẢM BIẾN THEO PHÒNG
                ViewBag.SensorList = recentLogs
                    .GroupBy(doc => doc["room_id"].AsString())
                    .Select(g => {
                        var latest = g.OrderByDescending(doc => doc["timestamp"].AsLong()).First();
                        return new SensorDataVM
                        {
                            RoomId = latest["room_id"].AsString(),
                            Temp = latest["temperature"].AsDouble(),
                            Time = DateTimeOffset.FromUnixTimeSeconds(latest["timestamp"].AsLong()).ToLocalTime().ToString("hh:mm tt")
                        };
                    }).ToList();

                // 7. LẤY DÃY VÀ PHÒNG THẬT CHO DROPDOWN TẠO MÃ
                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();
                ViewBag.Blocks = blocks.Select(b => b["BlockId"].AsString()).ToList();

                // TRUYỀN DANH SÁCH PHÒNG THẬT LÊN VIEW
                ViewBag.RealRoomsList = allRoomsDocs.Select(r => new SafeGuard.Models.RoomDisplayViewModel
                {
                    BlockName = r.ContainsKey("BlockId") ? r["BlockId"].AsString() : "",
                    RoomId = r.ContainsKey("RoomId") ? r["RoomId"].AsString() : ""
                }).OrderBy(x => x.BlockName).ThenBy(x => x.RoomId).ToList();

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi AWS: " + ex.Message);
                ViewBag.TotalRooms = 0; ViewBag.ActiveRooms = 0; ViewBag.OnlineDevices = 0; ViewBag.CurrentAlerts = 0;
            }

            return View();
        }

        // ==========================================
        // 2. QUẢN LÝ PHÒNG
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> QuanLyPhong()
        {
            try
            {
                var client = GetClient();
                var tableFacilities = Table.LoadTable(client, "Facilities");
                ViewBag.Blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();

                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var tableUsers = Table.LoadTable(client, "Users");
                var allUsers = await tableUsers.Scan(new ScanFilter()).GetRemainingAsync();

                var roomList = new List<RoomDisplayViewModel>();
                foreach (var r in allRooms)
                {
                    string bId = r["BlockId"].AsString();
                    string rId = r["RoomId"].AsString();
                    string fullId = $"{bId}-{rId}";

                    var owner = allUsers.FirstOrDefault(u => u.ContainsKey("AssignedRoom") && u["AssignedRoom"].AsString() == fullId);
                    roomList.Add(new RoomDisplayViewModel
                    {
                        RoomName = "Phòng " + bId + "-" + rId,
                        BlockName = bId,
                        RoomId = rId,
                        OwnerEmail = owner != null ? (owner.ContainsKey("fullName") ? owner["fullName"].AsString() : owner["userID"].AsString()) : "Chưa có người thuê",
                        IsOnline = owner != null
                    });
                }
                ViewBag.Rooms = roomList.OrderBy(r => r.BlockName).ThenBy(r => r.RoomId).ToList();
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi lấy dữ liệu: " + ex.Message; }
            return View();
        }

        [HttpPost]
        public async Task<ActionResult> ThemPhongMoi(string blockId, string roomNumber)
        {
            try
            {
                blockId = blockId?.Trim(); roomNumber = roomNumber?.Trim();
                if (string.IsNullOrEmpty(blockId) || string.IsNullOrEmpty(roomNumber)) return RedirectToAction("QuanLyPhong");

                var client = GetClient();
                var table = Table.LoadTable(client, "Rooms");
                var existingRoom = await table.GetItemAsync(blockId, roomNumber);
                if (existingRoom != null)
                {
                    TempData["ErrorMessage"] = $"Lỗi: Phòng {roomNumber} đã tồn tại trong Dãy {blockId}!";
                    return RedirectToAction("QuanLyPhong");
                }

                var item = new Document();
                item["BlockId"] = blockId; item["RoomId"] = roomNumber; item["CreatedAt"] = DateTime.Now.ToString("O");
                await table.PutItemAsync(item);
                TempData["SuccessMessage"] = $"Đã thêm phòng {roomNumber} vào dãy {blockId} thành công!";
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi từ AWS: " + ex.Message; }
            return RedirectToAction("QuanLyPhong");
        }

        [HttpPost]
        public async Task<JsonResult> XoaPhong(string blockId, string roomId)
        {
            try
            {
                var client = GetClient();
                string fullRoomId = $"{blockId}-{roomId}";

                // 1. Xóa phòng khỏi bảng Rooms
                var tableRooms = Table.LoadTable(client, "Rooms");
                await tableRooms.DeleteItemAsync(blockId, roomId);

                // 2. Đá TẤT CẢ người thuê ra khỏi phòng
                var usersTable = Table.LoadTable(client, "Users");
                var scanFilter = new ScanFilter();
                scanFilter.AddCondition("AssignedRoom", ScanOperator.Equal, fullRoomId);
                var usersInRoom = await usersTable.Scan(scanFilter).GetRemainingAsync();

                foreach (var user in usersInRoom)
                {
                    user.Remove("AssignedRoom");
                    await usersTable.PutItemAsync(user); // Ghi đè toàn bộ để ép DB xóa cột
                }

                // 3. Xóa sạch các Mã Kích Hoạt của phòng này
                var invitesTable = Table.LoadTable(client, "RoomInvites");
                var inviteFilter = new ScanFilter();
                inviteFilter.AddCondition("RoomId", ScanOperator.Equal, fullRoomId);
                var invitesInRoom = await invitesTable.Scan(inviteFilter).GetRemainingAsync();

                foreach (var inv in invitesInRoom)
                {
                    await invitesTable.DeleteItemAsync(inv["InviteCode"].AsString());
                }

                return Json(new { success = true, message = $"Đã xóa vĩnh viễn phòng {fullRoomId} và dọn dẹp dữ liệu!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        // ==========================================
        // 3. QUẢN LÝ DÃY TRỌ
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> QuanLyDayTro()
        {
            try
            {
                var client = GetClient();
                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();
                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();
                var tableUsers = Table.LoadTable(client, "Users");
                var allUsers = await tableUsers.Scan(new ScanFilter()).GetRemainingAsync();

                foreach (var block in blocks)
                {
                    string bId = block["BlockId"].AsString();
                    var roomsInBlock = allRooms.Where(r => r.ContainsKey("BlockId") && r["BlockId"].AsString() == bId).ToList();
                    block["TotalRooms"] = roomsInBlock.Count;
                    int activeRooms = roomsInBlock.Count(room => allUsers.Any(u => u.ContainsKey("AssignedRoom") && u["AssignedRoom"].AsString() == $"{bId}-{room["RoomId"].AsString()}"));
                    block["ActiveRooms"] = activeRooms;
                }
                ViewBag.Blocks = blocks.OrderBy(b => b["BlockName"].AsString()).ToList();

                // MỚI THÊM: Truyền danh sách phòng và user sang View để hiển thị Popup Chi Tiết
                ViewBag.AllRooms = allRooms;
                ViewBag.AllUsers = allUsers;
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi: " + ex.Message; }
            return View();
        }

        [HttpPost]
        public async Task<ActionResult> ThemDayMoi(string blockName, string address, int numberOfRooms)
        {
            try
            {
                var client = GetClient();
                string blockId = blockName.Trim().ToUpper();
                if (blockId.Contains(" ")) blockId = blockId.Split(' ').Last();

                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blockItem = new Document();
                blockItem["PK"] = "BLOCK";
                blockItem["SK"] = $"BLOCK#{Guid.NewGuid().ToString().Substring(0, 5)}";
                blockItem["BlockId"] = blockId; blockItem["BlockName"] = blockName;
                blockItem["Address"] = address; blockItem["TotalRooms"] = numberOfRooms; blockItem["ActiveRooms"] = 0;
                await tableFacilities.PutItemAsync(blockItem);

                var tableRooms = Table.LoadTable(client, "Rooms");
                for (int i = 1; i <= numberOfRooms; i++)
                {
                    var roomItem = new Document();
                    roomItem["BlockId"] = blockId; roomItem["RoomId"] = $"10{i}"; roomItem["CreatedAt"] = DateTime.Now.ToString("O");
                    await tableRooms.PutItemAsync(roomItem);
                }
                // ĐÃ SỬA CÂU THÔNG BÁO Ở ĐÂY
                TempData["SuccessMessage"] = $"Đã thêm dãy trọ {blockName} thành công!";
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi: " + ex.Message; }
            return RedirectToAction("QuanLyDayTro");
        }

        // ==========================================
        // 4. TẠO MÃ KÍCH HOẠT
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> TaoMaKichHoat()
        {
            try
            {
                var client = GetClient();
                var tableInvites = Table.LoadTable(client, "RoomInvites");
                ViewBag.InviteList = await tableInvites.Scan(new ScanFilter()).GetRemainingAsync();
                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var roomList = new List<RoomDisplayViewModel>();
                foreach (var r in allRooms)
                {
                    string bId = r["BlockId"].AsString();
                    string rId = r["RoomId"].AsString();
                    roomList.Add(new RoomDisplayViewModel { RoomName = "Phòng " + bId + "-" + rId, BlockName = bId, RoomId = rId });
                }
                ViewBag.Rooms = roomList.OrderBy(r => r.BlockName).ThenBy(r => r.RoomId).ToList();
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi: " + ex.Message; }
            return View();
        }

        [HttpPost]
        public async Task<ActionResult> TaoMaMoi(string selectedRoom, int expireHours)
        {
            try
            {
                string newInviteCode = $"{selectedRoom}-{Guid.NewGuid().ToString().Substring(0, 6).ToUpper()}";
                var client = GetClient();
                var table = Table.LoadTable(client, "RoomInvites");
                var item = new Document();
                item["InviteCode"] = newInviteCode; item["RoomId"] = selectedRoom;
                item["ExpireHours"] = expireHours; item["IsUsed"] = false; item["CreatedAt"] = DateTime.UtcNow.ToString("O");
                await table.PutItemAsync(item);
                TempData["SuccessMessage"] = $"Tạo mã thành công: {newInviteCode}";
                return Request.UrlReferrer?.AbsolutePath.Contains("Admin/Index") == true ? RedirectToAction("Index") : RedirectToAction("TaoMaKichHoat");
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi: " + ex.Message; return RedirectToAction("TaoMaKichHoat"); }
        }

        // ==========================================
        // 5. LỊCH SỬ CẢNH BÁO
        // ==========================================
        public async Task<ActionResult> LichSuCanhBao()
        {
            var alertList = new List<AlertHistoryViewModel>();
            try
            {
                var client = GetClient();
                var table = Table.LoadTable(client, "SafeDorm_History");
                var documentList = await table.Scan(new ScanFilter()).GetNextSetAsync();
                foreach (var doc in documentList)
                {
                    double temp = doc["temperature"].AsDouble();
                    if (temp >= 38)
                    {
                        DateTime timeStamp = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds(doc["timestamp"].AsLong()).ToLocalTime();
                        string description = temp >= 50 ? "[KHẨN CẤP] Nguy cơ cháy nổ cao!" : temp >= 45 ? "Nhiệt độ tăng cao bất thường." : "Cảnh báo ngưỡng 1.";
                        alertList.Add(new AlertHistoryViewModel { TimeStamp = timeStamp, RoomId = "Phòng " + doc["room_id"].AsString(), Temperature = temp, Description = description });
                    }
                }
            }
            catch (Exception) { }
            return View(alertList.OrderByDescending(x => x.TimeStamp).ToList());
        }

        public async Task<ActionResult> XuatExcelLichSu()
        {
            var alertList = new List<AlertHistoryViewModel>();
            try
            {
                var client = GetClient();
                var table = Table.LoadTable(client, "SafeDorm_History");
                var documentList = await table.Scan(new ScanFilter()).GetNextSetAsync();
                foreach (var doc in documentList)
                {
                    double temp = doc["temperature"].AsDouble();
                    if (temp >= 38)
                    {
                        DateTime timeStamp = DateTimeOffset.FromUnixTimeSeconds(doc["timestamp"].AsLong()).ToLocalTime().DateTime;
                        string desc = temp >= 50 ? "[KHẨN CẤP] Nguy cơ cháy nổ" : temp >= 45 ? "Nhiệt độ tăng cao bất thường" : "Cảnh báo ngưỡng 1";
                        alertList.Add(new AlertHistoryViewModel { TimeStamp = timeStamp, RoomId = "Phòng " + doc["room_id"].AsString(), Temperature = temp, Description = desc });
                    }
                }
            }
            catch (Exception) { }

            var builder = new StringBuilder();
            builder.AppendLine("Thời Gian,Phòng,Mức Nhiệt Độ,Mô Tả Cảnh Báo");
            foreach (var item in alertList.OrderByDescending(x => x.TimeStamp))
            {
                builder.AppendLine($"{item.TimeStamp:dd/MM/yyyy HH:mm:ss},{item.RoomId},{item.Temperature},\"{item.Description}\"");
            }
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray(), "text/csv", $"LichSuCanhBao_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        }

        // ==========================================
        // LẤY DANH SÁCH THÀNH VIÊN TRONG PHÒNG (DÙNG CHO POPUP AJAX)
        // ==========================================
        [HttpGet]
        public async Task<JsonResult> GetRoomMembers(string roomId)
        {
            try
            {
                var client = GetClient();
                var usersTable = Table.LoadTable(client, "Users");

                var scanFilter = new ScanFilter();
                scanFilter.AddCondition("AssignedRoom", ScanOperator.Equal, roomId);
                var search = usersTable.Scan(scanFilter);
                var documentList = await search.GetRemainingAsync();

                var members = documentList.Select(doc => new {
                    FullName = doc.ContainsKey("fullName") ? doc["fullName"].AsString() : "Chưa cập nhật",
                    Email = doc.ContainsKey("email") ? doc["email"].AsString() : (doc.ContainsKey("userID") ? doc["userID"].AsString() : "N/A")
                }).ToList();

                return Json(new { success = true, data = members }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        // Dùng cho AJAX load lại bảng cảm biến Real-time ở trang Tổng quan
        [HttpGet]
        public async Task<JsonResult> GetRealTimeSensorData()
        {
            try
            {
                var client = GetClient();
                var historyTable = Table.LoadTable(client, "SafeDorm_History");

                // Lấy data trong 10 phút qua
                long tenMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
                var historyFilter = new ScanFilter();
                historyFilter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, tenMinsAgo);
                var recentLogs = await historyTable.Scan(historyFilter).GetNextSetAsync();

                var sensorList = recentLogs
                    .GroupBy(doc => doc["room_id"].AsString())
                    .Select(g => {
                        var latest = g.OrderByDescending(doc => doc["timestamp"].AsLong()).First();
                        double temp = latest["temperature"].AsDouble();
                        return new
                        {
                            RoomId = latest["room_id"].AsString(),
                            Temp = temp,
                            Time = DateTimeOffset.FromUnixTimeSeconds(latest["timestamp"].AsLong()).ToLocalTime().ToString("hh:mm tt"),
                            IsAlert = temp >= 38
                        };
                    }).ToList();

                return Json(sensorList, JsonRequestBehavior.AllowGet);
            }
            catch { return Json(null, JsonRequestBehavior.AllowGet); }
        }

        public ActionResult BaoCaoAI() => View();
    }
}

// ======================================================================
// GÓI CÁC CLASS MODEL XUỐNG ĐÂY ĐỂ ĐẢM BẢO KHÔNG LỖI THIẾU FILE
// ======================================================================
namespace SafeGuard.Controllers.ViewModels
{
    public class RecentActivityVM { public string Name { get; set; } public string Room { get; set; } public string Time { get; set; } }
    public class SensorDataVM { public string RoomId { get; set; } public double Temp { get; set; } public string Time { get; set; } }
}