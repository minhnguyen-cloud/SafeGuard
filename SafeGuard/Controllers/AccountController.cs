using SafeGuard.Models;
using SafeGuard.Services;
using System;
using System.Net.Http; // Thêm thư viện này để gọi API
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
                // 1. Xác nhận mã 6 số (Khi dòng này chạy thành công, Lambda 1 sẽ tự động lưu user vào DynamoDB với quyền TENANT)
                await _cognito.ConfirmSignUpAsync(model.Username, model.Code);

                // 2. Kiểm tra xem người dùng có nhập mã Quản lý không để gọi Lambda 2 (Nâng quyền)
                if (!string.IsNullOrEmpty(model.AdminCode))
                {
                    try
                    {
                        using (var client = new HttpClient())
                        {
                            // Chuẩn bị dữ liệu gửi lên API Gateway
                            var payload = new
                            {
                                userID = model.Username, // Khớp với cách bạn setup Cognito
                                username = model.Username,
                                upgradeCode = model.AdminCode
                            };

                            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
                            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                            // ⚠️ QUAN TRỌNG: DÁN INVOKE URL CỦA API GATEWAY VÀO ĐÂY (Nhớ giữ đuôi /upgrade)
                            string apiUrl = "https://nycp96odz5.execute-api.ap-southeast-1.amazonaws.com/upgrade";

                            // Gửi yêu cầu lên AWS Lambda
                            var response = await client.PostAsync(apiUrl, content);

                            if (response.IsSuccessStatusCode)
                            {
                                TempData["Success"] = "Kích hoạt và phân quyền Quản lý thành công! Vui lòng đăng nhập.";
                                return RedirectToAction("Login");
                            }
                            else
                            {
                                // Kích hoạt email thành công nhưng mã Admin sai
                                ModelState.AddModelError("", "Kích hoạt tài khoản thành công, nhưng mã nâng cấp không hợp lệ hoặc đã hết hạn.");
                                return View(model);
                            }
                        }
                    }
                    catch (Exception apiEx)
                    {
                        ModelState.AddModelError("", "Lỗi khi gọi API nâng cấp: " + apiEx.Message);
                        return View(model);
                    }
                }

                // Nếu không nhập mã Admin thì báo thành công bình thường (User là Tenant)
                TempData["Success"] = "Kích hoạt tài khoản thành công! Vui lòng đăng nhập.";
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
                var payload = idToken.Split('.')[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                var jsonBytes = Convert.FromBase64String(payload);
                var jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes);

                if (jsonString.Contains("\"cognito:groups\":[\"ADMIN\"]") || jsonString.Contains("\"cognito:groups\": [\"ADMIN\"]"))
                {
                    return "ADMIN";
                }
            }
            catch { }

            return "TENANT";
        }
    }
}