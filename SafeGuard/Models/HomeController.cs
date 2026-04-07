using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace SafeGuard.Models
{
    public class HomeController : Controller
    {
        // GET: Home
        public ActionResult Index()
        {
            return View();
        }

        // THÊM HÀM NÀY VÀO ĐỂ TẠO TRANG TÍNH NĂNG
        public ActionResult Features()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG LỢI ÍCH
        public ActionResult Benefits()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG TIN TỨC
        public ActionResult News()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG LIÊN HỆ
        public ActionResult Contact()
        {
            return View();
        }
        // THÊM HÀM NÀY CHO TRANG THIẾT BỊ
        public ActionResult Devices()
        {
            return View();
        }
        public ActionResult SafetyRegulations()
        {
            ViewBag.Message = "Quy định Phòng cháy Chữa cháy tại Nhà trọ SafeGuard";
            return View();
        }
    }

}