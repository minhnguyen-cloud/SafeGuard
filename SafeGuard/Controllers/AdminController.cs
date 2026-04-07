using System;
using System.Threading.Tasks;
using System.Web.Mvc;
using SafeGuard.Filters;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using System.Linq;
using System.Collections.Generic;

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "ADMIN")] // CHỈ ADMIN MỚI ĐƯỢC VÀO CONTROLLER NÀY
    public class AdminController : Controller
    {
        // ==========================================
        // 1. TỔNG QUAN (DASHBOARD)
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> Index()
        {
            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "Facilities");

                // Lấy tất cả các dòng là DÃY TRỌ (PK = BLOCK) để đổ vào Dropdown tạo mã
                var filter = new QueryFilter("PK", QueryOperator.Equal, "BLOCK");
                var search = table.Query(filter);
                var blocks = await search.GetRemainingAsync();

                ViewBag.Blocks = blocks;
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi tải danh sách dãy tại Dashboard: " + ex.Message;
            }
            return View();
        }

        public ActionResult QuanLyPhong() => View();
        public ActionResult LichSuCanhBao() => View();
        public ActionResult BaoCaoAI() => View();

        // ==========================================
        // 2. QUẢN LÝ DÃY TRỌ (Hiển thị & Tự động đếm phòng đang thuê)
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> QuanLyDayTro()
        {
            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);

                // Lấy dữ liệu các Dãy từ bảng Facilities
                var tableFacilities = Table.LoadTable(client, "Facilities");
                var filter = new QueryFilter("PK", QueryOperator.Equal, "BLOCK");
                var blocks = await tableFacilities.Query(filter).GetRemainingAsync();

                // Lấy toàn bộ mã đã tạo từ bảng RoomInvites để đếm số lượng "Đang thuê"
                var tableInvites = Table.LoadTable(client, "RoomInvites");
                var allInvites = await tableInvites.Scan(new ScanFilter()).GetRemainingAsync();

                foreach (var block in blocks)
                {
                    string blockId = block["BlockId"].AsString();

                    // Đếm các mã đã dùng (IsUsed = true) thuộc về dãy này (dựa vào tiền tố RoomId)
                    int rentedCount = allInvites.Count(i =>
                        i.ContainsKey("IsUsed") && i["IsUsed"].AsBoolean() == true &&
                        i.ContainsKey("RoomId") && i["RoomId"].AsString().StartsWith(blockId)
                    );

                    // Cập nhật con số đang thuê động vào dữ liệu hiển thị
                    block["ActiveRooms"] = rentedCount;
                }

                ViewBag.Blocks = blocks;
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi tải dữ liệu dãy trọ: " + ex.Message;
            }
            return View();
        }

        // ==========================================
        // 3. THÊM DÃY MỚI (Lưu thông tin dãy vào DynamoDB)
        // ==========================================
        [HttpPost]
        public async Task<ActionResult> ThemDayMoi(string blockName, string address, int numberOfRooms)
        {
            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "Facilities");

                // LẤY TÊN DÃY LÀM ID LUÔN (Ví dụ nhập "Dãy SS" thì lấy "SS")
                // Nếu tên quá dài thì lấy 3 ký tự đầu, viết hoa hết.
                string blockId = blockName.Trim().ToUpper();
                if (blockId.Contains(" "))
                    blockId = blockId.Split(' ').Last(); // Lấy chữ cuối cùng nếu có dấu cách

                var blockItem = new Document();
                blockItem["PK"] = "BLOCK";
                blockItem["SK"] = $"BLOCK#{Guid.NewGuid().ToString().Substring(0, 5)}"; // SK vẫn giữ ngẫu nhiên để tránh trùng
                blockItem["BlockId"] = blockId; // Đây là cái sẽ hiện trên mã: SS, A, B...
                blockItem["BlockName"] = blockName;
                blockItem["Address"] = address;
                blockItem["TotalRooms"] = numberOfRooms;
                blockItem["ActiveRooms"] = 0;

                await table.PutItemAsync(blockItem);
                TempData["SuccessMessage"] = $"Đã thêm {blockName} thành công!";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi: " + ex.Message;
            }
            return RedirectToAction("QuanLyDayTro");
        }

        // ==========================================
        // 4. TẠO MÃ KÍCH HOẠT (HÀM GET - Load danh sách mã)
        // ==========================================
        [HttpGet]
        public async Task<ActionResult> TaoMaKichHoat()
        {
            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);

                // 1. Lấy danh sách mã mời (giữ nguyên code cũ)
                var tableInvites = Table.LoadTable(client, "RoomInvites");
                var invites = await tableInvites.Scan(new ScanFilter()).GetRemainingAsync();
                ViewBag.InviteList = invites;

                // 2. THÊM ĐOẠN NÀY: Lấy danh sách Dãy trọ thực tế từ DynamoDB
                var tableFacilities = Table.LoadTable(client, "Facilities");
                var filter = new QueryFilter("PK", QueryOperator.Equal, "BLOCK");
                var blocks = await tableFacilities.Query(filter).GetRemainingAsync();
                ViewBag.Blocks = blocks;
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi tải dữ liệu: " + ex.Message;
            }
            return View();
        }

        // ==========================================
        // 5. PHÁT SINH MÃ MỚI (HÀM POST)
        // ==========================================
        [HttpPost]
        public async Task<ActionResult> TaoMaMoi(string selectedRoom, int expireHours)
        {
            try
            {
                // Sinh mã ngẫu nhiên: [Phòng]-[6 ký tự]
                string randomString = Guid.NewGuid().ToString().Substring(0, 6).ToUpper();
                string newInviteCode = $"{selectedRoom}-{randomString}";

                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var table = Table.LoadTable(client, "RoomInvites");

                var item = new Document();
                item["InviteCode"] = newInviteCode;
                item["RoomId"] = selectedRoom;
                item["ExpireHours"] = expireHours;
                item["IsUsed"] = false;
                item["CreatedAt"] = DateTime.UtcNow.ToString("O");

                await table.PutItemAsync(item);

                TempData["SuccessMessage"] = $"Tạo mã thành công: {newInviteCode} cho {selectedRoom}";

                // Nếu tạo từ trang Index thì trả về Index, nếu từ trang TaoMa thì trả về TaoMa
                string referer = Request.UrlReferrer?.AbsolutePath;
                if (referer != null && referer.Contains("Admin/Index"))
                {
                    return RedirectToAction("Index");
                }
                return RedirectToAction("TaoMaKichHoat");
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi khi lưu lên AWS: " + ex.Message;
                return RedirectToAction("TaoMaKichHoat");
            }
        }
    }
}