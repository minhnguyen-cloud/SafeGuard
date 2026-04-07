using System;
using System.Threading.Tasks;
using System.Web.Mvc;
using SafeGuard.Filters;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using System.Linq;
using System.Collections.Generic;
using SafeGuard.Models;

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
            try
            {
                var client = GetClient();
                var table = Table.LoadTable(client, "Facilities");
                ViewBag.Blocks = await table.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();
            }
            catch (Exception ex) { TempData["ErrorMessage"] = "Lỗi Dashboard: " + ex.Message; }
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

        public ActionResult LichSuCanhBao() => View();
        public ActionResult BaoCaoAI() => View();
    }
}