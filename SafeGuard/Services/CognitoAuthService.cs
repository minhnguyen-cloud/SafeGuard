using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading.Tasks;

namespace SafeGuard.Services
{
    public class CognitoAuthService
    {
        private readonly AmazonCognitoIdentityProviderClient _client;
        private readonly string _clientId;

        public CognitoAuthService()
        {
            var region = ConfigurationManager.AppSettings["AWSRegion"];
            _clientId = ConfigurationManager.AppSettings["CognitoClientId"];
            _client = new AmazonCognitoIdentityProviderClient(RegionEndpoint.GetBySystemName(region));
        }

        // 1. Đăng ký
        public async Task SignUpAsync(string email, string password, string fullName, string username)
        {
            var request = new SignUpRequest
            {
                ClientId = _clientId,
                Username = username,
                Password = password,
                UserAttributes = new List<AttributeType> {
                    new AttributeType { Name = "email", Value = email },
                    new AttributeType { Name = "name", Value = fullName }
                }
            };
            await _client.SignUpAsync(request);
        }

        // 2. Xác nhận mã
        public async Task ConfirmSignUpAsync(string username, string code)
        {
            var request = new ConfirmSignUpRequest
            {
                ClientId = _clientId,
                Username = username,
                ConfirmationCode = code
            };
            await _client.ConfirmSignUpAsync(request);
        }

        // 3. Đăng nhập (Dùng luồng USER_PASSWORD_AUTH cho Public Client)
        public async Task<InitiateAuthResponse> LoginAsync(string username, string password)
        {
            var request = new InitiateAuthRequest
            {
                ClientId = _clientId,
                AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                AuthParameters = new Dictionary<string, string> {
                    { "USERNAME", username },
                    { "PASSWORD", password }
                }
            };
            return await _client.InitiateAuthAsync(request);
        }

        // 4. Quên mật khẩu
        public async Task ForgotPasswordAsync(string username)
        {
            await _client.ForgotPasswordAsync(new ForgotPasswordRequest { ClientId = _clientId, Username = username });
        }

        // 5. Đặt lại mật khẩu
        public async Task ConfirmForgotPasswordAsync(string username, string code, string newPassword)
        {
            await _client.ConfirmForgotPasswordAsync(new ConfirmForgotPasswordRequest
            {
                ClientId = _clientId,
                Username = username,
                ConfirmationCode = code,
                Password = newPassword
            });
        }
        public async Task AddUserToGroupAsync(string username, string groupName)
        {
            // Lấy Key một cách an toàn từ file Web.config
            var accessKey = ConfigurationManager.AppSettings["AWSAccessKey"];
            var secretKey = ConfigurationManager.AppSettings["AWSSecretKey"];

            // Cấp quyền Admin cho C# bằng key bảo mật
            var adminClient = new AmazonCognitoIdentityProviderClient(
                accessKey,
                secretKey,
                RegionEndpoint.APSoutheast1
            );

            var request = new AdminAddUserToGroupRequest
            {
                UserPoolId = ConfigurationManager.AppSettings["CognitoUserPoolId"],
                Username = username,
                GroupName = groupName
            };
            await adminClient.AdminAddUserToGroupAsync(request);
        }
    }
}