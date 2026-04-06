using System.Web.Mvc;
using SafeGuard.Filters; // Nhớ using thư mục Filters

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "ADMIN")] // CHỈ ADMIN MỚI ĐƯỢC VÀO CONTROLLER NÀY
    public class AdminController : Controller
    {
        public ActionResult Index()
        {
            return View(); // Trả về giao diện Dashboard của Admin
        }
        public ActionResult QuanLyDayTro() => View();

        public ActionResult QuanLyPhong() => View();
        public ActionResult TaoMaKichHoat() => View();
        public ActionResult LichSuCanhBao() => View();
        public ActionResult BaoCaoAI() => View();
    }
}