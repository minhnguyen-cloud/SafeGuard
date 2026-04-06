using System.Web;
using System.Web.Mvc;

namespace SafeGuard.Filters
{
    public class RoleAuthorizeAttribute : AuthorizeAttribute
    {
        // Nhận tên quyền truyền vào (VD: "ADMIN" hoặc "TENANT")
        public string Role { get; set; }

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            // 1. Nếu chưa đăng nhập (Session trống) -> Từ chối
            if (httpContext.Session["UserGroup"] == null)
            {
                return false;
            }

            // 2. Nếu không yêu cầu quyền cụ thể, chỉ cần đăng nhập là được
            if (string.IsNullOrEmpty(Role))
            {
                return true;
            }

            // 3. Kiểm tra xem quyền trong Session có khớp với quyền yêu cầu không
            return httpContext.Session["UserGroup"].ToString() == Role;
        }

        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            if (filterContext.HttpContext.Session["UserGroup"] == null)
            {
                // Chưa đăng nhập thì đá về trang Login
                filterContext.Result = new RedirectResult("~/Account/Login");
            }
            else
            {
                // Có tài khoản nhưng đi nhầm vào khu vực không được phép (Ví dụ Tenant mò vào trang Admin) -> Đá về Home
                // Bạn có thể tạo riêng trang Error 403 Access Denied sau
                filterContext.Result = new RedirectResult("~/Home/Index");
            }
        }
    }
}