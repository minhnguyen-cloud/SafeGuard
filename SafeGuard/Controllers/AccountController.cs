using SafeGuard.Models;
using SafeGuard.Services;
using System;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace SafeGuard.Controllers
{
    public class AccountController : Controller
    {
        // Khởi tạo kết nối với Cognito Service
        private readonly CognitoAuthService _cognito = new CognitoAuthService();

        // ==========================
        // 1. ĐĂNG NHẬP (LOGIN)
        // ==========================
        [HttpGet]
        public ActionResult Login() => View(new LoginViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid) return View(model);
            try
            {
                var authResult = await _cognito.LoginAsync(model.Username, model.Password);

                if (authResult.AuthenticationResult != null)
                {
                    Session["AccessToken"] = authResult.AuthenticationResult.AccessToken;
                    Session["IdToken"] = authResult.AuthenticationResult.IdToken;
                    Session["UserEmail"] = model.Username;

                    // Bóc cái IdToken ra để lấy chức vụ lưu vào Session
                    string userGroup = ExtractGroupFromToken(authResult.AuthenticationResult.IdToken);
                    Session["UserGroup"] = userGroup;

                    return RedirectToAction("Index", "Home");
                }
                ModelState.AddModelError("", "Đăng nhập thất bại.");
                return View(model);
            }
            catch (Exception)
            {
                ModelState.AddModelError("", "Tài khoản hoặc mật khẩu không đúng.");
                return View(model);
            }
        }

        // ==========================
        // 2. ĐĂNG KÝ (REGISTER)
        // ==========================
        [HttpGet]
        public ActionResult Register() => View(new RegisterViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            try
            {
                await _cognito.SignUpAsync(model.Email, model.Password, model.FullName, model.Username);

                // Đăng ký xong, chuyển sang trang nhập mã 6 số để kích hoạt
                TempData["UserEmail"] = model.Email;
                return RedirectToAction("ConfirmSignUp", new { username = model.Username, email = model.Email });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", ex.Message);
                return View(model);
            }
        }

        // ==========================
        // 3. XÁC NHẬN MÃ (CONFIRM)
        // ==========================

        // HÀM NÀY BỊ THIẾU LÚC NÃY NÈ (Hiển thị giao diện)
        [HttpGet]
        public ActionResult ConfirmSignUp(string username, string email)
        {
            return View(new ConfirmSignUpViewModel { Username = username, DisplayEmail = email });
        }

        // Hàm này xử lý khi nhấn nút XÁC NHẬN
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ConfirmSignUp(ConfirmSignUpViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            try
            {
                // 1. Xác nhận mã 6 số 
                await _cognito.ConfirmSignUpAsync(model.Username, model.Code);

                // 2. Add Group - Phân quyền tự động
                try
                {
                    string maBiMatCuaDung = "SAFEGUARD2026";

                    // Nếu có mã và đúng mã -> ADMIN. Không thì -> TENANT
                    if (!string.IsNullOrEmpty(model.AdminCode) && model.AdminCode == maBiMatCuaDung)
                    {
                        await _cognito.AddUserToGroupAsync(model.Username, "ADMIN");
                    }
                    else
                    {
                        await _cognito.AddUserToGroupAsync(model.Username, "TENANT");
                    }
                }
                catch (Exception groupEx)
                {
                    // Lỗi IAM sẽ hiện đỏ ở đây
                    ModelState.AddModelError("", "Lỗi phân quyền IAM (Chưa add được vào Group): " + groupEx.Message);
                    return View(model);
                }

                TempData["Success"] = "Kích hoạt và Phân quyền thành công! Vui lòng đăng nhập.";
                return RedirectToAction("Login");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", "Lỗi xác nhận: " + ex.Message);
                return View(model);
            }
        }

        // ==========================
        // 4. QUÊN MẬT KHẨU (FORGOT)
        // ==========================
        [HttpGet]
        public ActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            try
            {
                await _cognito.ForgotPasswordAsync(model.Email);
                // Gửi mã xong thì chuyển sang trang nhập mã và đặt pass mới
                return RedirectToAction("ResetPassword", new { username = model.Email });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", ex.Message);
                return View(model);
            }
        }

        // ==========================
        // 5. ĐẶT LẠI PASS (RESET)
        // ==========================
        [HttpGet]
        public ActionResult ResetPassword(string username)
        {
            return View(new ResetPasswordViewModel { Email = username });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            try
            {
                // Gọi AWS để đổi pass
                await _cognito.ConfirmForgotPasswordAsync(model.Email, model.Code, model.NewPassword);

                TempData["Success"] = "Đã đổi mật khẩu thành công! Vui lòng đăng nhập lại với mật khẩu mới.";
                return RedirectToAction("Login");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", "Lỗi AWS: " + ex.Message);
                return View(model);
            }
        }

        // ==========================
        // 6. ĐĂNG XUẤT (LOGOUT)
        // ==========================
        public ActionResult Logout()
        {
            Session.Clear(); // Xóa hết Token đã lưu
            return RedirectToAction("Login");
        }

        // ==========================
        // HÀM PHỤ TRỢ: ĐỌC TOKEN
        // ==========================
        private string ExtractGroupFromToken(string idToken)
        {
            try
            {
                // Token của AWS có 3 phần, phần số 2 chứa thông tin user
                var payload = idToken.Split('.')[1];

                // Cân bằng chuỗi Base64
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                // Dịch từ mã máy sang mã người đọc được
                var jsonBytes = Convert.FromBase64String(payload);
                var jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes);

                // Tìm xem trong thông tin có chữ ADMIN không
                if (jsonString.Contains("\"cognito:groups\":[\"ADMIN\"]") || jsonString.Contains("\"cognito:groups\": [\"ADMIN\"]"))
                {
                    return "ADMIN";
                }
            }
            catch { }

            return "TENANT"; // Nếu lỗi hoặc không thấy, mặc định cho làm người thuê
        }
    }
}