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
        private static readonly Dictionary<string, DateTime> _sentAlerts = new Dictionary<string, DateTime>();

        // Dùng 1 tài khoản Gmail bot duy nhất cho cả cảnh báo và khẩn cấp
        private static readonly string BOT_EMAIL = "alertsafeguard@gmail.com";
        private static readonly string BOT_PASSWORD = "lebykbyizvfkayqd";

        public static void Start()
        {
            System.Diagnostics.Debug.WriteLine("=== EmailWorker.Start() ĐÃ ĐƯỢC GỌI ===");
            _timer = new Timer(async (e) => await CheckAndSendAlerts(), null, 0, 5000);
        }

        private static async Task CheckAndSendAlerts()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                System.Diagnostics.Debug.WriteLine("=== Worker đang quét DynamoDB lúc: " + DateTime.Now + " ===");

                var historyTable = Table.LoadTable(_client, "SafeDorm_History");
                var usersTable = Table.LoadTable(_client, "Users");

                long tenMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
                var filter = new ScanFilter();
                filter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, tenMinsAgo);

                var recentLogs = await historyTable.Scan(filter).GetRemainingAsync();
                System.Diagnostics.Debug.WriteLine("Số log 10 phút gần đây: " + recentLogs.Count);

                var latestLogsByRoom = recentLogs
                    .Where(d => d.ContainsKey("room_id") && d.ContainsKey("temperature") && d.ContainsKey("timestamp"))
                    .GroupBy(d => d["room_id"].AsString())
                    .Select(g => g.OrderByDescending(x => x["timestamp"].AsLong()).First())
                    .ToList();

                System.Diagnostics.Debug.WriteLine("Số phòng có log mới nhất: " + latestLogsByRoom.Count);

                foreach (var log in latestLogsByRoom)
                {
                    string roomId = log["room_id"].AsString();
                    double currentTemp = log["temperature"].AsDouble();

                    System.Diagnostics.Debug.WriteLine($"Worker đọc phòng {roomId} | temp = {currentTemp}");

                    if (currentTemp < 38)
                    {
                        System.Diagnostics.Debug.WriteLine($"Bỏ qua {roomId} vì temp < 38");
                        continue;
                    }

                    // Chống spam mail trong 10 phút
                    if (_sentAlerts.ContainsKey(roomId) && (DateTime.Now - _sentAlerts[roomId]).TotalMinutes <= 10)
                    {
                        System.Diagnostics.Debug.WriteLine($"Bỏ qua {roomId} vì đã gửi mail trong 10 phút gần đây");
                        continue;
                    }

                    var userFilter = new ScanFilter();
                    userFilter.AddCondition("AssignedRoom", ScanOperator.Equal, roomId);
                    var usersInRoom = await usersTable.Scan(userFilter).GetRemainingAsync();

                    System.Diagnostics.Debug.WriteLine($"Tìm thấy {usersInRoom.Count} user trong phòng {roomId}");

                    if (usersInRoom.Count == 0) continue;

                    string targetEmail = usersInRoom[0].ContainsKey("email")
                        ? usersInRoom[0]["email"].AsString()
                        : (usersInRoom[0].ContainsKey("mail") ? usersInRoom[0]["mail"].AsString() : "");

                    System.Diagnostics.Debug.WriteLine($"Email nhận: {targetEmail}");

                    if (string.IsNullOrWhiteSpace(targetEmail) || !targetEmail.Contains("@"))
                    {
                        System.Diagnostics.Debug.WriteLine("Email không hợp lệ, bỏ qua.");
                        continue;
                    }

                    bool sentOk;

                    if (currentTemp >= 50)
                    {
                        System.Diagnostics.Debug.WriteLine("Gửi mail KHẨN CẤP");
                        sentOk = await SendEmergencyGmail(targetEmail, roomId, currentTemp);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Gửi mail CẢNH BÁO");
                        sentOk = await SendWarningGmail(targetEmail, roomId, currentTemp);
                    }

                    if (sentOk)
                    {
                        _sentAlerts[roomId] = DateTime.Now;
                        System.Diagnostics.Debug.WriteLine($"Đánh dấu đã gửi mail thành công cho {roomId}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Không đánh dấu gửi cho {roomId} vì SMTP thất bại");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi Worker: " + ex.ToString());
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private static SmtpClient BuildSmtpClient()
        {
            return new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(BOT_EMAIL, BOT_PASSWORD)
            };
        }

        private static async Task<bool> SendWarningGmail(string toEmail, string room, double temp)
        {
            try
            {
                var fromAddress = new MailAddress(BOT_EMAIL, "SafeGuard Warning");
                var toAddress = new MailAddress(toEmail);

                using (var smtp = BuildSmtpClient())
                using (var message = new MailMessage(fromAddress, toAddress)
                {
                    Subject = $"⚠️ [CẢNH BÁO] NHIỆT ĐỘ CAO TẠI PHÒNG {room}",
                    IsBodyHtml = true,
                    Body = $@"<div style='border:2px solid orange; padding:20px; font-family:Arial;'>
                                <h2 style='color:#fd7e14;'>CẢNH BÁO NHIỆT ĐỘ CAO</h2>
                                <p>Hệ thống SafeGuard ghi nhận nhiệt độ tại <b>Phòng {room}</b> là <b>{temp}°C</b>.</p>
                                <p>Vui lòng kiểm tra các thiết bị điện, ổ cắm, bếp hoặc các nguồn sinh nhiệt trong phòng.</p>
                                <hr/>
                                <small>Đây là email cảnh báo tự động từ hệ thống SafeGuard.</small>
                             </div>"
                })
                {
                    await smtp.SendMailAsync(message);
                    System.Diagnostics.Debug.WriteLine($"---> Đã gửi mail cảnh báo cho {toEmail} lúc {DateTime.Now}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi SMTP Warning: " + ex.ToString());
                return false;
            }
        }

        private static async Task<bool> SendEmergencyGmail(string toEmail, string room, double temp)
        {
            try
            {
                var fromAddress = new MailAddress(BOT_EMAIL, "SafeGuard Fire Alert");
                var toAddress = new MailAddress(toEmail);

                using (var smtp = BuildSmtpClient())
                using (var message = new MailMessage(fromAddress, toAddress)
                {
                    Subject = $"🔥 [KHẨN CẤP] CẢNH BÁO CHÁY PHÒNG {room}",
                    IsBodyHtml = true,
                    Body = $@"<div style='border:3px solid red; padding:20px; font-family:Arial;'>
                                <h1 style='color:red;'>CẢNH BÁO NGUY HIỂM!</h1>
                                <p>Hệ thống SafeGuard phát hiện nhiệt độ tại <b>Phòng {room}</b> tăng lên <b>{temp}°C</b>.</p>
                                <p>Vui lòng kiểm tra ngay, ngắt cầu dao điện và sơ tán nếu cần thiết!</p>
                                <hr/>
                                <small>Đây là tin nhắn tự động từ hệ thống giám sát SafeGuard.</small>
                             </div>"
                })
                {
                    await smtp.SendMailAsync(message);
                    System.Diagnostics.Debug.WriteLine($"---> Đã gửi mail khẩn cấp cho {toEmail} lúc {DateTime.Now}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi SMTP Emergency: " + ex.ToString());
                return false;
            }
        }
    }
}