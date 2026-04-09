using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;

namespace SafeGuard.Helpers
{
    public class EmailWorker
    {
        private static Timer _timer;
        private static readonly AmazonDynamoDBClient _client = new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
        private static bool _isProcessing = false;

        // BỘ NHỚ TẠM: Lưu trữ [Tên phòng - Thời điểm gửi mail gần nhất] để tránh gửi liên tục
        private static Dictionary<string, DateTime> _sentAlerts = new Dictionary<string, DateTime>();

        public static void Start()
        {
            // Cứ 5 giây (5000ms) quét DynamoDB 1 lần để check nhiệt độ
            _timer = new Timer(async (e) => await CheckAndSendAlerts(), null, 0, 5000);
        }

        private static async Task CheckAndSendAlerts()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                var historyTable = Table.LoadTable(_client, "SafeDorm_History");
                var usersTable = Table.LoadTable(_client, "Users");

                // 1. Lấy dữ liệu nhiệt độ trong 5 phút gần đây
                long fiveMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
                var filter = new ScanFilter();
                filter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, fiveMinsAgo);

                var recentLogs = await historyTable.Scan(filter).GetNextSetAsync();

                // Lọc ra những phòng có nhiệt độ >= 50, lấy bản ghi mới nhất của mỗi phòng
                var dangerLogs = recentLogs.Where(d => d["temperature"].AsDouble() >= 50)
                                           .GroupBy(d => d["room_id"].AsString())
                                           .Select(g => g.OrderByDescending(x => x["timestamp"].AsLong()).First());

                foreach (var log in dangerLogs)
                {
                    string roomId = log["room_id"].AsString();
                    double currentTemp = log["temperature"].AsDouble();

                    // KIỂM TRA: Nếu chưa gửi mail cho phòng này HOẶC đã gửi nhưng cách đây hơn 10 phút
                    if (!_sentAlerts.ContainsKey(roomId) || (DateTime.Now - _sentAlerts[roomId]).TotalMinutes > 10)
                    {
                        // 2. Tìm Email của người thuê phòng này trong bảng Users
                        var userFilter = new ScanFilter();
                        userFilter.AddCondition("AssignedRoom", ScanOperator.Equal, roomId);
                        var usersInRoom = await usersTable.Scan(userFilter).GetRemainingAsync();

                        if (usersInRoom.Count > 0)
                        {
                            string targetEmail = usersInRoom[0].ContainsKey("email") ? usersInRoom[0]["email"].AsString() :
                                                (usersInRoom[0].ContainsKey("mail") ? usersInRoom[0]["mail"].AsString() : "");

                            if (!string.IsNullOrEmpty(targetEmail) && targetEmail.Contains("@"))
                            {
                                await SendEmergencyGmail(targetEmail, roomId, currentTemp);

                                // Cập nhật thời điểm gửi mail để không gửi lặp lại ngay lập tức
                                _sentAlerts[roomId] = DateTime.Now;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi Worker: " + ex.Message);
            }
            finally { _isProcessing = false; }
        }

        private static async Task SendEmergencyGmail(string toEmail, string room, double temp)
        {
            try
            {
                // THÔNG TIN BOT GỬI THƯ (Dùng mã 16 ký tự viết liền)
                string botEmail = "adminsafeguard@gmail.com";
                string botPassword = "czachhzqyziqqbwt";

                var fromAddress = new MailAddress(botEmail, "SafeGuard Fire Alert");
                var toAddress = new MailAddress(toEmail);

                var smtp = new SmtpClient
                {
                    Host = "smtp.gmail.com",
                    Port = 587,
                    EnableSsl = true,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(fromAddress.Address, botPassword)
                };

                using (var message = new MailMessage(fromAddress, toAddress)
                {
                    Subject = $"🔥 [KHẨN CẤP] CẢNH BÁO CHÁY PHÒNG {room}",
                    IsBodyHtml = true,
                    Body = $@"<div style='border:3px solid red; padding:20px; font-family:Arial;'>
                                <h1 style='color:red;'>CẢNH BÁO NGUY HIỂM!</h1>
                                <p>Hệ thống SafeGuard phát hiện nhiệt độ tại <b>Phòng {room}</b> vọt lên <b>{temp}°C</b>.</p>
                                <p>Vui lòng ngắt cầu dao điện và sơ tán ngay lập tức!</p>
                                <hr/>
                                <small>Đây là tin nhắn tự động từ hệ thống giám sát ký túc xá.</small>
                             </div>"
                })
                {
                    await smtp.SendMailAsync(message);
                    System.Diagnostics.Debug.WriteLine($"---> Đã bắn mail báo cháy cho {toEmail} lúc {DateTime.Now}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi SMTP: " + ex.Message);
            }
        }
    }
}