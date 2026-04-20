using SafeGuard.Models;
using SafeGuard.Services;
using Amazon.CognitoIdentityProvider.Model;
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

                    // Điều hướng theo đúng chức vụ
                    if (userGroup == "ADMIN")
                    {
                        return RedirectToAction("Index", "Admin"); // Trả về Dashboard tổng quan của chủ trọ
                    }
                    else
                    {
                        return RedirectToAction("QuanLyPhong", "Tenant"); // Trả về trang nhập mã của người thuê
                    }
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

            bool accountConfirmed = false;

            try
            {
                await _cognito.ConfirmSignUpAsync(model.Username, model.Code);
                accountConfirmed = true;
            }
            catch (NotAuthorizedException ex)
            {
                if (ex.Message.IndexOf("CONFIRMED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    accountConfirmed = true;
                }
                else
                {
                    ModelState.AddModelError("", "Lỗi xác nhận: " + ex.Message);
                    return View(model);
                }
            }
            catch (CodeMismatchException)
            {
                ModelState.AddModelError("", "Mã xác nhận không đúng. Vui lòng kiểm tra email hoặc bấm gửi lại mã.");
                return View(model);
            }
            catch (ExpiredCodeException)
            {
                ModelState.AddModelError("", "Mã xác nhận đã hết hạn. Vui lòng bấm gửi lại mã để nhận mã mới.");
                return View(model);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", "Lỗi xác nhận: " + ex.Message);
                return View(model);
            }

            if (accountConfirmed)
            {
                string adminCode = (model.AdminCode ?? "").Trim();

                if (!string.IsNullOrEmpty(adminCode))
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
                                upgradeCode = adminCode
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
                                await _cognito.AddUserToGroupAsync(model.Username, "TENANT");
                                TempData["Warning"] = "Tài khoản đã được kích hoạt, nhưng mã quản lý không hợp lệ hoặc đã hết hạn. Tài khoản hiện được đưa vào nhóm Người thuê.";
                                return RedirectToAction("Login");
                            }
                        }
                    }
                    catch (Exception apiEx)
                    {
                        try
                        {
                            await _cognito.AddUserToGroupAsync(model.Username, "TENANT");
                        }
                        catch { }

                        TempData["Warning"] = "Tài khoản đã được kích hoạt, nhưng chưa nâng quyền Quản lý được: " + apiEx.Message;
                        return RedirectToAction("Login");
                    }
                }

                await _cognito.AddUserToGroupAsync(model.Username, "TENANT");
                TempData["Success"] = "Kích hoạt tài khoản thành công! Vui lòng đăng nhập.";
                return RedirectToAction("Login");
            }

            ModelState.AddModelError("", "Không thể xác nhận tài khoản. Vui lòng thử lại.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ResendConfirmationCode(string username, string displayEmail)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                TempData["ConfirmError"] = "Không tìm thấy tài khoản để gửi lại mã.";
                return RedirectToAction("Register");
            }

            try
            {
                await _cognito.ResendConfirmationCodeAsync(username);
                TempData["ConfirmMessage"] = "Đã gửi lại mã xác nhận mới. Vui lòng kiểm tra email.";
            }
            catch (LimitExceededException)
            {
                TempData["ConfirmError"] = "Bạn vừa yêu cầu gửi mã quá nhiều lần. Vui lòng chờ một lúc rồi thử lại.";
            }
            catch (InvalidParameterException ex)
            {
                TempData["ConfirmError"] = "Không thể gửi lại mã: " + ex.Message;
            }
            catch (Exception ex)
            {
                TempData["ConfirmError"] = "Lỗi gửi lại mã: " + ex.Message;
            }

            return RedirectToAction("ConfirmSignUp", new { username = username, email = displayEmail });
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
