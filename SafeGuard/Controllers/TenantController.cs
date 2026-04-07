using System.Web.Mvc;
using SafeGuard.Filters;

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "TENANT")]
    public class TenantController : Controller
    {
        public ActionResult Index() => View();
        public ActionResult LichSuCanhBao() => View();
        public ActionResult NoiQuy() => View();
        public ActionResult ThongTinCaNhan() => View();
        public ActionResult QuanLyPhong() => View();
        public ActionResult PhanTichAI() => View(); // Thêm phần AI cho người thuê
    }
}