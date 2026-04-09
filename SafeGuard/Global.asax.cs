using System;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;

namespace SafeGuard
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            RouteConfig.RegisterRoutes(RouteTable.Routes);

            // Kích hoạt quét nhiệt độ chạy ngầm
            // Chú ý: Kiểm tra file Helpers/EmailWorker.cs đã được tạo chưa
            try
            {
                SafeGuard.Helpers.EmailWorker.Start();
            }
            catch { }
        }
    }
}