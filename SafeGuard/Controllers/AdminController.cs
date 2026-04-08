using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using SafeGuard.Filters;
using SafeGuard.Models;
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
            var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);

            try
            {
                // 1. TỔNG SỐ PHÒNG (Quét bảng Rooms)
                var roomsTable = Table.LoadTable(client, "Rooms");
                var allRooms = await roomsTable.Scan(new ScanFilter()).GetNextSetAsync();
                ViewBag.TotalRooms = allRooms.Count;

                // 2. PHÒNG ĐANG HOẠT ĐỘNG (Đếm user có role = TENANT)
                var usersTable = Table.LoadTable(client, "Users");
                var tenantFilter = new ScanFilter();
                tenantFilter.AddCondition("role", ScanOperator.Equal, "TENANT");
                var activeTenants = await usersTable.Scan(tenantFilter).GetNextSetAsync();
                ViewBag.ActiveRooms = activeTenants.Count;

                // 3 & 4. THIẾT BỊ ONLINE & CẢNH BÁO (Quét bảng SafeDorm_History trong 10 phút qua)
                var historyTable = Table.LoadTable(client, "SafeDorm_History");

                // Lấy thời gian 10 phút trước tính bằng Unix Timestamp (giống định dạng bạn lưu trong DynamoDB)
                long tenMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();

                var historyFilter = new ScanFilter();
                historyFilter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, tenMinsAgo);
                var recentLogs = await historyTable.Scan(historyFilter).GetNextSetAsync();

                // Lọc ra danh sách các phòng "Đang Online" (loại bỏ trùng lặp nếu 1 phòng gửi nhiều lần trong 10 phút)
                var onlineRoomIds = recentLogs.Select(doc => doc["room_id"].AsString()).Distinct().ToList();
                ViewBag.OnlineDevices = onlineRoomIds.Count;

                // Đếm số cảnh báo (Nhiệt độ >= 38) trong số các log mới nhất
                int alertCount = recentLogs.Count(doc => doc["temperature"].AsDouble() >= 38);
                ViewBag.CurrentAlerts = alertCount;
            }
            catch (Exception ex)
            {
                // Xử lý nếu lỗi kết nối AWS
                System.Diagnostics.Debug.WriteLine("Lỗi AWS: " + ex.Message);
                ViewBag.TotalRooms = 0; ViewBag.ActiveRooms = 0;
                ViewBag.OnlineDevices = 0; ViewBag.CurrentAlerts = 0;
            }

            // Các phần code Load ViewBags khác của bạn (Ví dụ: ViewBag.Blocks để tạo mã mời) giữ nguyên ở đây...

            return View();
        }

        // ==========================================
        // 2. QUẢN LÝ PHÒNG (Đọc dữ liệu từ bảng Rooms)
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> QuanLyPhong()
        {
            try
            {
                var client = GetClient();

                // Lấy Dãy
                var tableFacilities = Table.LoadTable(client, "Facilities");
                ViewBag.Blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();

                // Lấy Phòng Thực Tế
                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                // Lấy Users để xét Online/Offline
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
                        OwnerEmail = owner != null ? owner["userID"].AsString() : "Chưa có người thuê",
                        IsOnline = owner != null
                    });
                }

                // Sắp xếp danh sách phòng cho đẹp
                ViewBag.Rooms = roomList.OrderBy(r => r.BlockName).ThenBy(r => r.RoomId).ToList();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi lấy dữ liệu: Vui lòng kiểm tra đã tạo bảng Rooms trên AWS chưa. Lỗi chi tiết: " + ex.Message;
            }
            return View();
        }

        // ==========================================
        // THÊM VÀ XÓA PHÒNG LẺ
        // ==========================================
        [HttpPost]
        public async Task<ActionResult> ThemPhongMoi(string blockId, string roomNumber)
        {
            try
            {
                // Loại bỏ khoảng trắng thừa để AWS không bị lỗi
                blockId = blockId?.Trim();
                roomNumber = roomNumber?.Trim();

                // Bắt lỗi nếu Form gửi lên bị thiếu dữ liệu
                if (string.IsNullOrEmpty(blockId) || string.IsNullOrEmpty(roomNumber))
                {
                    TempData["ErrorMessage"] = "Dữ liệu bị trống, vui lòng nhập đầy đủ thông tin dãy và số phòng!";
                    return RedirectToAction("QuanLyPhong");
                }

                var client = GetClient();
                var table = Table.LoadTable(client, "Rooms");

                // 1. KIỂM TRA XEM PHÒNG ĐÃ TỒN TẠI HAY CHƯA
                var existingRoom = await table.GetItemAsync(blockId, roomNumber);
                if (existingRoom != null)
                {
                    // Nếu tìm thấy phòng rồi -> Báo lỗi đỏ và chặn lại luôn
                    TempData["ErrorMessage"] = $"Lỗi: Phòng {roomNumber} đã tồn tại trong Dãy {blockId} rồi! Không thể thêm trùng.";
                    return RedirectToAction("QuanLyPhong");
                }

                // 2. NẾU CHƯA CÓ THÌ MỚI TIẾN HÀNH THÊM MỚI
                var item = new Document();
                item["BlockId"] = blockId; // Phải khớp 100% với Partition Key
                item["RoomId"] = roomNumber; // Phải khớp 100% với Sort Key
                item["CreatedAt"] = DateTime.Now.ToString("O");

                await table.PutItemAsync(item);

                TempData["SuccessMessage"] = $"Đã thêm phòng {roomNumber} vào dãy {blockId} thành công!";
            }
            catch (Exception ex)
            {
                // Nếu AWS từ chối, sẽ hiện lỗi đỏ chi tiết
                TempData["ErrorMessage"] = "Lỗi từ hệ thống AWS: " + ex.Message;
            }

            return RedirectToAction("QuanLyPhong");
        }

        [HttpPost]
        public async Task<ActionResult> XoaPhong(string blockId, string roomId)
        {
            try
            {
                var client = GetClient();
                var table = Table.LoadTable(client, "Rooms");
                await table.DeleteItemAsync(blockId, roomId);
                TempData["SuccessMessage"] = $"Đã xóa phòng {roomId} thành công!";
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi xóa phòng: " + ex.Message; }
            return RedirectToAction("QuanLyPhong");
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

                // 1. Lấy tất cả các dãy từ bảng Facilities
                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();

                // 2. Lấy tất cả phòng và user để đếm số liệu Real-time
                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var tableUsers = Table.LoadTable(client, "Users");
                var allUsers = await tableUsers.Scan(new ScanFilter()).GetRemainingAsync();

                // 3. Quét từng dãy và tính toán lại con số thực tế
                foreach (var block in blocks)
                {
                    string bId = block["BlockId"].AsString();

                    // Lọc ra các phòng thuộc dãy này
                    var roomsInBlock = allRooms.Where(r => r.ContainsKey("BlockId") && r["BlockId"].AsString() == bId).ToList();

                    // Cập nhật Tổng số phòng thực tế
                    int realTotalRooms = roomsInBlock.Count;

                    // Đếm xem trong dãy này có bao nhiêu phòng có người ở
                    int realActiveRooms = 0;
                    foreach (var room in roomsInBlock)
                    {
                        string fullRoomId = $"{bId}-{room["RoomId"].AsString()}";
                        bool isOccupied = allUsers.Any(u => u.ContainsKey("AssignedRoom") && u["AssignedRoom"].AsString() == fullRoomId);
                        if (isOccupied)
                        {
                            realActiveRooms++;
                        }
                    }

                    // Gán dữ liệu Real-time ngược lại vào block để View hiển thị
                    // (Thao tác này chỉ thay đổi trên RAM để hiển thị, không làm đổi dữ liệu gốc trên AWS)
                    block["TotalRooms"] = realTotalRooms;
                    block["ActiveRooms"] = realActiveRooms;
                }

                ViewBag.Blocks = blocks.OrderBy(b => b["BlockName"].AsString()).ToList();
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

                // 1. Lưu Dãy vào Facilities
                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blockItem = new Document();
                blockItem["PK"] = "BLOCK";
                blockItem["SK"] = $"BLOCK#{Guid.NewGuid().ToString().Substring(0, 5)}";
                blockItem["BlockId"] = blockId;
                blockItem["BlockName"] = blockName;
                blockItem["Address"] = address;
                blockItem["TotalRooms"] = numberOfRooms;
                blockItem["ActiveRooms"] = 0;
                await tableFacilities.PutItemAsync(blockItem);

                // 2. TỰ ĐỘNG SINH 5 PHÒNG (101->105) VÀO BẢNG ROOMS
                var tableRooms = Table.LoadTable(client, "Rooms");
                for (int i = 1; i <= numberOfRooms; i++)
                {
                    var roomItem = new Document();
                    roomItem["BlockId"] = blockId;
                    roomItem["RoomId"] = $"10{i}";
                    roomItem["CreatedAt"] = DateTime.Now.ToString("O");
                    await tableRooms.PutItemAsync(roomItem);
                }

                TempData["SuccessMessage"] = $"Đã thêm {blockName} và tự động tạo {numberOfRooms} phòng thành công!";
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

                // Lấy phòng thực tế để bỏ vào Dropdown tạo mã
                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var roomList = new List<RoomDisplayViewModel>();
                foreach (var r in allRooms)
                {
                    string bId = r["BlockId"].AsString();
                    string rId = r["RoomId"].AsString();
                    roomList.Add(new RoomDisplayViewModel
                    {
                        RoomName = "Phòng " + bId + "-" + rId,
                        BlockName = bId,
                        RoomId = rId
                    });
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
                string randomString = Guid.NewGuid().ToString().Substring(0, 6).ToUpper();
                string newInviteCode = $"{selectedRoom}-{randomString}";

                var client = GetClient();
                var table = Table.LoadTable(client, "RoomInvites");

                var item = new Document();
                item["InviteCode"] = newInviteCode;
                item["RoomId"] = selectedRoom;
                item["ExpireHours"] = expireHours;
                item["IsUsed"] = false;
                item["CreatedAt"] = DateTime.UtcNow.ToString("O");

                await table.PutItemAsync(item);
                TempData["SuccessMessage"] = $"Tạo mã thành công: {newInviteCode}";

                string referer = Request.UrlReferrer?.AbsolutePath;
                if (referer != null && referer.Contains("Admin/Index")) return RedirectToAction("Index");
                return RedirectToAction("TaoMaKichHoat");
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi: " + ex.Message; return RedirectToAction("TaoMaKichHoat"); }
        }

        public async Task<ActionResult> LichSuCanhBao()
        {
            var alertList = new List<AlertHistoryViewModel>();

            try
            {
                // 1. Khởi tạo kết nối DynamoDB (Region apsoutheast-1 như trong ảnh của bạn)
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);

                // 2. Load bảng SafeDorm_History
                var table = Table.LoadTable(client, "SafeDorm_History");

                // 3. Quét dữ liệu (Scan)
                // Lưu ý: Đang dùng Scan để lấy hết rồi lọc ở code cho dễ hiểu. 
                var scanFilter = new ScanFilter();
                var search = table.Scan(scanFilter);
                var documentList = await search.GetNextSetAsync();

                // 4. Xử lý và lọc dữ liệu
                foreach (var doc in documentList)
                {
                    double temp = doc["temperature"].AsDouble();

                    // CHỈ XỬ LÝ NHỮNG CA CÓ NHIỆT ĐỘ >= 38 (Lọc bỏ phòng bình thường cho nhẹ)
                    if (temp >= 38)
                    {
                        string roomId = doc["room_id"].AsString();
                        long unixTimestamp = doc["timestamp"].AsLong();

                        // Chuyển đổi Unix timestamp (số) sang DateTime của C#
                        DateTime timeStamp = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                                                .AddSeconds(unixTimestamp)
                                                .ToLocalTime();

                        // Tự động sinh mô tả
                        string description = "";
                        if (temp >= 50)
                            description = "[KHẨN CẤP] Nhiệt độ vượt ngưỡng an toàn nghiêm trọng. Nguy cơ cháy nổ cao!";
                        else if (temp >= 45)
                            description = "Nhiệt độ tăng cao bất thường. Cần kiểm tra thiết bị hoặc phòng ngay.";
                        else
                            description = "Cảnh báo ngưỡng 1: Nhiệt độ phòng đang có dấu hiệu nóng lên.";

                        alertList.Add(new AlertHistoryViewModel
                        {
                            TimeStamp = timeStamp,
                            RoomId = "Phòng " + roomId,
                            Temperature = temp,
                            Description = description
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // Hiển thị lỗi ra console hoặc truyền ra View nếu mất kết nối AWS
                System.Diagnostics.Debug.WriteLine("Lỗi AWS DynamoDB: " + ex.Message);
            }

            // 5. Sắp xếp thời gian mới nhất lên đầu trang
            alertList = alertList.OrderByDescending(x => x.TimeStamp).ToList();

            return View(alertList);
        }
        public async Task<ActionResult> XuatExcelLichSu()
        {
            // 1. Lấy dữ liệu (Ở đây bạn dùng lại đoạn code lấy từ DynamoDB nhé)
            // Giả sử sau khi quét DynamoDB và lọc, bạn có list này:
            var alertList = new List<AlertHistoryViewModel>();
            // ... (code lấy data của bạn) ...

            // 2. Tạo nội dung file CSV
            var builder = new StringBuilder();

            // Tạo dòng tiêu đề (Header)
            builder.AppendLine("Thời Gian,Phòng,Mức Nhiệt Độ,Mô Tả Cảnh Báo");

            // Lặp qua dữ liệu để tạo các dòng
            foreach (var item in alertList)
            {
                // Chú ý: Cột mô tả có thể có dấu phẩy, nên ta bao nó trong ngoặc kép ""
                builder.AppendLine($"{item.TimeStamp:dd/MM/yyyy HH:mm:ss},{item.RoomId},{item.Temperature},\"{item.Description}\"");
            }

            // 3. Trả về file cho trình duyệt tải xuống
            // Dùng UTF8 Encoding và có BOM để Excel tiếng Việt không bị lỗi font
            byte[] buffer = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
            return File(buffer, "text/csv", $"LichSuCanhBao_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        }
        public ActionResult BaoCaoAI() => View();
    }
}