using System;
using System.Threading.Tasks;
using System.Web.Mvc;
using SafeGuard.Filters;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "TENANT")] // CHỈ NGƯỜI THUÊ MỚI VÀO ĐƯỢC
    public class TenantController : Controller
    {
        // Các trang giao diện cơ bản
        public ActionResult Index() => View();
        public ActionResult LichSuCanhBao() => View();
        public ActionResult NoiQuy() => View();
        public ActionResult ThongTinCaNhan() => View();
        public ActionResult QuanLyPhong() => View();
        public ActionResult PhanTichAI() => View();

        // Hàm xử lý logic khi người thuê bấm XÁC NHẬN MÃ
        [HttpPost]
        public async Task<ActionResult> XacNhanMaPhong(string inviteCode)
        {
            // 1. Kiểm tra xem có nhập mã chưa
            if (string.IsNullOrEmpty(inviteCode))
            {
                TempData["ErrorMessage"] = "Vui lòng nhập mã phòng!";
                return RedirectToAction("QuanLyPhong");
            }

            try
            {
                var client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
                var inviteTable = Table.LoadTable(client, "RoomInvites");

                // 2. Tìm mã trong bảng RoomInvites trên AWS
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

                // ==========================================
                // 3. KIỂM TRA THỜI HẠN (HẠN SỬ DỤNG)
                // ==========================================
                if (inviteItem.ContainsKey("CreatedAt") && inviteItem.ContainsKey("ExpireHours"))
                {
                    DateTime createdAt = DateTime.Parse(inviteItem["CreatedAt"].AsString());
                    int expireHours = inviteItem["ExpireHours"].AsInt();

                    // Nếu thời gian hiện tại (UTC) lớn hơn thời gian tạo + số giờ cho phép -> Đã hết hạn
                    if (DateTime.UtcNow > createdAt.AddHours(expireHours))
                    {
                        TempData["ErrorMessage"] = "Mã kích hoạt này đã hết hạn sử dụng. Vui lòng xin mã mới từ Chủ trọ!";
                        return RedirectToAction("QuanLyPhong");
                    }
                }

                // 4. Nếu mã đúng, chưa dùng và còn hạn: Cập nhật mã này thành ĐÃ SỬ DỤNG
                string roomId = inviteItem["RoomId"].AsString();
                inviteItem["IsUsed"] = true;
                await inviteTable.UpdateItemAsync(inviteItem);

                // 5. Gán phòng cho User hiện tại (Lưu vào bảng Users)
                if (Session["UserEmail"] != null)
                {
                    string userEmail = Session["UserEmail"].ToString();
                    var usersTable = Table.LoadTable(client, "Users");

                    var userDoc = new Document();
                    userDoc["userID"] = userEmail; // Tùy vào Partition Key bảng Users của bạn
                    userDoc["AssignedRoom"] = roomId; // Cập nhật cột AssignedRoom

                    await usersTable.UpdateItemAsync(userDoc);

                    // Lưu vào Session để các trang khác lấy ra dùng nhanh mà không cần gọi DB
                    Session["AssignedRoom"] = roomId;
                }

                // Báo cáo thành công!
                TempData["SuccessMessage"] = $"Chúc mừng! Bạn đã gia nhập phòng {roomId} thành công. Hệ thống cảnh báo đã được kích hoạt.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi hệ thống khi kết nối AWS: " + ex.Message;
            }

            return RedirectToAction("QuanLyPhong");
        }
    }
}