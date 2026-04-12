using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using SafeGuard.Filters;
using SafeGuard.Models;
using SafeGuard.Controllers.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;

namespace SafeGuard.Controllers
{
    [RoleAuthorize(Role = "ADMIN")]
    public class AdminController : Controller
    {
        private AmazonDynamoDBClient GetClient() => new AmazonDynamoDBClient(Amazon.RegionEndpoint.APSoutheast1);
        private readonly Random _random = new Random();

        private async Task<List<string>> GetAvailableRoomIdsAsync(AmazonDynamoDBClient client)
        {
            var roomsTable = Table.LoadTable(client, "Rooms");
            var allRooms = await roomsTable.Scan(new ScanFilter()).GetRemainingAsync();

            return allRooms
                .Where(r => r.ContainsKey("BlockId") && r.ContainsKey("RoomId"))
                .Select(r => $"{r["BlockId"].AsString().Trim()}-{r["RoomId"].AsString().Trim()}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<double> GetScenarioTemperatures(string scenario)
        {
            switch ((scenario ?? "").Trim().ToLower())
            {
                case "safe":
                    return new List<double> { 28, 29, 30, 31, 32 };

                case "warning":
                    return new List<double> { 34, 36, 38, 40, 42 };

                case "danger":
                    return new List<double> { 40, 43, 46, 49, 52 };

                default:
                    return new List<double> { 30, 32, 35, 39, 44 };
            }
        }

        private string BuildAdminDemoDescription(double temp)
        {
            if (temp >= 50)
                return "Kịch bản nguy hiểm cao: nhiệt độ vượt ngưỡng khẩn cấp.";
            if (temp >= 45)
                return "Kịch bản cảnh báo mạnh: nhiệt độ tăng cao bất thường.";
            if (temp >= 38)
                return "Kịch bản cảnh báo: nhiệt độ đã vượt ngưỡng theo dõi.";
            return "Kịch bản an toàn: nhiệt độ phòng đang ổn định.";
        }

        private async Task<int> DeleteAdminDemoInternal(AmazonDynamoDBClient client)
        {
            var historyTable = Table.LoadTable(client, "SafeDorm_History");
            var allDocs = await historyTable.Scan(new ScanFilter()).GetRemainingAsync();

            var adminDemoDocs = allDocs
                .Where(d =>
                    d.ContainsKey("isDemo") &&
                    d["isDemo"].AsBoolean() &&
                    d.ContainsKey("source") &&
                    string.Equals(d["source"].AsString(), "SYSTEM_SIMULATOR", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var doc in adminDemoDocs)
            {
                if (doc.ContainsKey("room_id") && doc.ContainsKey("timestamp"))
                {
                    await historyTable.DeleteItemAsync(doc["room_id"].AsString(), doc["timestamp"].AsLong());
                }
            }

            return adminDemoDocs.Count;
        }

        private async Task<(bool success, string message, int roomCount)> CreateSystemDemoInternal(string scenario)
        {
            var client = GetClient();
            var historyTable = Table.LoadTable(client, "SafeDorm_History");

            var availableRooms = await GetAvailableRoomIdsAsync(client);
            if (!availableRooms.Any())
            {
                return (false, "Không có phòng nào trong bảng Rooms để tạo dữ liệu demo.", 0);
            }

            await DeleteAdminDemoInternal(client);

            int numberOfRooms = Math.Min(6, availableRooms.Count);
            var selectedRooms = availableRooms
                .OrderBy(x => _random.Next())
                .Take(numberOfRooms)
                .ToList();

            var baseTemps = GetScenarioTemperatures(scenario);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (int roomIndex = 0; roomIndex < selectedRooms.Count; roomIndex++)
            {
                string roomId = selectedRooms[roomIndex];
                double roomOffset = Math.Round((_random.NextDouble() * 2.0) - 1.0, 1);

                for (int i = 0; i < baseTemps.Count; i++)
                {
                    long ts = now - ((baseTemps.Count - 1 - i) * 60) - roomIndex;
                    double temp = Math.Round(baseTemps[i] + roomOffset + ((_random.NextDouble() * 0.6) - 0.3), 1);

                    var doc = new Document();
                    doc["room_id"] = roomId;
                    doc["timestamp"] = ts;
                    doc["temperature"] = temp;
                    doc["isDemo"] = true;
                    doc["source"] = "SYSTEM_SIMULATOR";
                    doc["demoScenario"] = scenario?.ToUpper() ?? "DEFAULT";
                    doc["description"] = BuildAdminDemoDescription(temp);
                    doc["createdAt"] = DateTime.UtcNow.ToString("O");

                    await historyTable.PutItemAsync(doc);
                }
            }

            string scenarioLabel =
                string.Equals(scenario, "safe", StringComparison.OrdinalIgnoreCase) ? "an toàn" :
                string.Equals(scenario, "warning", StringComparison.OrdinalIgnoreCase) ? "cảnh báo" :
                string.Equals(scenario, "danger", StringComparison.OrdinalIgnoreCase) ? "nguy hiểm" :
                "mặc định";

            return (true, $"Đã tạo dữ liệu demo hệ thống theo kịch bản {scenarioLabel} cho {selectedRooms.Count} phòng.", selectedRooms.Count);
        }

        [HttpGet]
        public async Task<ActionResult> Index()
        {
            var client = GetClient();

            try
            {
                var roomsTable = Table.LoadTable(client, "Rooms");
                var allRoomsDocs = await roomsTable.Scan(new ScanFilter()).GetRemainingAsync();
                ViewBag.TotalRooms = allRoomsDocs.Count;

                var usersTable = Table.LoadTable(client, "Users");
                var tenantFilter = new ScanFilter();
                tenantFilter.AddCondition("role", ScanOperator.Equal, "TENANT");
                var tenants = await usersTable.Scan(tenantFilter).GetRemainingAsync();

                var activeRoomIds = tenants
                    .Where(u => u.ContainsKey("AssignedRoom") && !string.IsNullOrWhiteSpace(u["AssignedRoom"].AsString()))
                    .Select(u => u["AssignedRoom"].AsString().Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ViewBag.ActiveRooms = activeRoomIds.Count;

                var historyTable = Table.LoadTable(client, "SafeDorm_History");
                long tenMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();

                var historyFilter = new ScanFilter();
                historyFilter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, tenMinsAgo);
                var recentLogs = await historyTable.Scan(historyFilter).GetRemainingAsync();

                var groupedLatestLogs = recentLogs
                    .Where(doc => doc.ContainsKey("room_id") && doc.ContainsKey("timestamp") && doc.ContainsKey("temperature"))
                    .GroupBy(doc => doc["room_id"].AsString())
                    .Select(g => g.OrderByDescending(doc => doc["timestamp"].AsLong()).First())
                    .ToList();

                ViewBag.OnlineDevices = groupedLatestLogs.Count;
                ViewBag.CurrentAlerts = groupedLatestLogs.Count(doc => doc["temperature"].AsDouble() >= 38);

                ViewBag.RecentActivities = tenants
                    .Where(u => u.ContainsKey("AssignedRoom") && !string.IsNullOrWhiteSpace(u["AssignedRoom"].AsString()))
                    .OrderByDescending(u => u.ContainsKey("createdAt") ? u["createdAt"].AsString() : "")
                    .Take(4)
                    .Select(u => new RecentActivityVM
                    {
                        Name = u.ContainsKey("fullName") ? u["fullName"].AsString() : "Sinh viên",
                        Room = u["AssignedRoom"].AsString(),
                        Time = u.ContainsKey("createdAt") && !string.IsNullOrWhiteSpace(u["createdAt"].AsString())
                            ? DateTime.Parse(u["createdAt"].AsString()).ToString("HH:mm - dd/MM")
                            : ""
                    })
                    .ToList();

                ViewBag.SensorList = groupedLatestLogs
                    .Select(latest => new SensorDataVM
                    {
                        RoomId = latest["room_id"].AsString(),
                        Temp = latest["temperature"].AsDouble(),
                        Time = DateTimeOffset.FromUnixTimeSeconds(latest["timestamp"].AsLong())
                            .ToLocalTime()
                            .ToString("hh:mm tt")
                    })
                    .OrderBy(x => x.RoomId)
                    .ToList();

                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();
                ViewBag.Blocks = blocks
                    .Where(b => b.ContainsKey("BlockId"))
                    .Select(b => b["BlockId"].AsString())
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                ViewBag.RealRoomsList = allRoomsDocs
                    .Select(r => new SafeGuard.Models.RoomDisplayViewModel
                    {
                        BlockName = r.ContainsKey("BlockId") ? r["BlockId"].AsString() : "",
                        RoomId = r.ContainsKey("RoomId") ? r["RoomId"].AsString() : ""
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.BlockName) && !string.IsNullOrWhiteSpace(x.RoomId))
                    .OrderBy(x => x.BlockName)
                    .ThenBy(x => x.RoomId)
                    .ToList();

                var allHistoryDocs = await historyTable.Scan(new ScanFilter()).GetRemainingAsync();

                var latestHistoryByRoom = allHistoryDocs
                    .Where(doc => doc.ContainsKey("room_id") && doc.ContainsKey("timestamp") && doc.ContainsKey("temperature"))
                    .GroupBy(doc => doc["room_id"].AsString())
                    .Select(g => g.OrderByDescending(doc => doc["timestamp"].AsLong()).First())
                    .OrderByDescending(doc => doc["temperature"].AsDouble())
                    .ToList();

                var hotRoomsForChart = latestHistoryByRoom
                    .Where(x => x["temperature"].AsDouble() >= 38)
                    .ToList();

                var finalChartRooms = hotRoomsForChart.Any()
                    ? hotRoomsForChart
                    : latestHistoryByRoom.Take(5).ToList();

                ViewBag.AIOverviewLabels = finalChartRooms
                    .Select(x => x["room_id"].AsString())
                    .ToList();

                ViewBag.AIOverviewTemps = finalChartRooms
                    .Select(x => x["temperature"].AsDouble())
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi AWS ở Index: " + ex.Message);
                ViewBag.TotalRooms = 0;
                ViewBag.ActiveRooms = 0;
                ViewBag.OnlineDevices = 0;
                ViewBag.CurrentAlerts = 0;
                ViewBag.RecentActivities = new List<RecentActivityVM>();
                ViewBag.SensorList = new List<SensorDataVM>();
                ViewBag.Blocks = new List<string>();
                ViewBag.RealRoomsList = new List<SafeGuard.Models.RoomDisplayViewModel>();
                ViewBag.AIOverviewLabels = new List<string>();
                ViewBag.AIOverviewTemps = new List<double>();
            }

            return View();
        }

        [HttpGet]
        public async Task<ActionResult> QuanLyPhong()
        {
            try
            {
                var client = GetClient();

                var tableFacilities = Table.LoadTable(client, "Facilities");
                ViewBag.Blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();

                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var tableUsers = Table.LoadTable(client, "Users");
                var allUsers = await tableUsers.Scan(new ScanFilter()).GetRemainingAsync();

                var tenantUsers = allUsers
                    .Where(u => u.ContainsKey("role") &&
                                string.Equals(u["role"].AsString(), "TENANT", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var roomList = new List<RoomDisplayViewModel>();

                foreach (var r in allRooms)
                {
                    string bId = r.ContainsKey("BlockId") ? r["BlockId"].AsString() : "";
                    string rId = r.ContainsKey("RoomId") ? r["RoomId"].AsString() : "";

                    if (string.IsNullOrWhiteSpace(bId) || string.IsNullOrWhiteSpace(rId))
                        continue;

                    string fullId = $"{bId}-{rId}";

                    var owner = tenantUsers.FirstOrDefault(u =>
                        u.ContainsKey("AssignedRoom") &&
                        string.Equals(u["AssignedRoom"].AsString(), fullId, StringComparison.OrdinalIgnoreCase));

                    roomList.Add(new RoomDisplayViewModel
                    {
                        RoomName = "Phòng " + fullId,
                        BlockName = bId,
                        RoomId = rId,
                        OwnerEmail = owner != null
                            ? (owner.ContainsKey("fullName") ? owner["fullName"].AsString() : owner["userID"].AsString())
                            : "Chưa có người thuê",
                        IsOnline = owner != null
                    });
                }

                ViewBag.Rooms = roomList
                    .OrderBy(r => r.BlockName)
                    .ThenBy(r => r.RoomId)
                    .ToList();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi lấy dữ liệu: " + ex.Message;
            }

            return View();
        }

        [HttpPost]
        public async Task<ActionResult> ThemPhongMoi(string blockId, string roomNumber)
        {
            try
            {
                blockId = blockId?.Trim();
                roomNumber = roomNumber?.Trim();

                if (string.IsNullOrEmpty(blockId) || string.IsNullOrEmpty(roomNumber))
                    return RedirectToAction("QuanLyPhong");

                var client = GetClient();
                var table = Table.LoadTable(client, "Rooms");

                var existingRoom = await table.GetItemAsync(blockId, roomNumber);
                if (existingRoom != null)
                {
                    TempData["ErrorMessage"] = $"Lỗi: Phòng {roomNumber} đã tồn tại trong Dãy {blockId}!";
                    return RedirectToAction("QuanLyPhong");
                }

                var item = new Document();
                item["BlockId"] = blockId;
                item["RoomId"] = roomNumber;
                item["CreatedAt"] = DateTime.Now.ToString("O");

                await table.PutItemAsync(item);

                TempData["SuccessMessage"] = $"Đã thêm phòng {roomNumber} vào dãy {blockId} thành công!";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi từ AWS: " + ex.Message;
            }

            return RedirectToAction("QuanLyPhong");
        }

        [HttpPost]
        public async Task<JsonResult> XoaPhong(string blockId, string roomId)
        {
            try
            {
                var client = GetClient();
                string fullRoomId = $"{blockId}-{roomId}";

                var tableRooms = Table.LoadTable(client, "Rooms");
                await tableRooms.DeleteItemAsync(blockId, roomId);

                var usersTable = Table.LoadTable(client, "Users");
                var scanFilter = new ScanFilter();
                scanFilter.AddCondition("AssignedRoom", ScanOperator.Equal, fullRoomId);
                var usersInRoom = await usersTable.Scan(scanFilter).GetRemainingAsync();

                foreach (var user in usersInRoom)
                {
                    user.Remove("AssignedRoom");
                    await usersTable.PutItemAsync(user);
                }

                var invitesTable = Table.LoadTable(client, "RoomInvites");
                var inviteFilter = new ScanFilter();
                inviteFilter.AddCondition("RoomId", ScanOperator.Equal, fullRoomId);
                var invitesInRoom = await invitesTable.Scan(inviteFilter).GetRemainingAsync();

                foreach (var inv in invitesInRoom)
                {
                    await invitesTable.DeleteItemAsync(inv["InviteCode"].AsString());
                }

                return Json(new
                {
                    success = true,
                    message = $"Đã xóa vĩnh viễn phòng {fullRoomId} và dọn dẹp dữ liệu!"
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi hệ thống: " + ex.Message
                });
            }
        }

        [HttpGet]
        public async Task<ActionResult> QuanLyDayTro()
        {
            try
            {
                var client = GetClient();

                var tableFacilities = Table.LoadTable(client, "Facilities");
                var blocks = await tableFacilities.Query(new QueryFilter("PK", QueryOperator.Equal, "BLOCK")).GetRemainingAsync();

                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var tableUsers = Table.LoadTable(client, "Users");
                var allUsers = await tableUsers.Scan(new ScanFilter()).GetRemainingAsync();

                var tenantUsers = allUsers
                    .Where(u => u.ContainsKey("role") &&
                                string.Equals(u["role"].AsString(), "TENANT", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var block in blocks)
                {
                    string bId = block.ContainsKey("BlockId") ? block["BlockId"].AsString() : "";
                    if (string.IsNullOrWhiteSpace(bId))
                        continue;

                    var roomsInBlock = allRooms
                        .Where(r => r.ContainsKey("BlockId") &&
                                    string.Equals(r["BlockId"].AsString(), bId, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    block["TotalRooms"] = roomsInBlock.Count;

                    int activeRooms = roomsInBlock.Count(room =>
                        tenantUsers.Any(u =>
                            u.ContainsKey("AssignedRoom") &&
                            string.Equals(
                                u["AssignedRoom"].AsString(),
                                $"{bId}-{room["RoomId"].AsString()}",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    );

                    block["ActiveRooms"] = activeRooms;
                }

                ViewBag.Blocks = blocks
                    .OrderBy(b => b.ContainsKey("BlockName") ? b["BlockName"].AsString() : "")
                    .ToList();

                ViewBag.AllRooms = allRooms;
                ViewBag.AllUsers = tenantUsers;
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi: " + ex.Message;
            }

            return View();
        }

        [HttpPost]
        public async Task<ActionResult> ThemDayMoi(string blockName, string address, int numberOfRooms)
        {
            try
            {
                var client = GetClient();

                string blockId = blockName.Trim().ToUpper();
                if (blockId.Contains(" "))
                    blockId = blockId.Split(' ').Last();

                var tableFacilities = Table.LoadTable(client, "Facilities");

                var blockItem = new Document();
                blockItem["PK"] = "BLOCK";
                blockItem["SK"] = $"BLOCK#{Guid.NewGuid().ToString().Substring(0, 5)}";
                blockItem["BlockId"] = blockId;
                blockItem["BlockName"] = blockName;
                blockItem["Address"] = address;
                blockItem["TotalRooms"] = numberOfRooms;
                blockItem["ActiveRooms"] = 0;

                await tableFacilities.PutItemAsync(blockItem);

                var tableRooms = Table.LoadTable(client, "Rooms");
                for (int i = 1; i <= numberOfRooms; i++)
                {
                    var roomItem = new Document();
                    roomItem["BlockId"] = blockId;
                    roomItem["RoomId"] = $"10{i}";
                    roomItem["CreatedAt"] = DateTime.Now.ToString("O");
                    await tableRooms.PutItemAsync(roomItem);
                }

                TempData["SuccessMessage"] = $"Đã thêm dãy trọ {blockName} thành công!";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi: " + ex.Message;
            }

            return RedirectToAction("QuanLyDayTro");
        }

        [HttpGet]
        public async Task<ActionResult> TaoMaKichHoat()
        {
            try
            {
                var client = GetClient();

                var tableInvites = Table.LoadTable(client, "RoomInvites");
                ViewBag.InviteList = await tableInvites.Scan(new ScanFilter()).GetRemainingAsync();

                var tableRooms = Table.LoadTable(client, "Rooms");
                var allRooms = await tableRooms.Scan(new ScanFilter()).GetRemainingAsync();

                var roomList = new List<RoomDisplayViewModel>();
                foreach (var r in allRooms)
                {
                    string bId = r.ContainsKey("BlockId") ? r["BlockId"].AsString() : "";
                    string rId = r.ContainsKey("RoomId") ? r["RoomId"].AsString() : "";

                    if (string.IsNullOrWhiteSpace(bId) || string.IsNullOrWhiteSpace(rId))
                        continue;

                    roomList.Add(new RoomDisplayViewModel
                    {
                        RoomName = "Phòng " + bId + "-" + rId,
                        BlockName = bId,
                        RoomId = rId
                    });
                }

                ViewBag.Rooms = roomList
                    .OrderBy(r => r.BlockName)
                    .ThenBy(r => r.RoomId)
                    .ToList();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi: " + ex.Message;
            }

            return View();
        }

        [HttpPost]
        public async Task<ActionResult> TaoMaMoi(string selectedRoom, int expireHours)
        {
            try
            {
                string newInviteCode = $"{selectedRoom}-{Guid.NewGuid().ToString().Substring(0, 6).ToUpper()}";

                var client = GetClient();
                var table = Table.LoadTable(client, "RoomInvites");

                var item = new Document();
                item["InviteCode"] = newInviteCode;
                item["RoomId"] = selectedRoom;
                item["ExpireHours"] = expireHours;
                item["IsUsed"] = false;
                item["CreatedAt"] = DateTime.UtcNow.ToString("O");

                await table.PutItemAsync(item);

                TempData["SuccessMessage"] = $"Tạo mã thành công: {newInviteCode}";

                return Request.UrlReferrer?.AbsolutePath.Contains("Admin/Index") == true
                    ? RedirectToAction("Index")
                    : RedirectToAction("TaoMaKichHoat");
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Lỗi: " + ex.Message;
                return RedirectToAction("TaoMaKichHoat");
            }
        }

        public async Task<ActionResult> LichSuCanhBao()
        {
            var alertList = new List<AlertHistoryViewModel>();

            try
            {
                var client = GetClient();
                var table = Table.LoadTable(client, "SafeDorm_History");
                var documentList = await table.Scan(new ScanFilter()).GetRemainingAsync();

                foreach (var doc in documentList)
                {
                    if (!doc.ContainsKey("temperature") || !doc.ContainsKey("timestamp") || !doc.ContainsKey("room_id"))
                        continue;

                    double temp = doc["temperature"].AsDouble();
                    if (temp >= 38)
                    {
                        DateTime timeStamp = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(doc["timestamp"].AsLong())
                            .ToLocalTime();

                        string description = temp >= 50
                            ? "[KHẨN CẤP] Nguy cơ cháy nổ cao!"
                            : temp >= 45
                                ? "Nhiệt độ tăng cao bất thường."
                                : "Cảnh báo ngưỡng 1.";

                        alertList.Add(new AlertHistoryViewModel
                        {
                            TimeStamp = timeStamp,
                            RoomId = "Phòng " + doc["room_id"].AsString(),
                            Temperature = temp,
                            Description = description
                        });
                    }
                }
            }
            catch (Exception)
            {
            }

            return View(alertList.OrderByDescending(x => x.TimeStamp).ToList());
        }

        public async Task<ActionResult> XuatExcelLichSu()
        {
            var alertList = new List<AlertHistoryViewModel>();

            try
            {
                var client = GetClient();
                var table = Table.LoadTable(client, "SafeDorm_History");
                var documentList = await table.Scan(new ScanFilter()).GetRemainingAsync();

                foreach (var doc in documentList)
                {
                    if (!doc.ContainsKey("temperature") || !doc.ContainsKey("timestamp") || !doc.ContainsKey("room_id"))
                        continue;

                    double temp = doc["temperature"].AsDouble();
                    if (temp >= 38)
                    {
                        DateTime timeStamp = DateTimeOffset
                            .FromUnixTimeSeconds(doc["timestamp"].AsLong())
                            .ToLocalTime()
                            .DateTime;

                        string desc = temp >= 50
                            ? "[KHẨN CẤP] Nguy cơ cháy nổ"
                            : temp >= 45
                                ? "Nhiệt độ tăng cao bất thường"
                                : "Cảnh báo ngưỡng 1";

                        alertList.Add(new AlertHistoryViewModel
                        {
                            TimeStamp = timeStamp,
                            RoomId = "Phòng " + doc["room_id"].AsString(),
                            Temperature = temp,
                            Description = desc
                        });
                    }
                }
            }
            catch (Exception)
            {
            }

            var builder = new StringBuilder();
            builder.AppendLine("Thời Gian,Phòng,Mức Nhiệt Độ,Mô Tả Cảnh Báo");

            foreach (var item in alertList.OrderByDescending(x => x.TimeStamp))
            {
                builder.AppendLine($"{item.TimeStamp:dd/MM/yyyy HH:mm:ss},{item.RoomId},{item.Temperature},\"{item.Description}\"");
            }

            return File(
                Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray(),
                "text/csv",
                $"LichSuCanhBao_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            );
        }

        [HttpGet]
        public async Task<JsonResult> GetRoomMembers(string roomId)
        {
            try
            {
                var client = GetClient();
                var usersTable = Table.LoadTable(client, "Users");

                var scanFilter = new ScanFilter();
                scanFilter.AddCondition("AssignedRoom", ScanOperator.Equal, roomId);

                var search = usersTable.Scan(scanFilter);
                var documentList = await search.GetRemainingAsync();

                var members = documentList.Select(doc => new
                {
                    FullName = doc.ContainsKey("fullName") ? doc["fullName"].AsString() : "Chưa cập nhật",
                    Email = doc.ContainsKey("email") ? doc["email"].AsString() : (doc.ContainsKey("userID") ? doc["userID"].AsString() : "N/A")
                }).ToList();

                return Json(new { success = true, data = members }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        [HttpGet]
        public async Task<JsonResult> GetRealTimeSensorData()
        {
            try
            {
                var client = GetClient();
                var historyTable = Table.LoadTable(client, "SafeDorm_History");

                long tenMinsAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
                var historyFilter = new ScanFilter();
                historyFilter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual, tenMinsAgo);

                var recentLogs = await historyTable.Scan(historyFilter).GetRemainingAsync();

                var sensorList = new List<object>();

                var groupedLogs = recentLogs
                    .Where(doc => doc.ContainsKey("room_id") && doc.ContainsKey("timestamp") && doc.ContainsKey("temperature"))
                    .GroupBy(doc => doc["room_id"].AsString());

                foreach (var g in groupedLogs)
                {
                    var latest = g.OrderByDescending(doc => doc["timestamp"].AsLong()).First();

                    string rId = latest["room_id"].AsString();
                    double temp = latest["temperature"].AsDouble();
                    bool isAlert = temp >= 38;

                    sensorList.Add(new
                    {
                        RoomId = rId,
                        Temp = temp,
                        Time = DateTimeOffset.FromUnixTimeSeconds(latest["timestamp"].AsLong())
                            .ToLocalTime()
                            .ToString("hh:mm tt"),
                        IsAlert = isAlert
                    });
                }

                return Json(sensorList, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi RealTime: " + ex.Message);
                return Json(null, JsonRequestBehavior.AllowGet);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> CreateSystemDemoData()
        {
            try
            {
                var result = await CreateSystemDemoInternal("default");
                return Json(new
                {
                    success = result.success,
                    message = result.message,
                    roomCount = result.roomCount
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể tạo dữ liệu demo hệ thống: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> CreateSafeScenario()
        {
            try
            {
                var result = await CreateSystemDemoInternal("safe");
                return Json(new
                {
                    success = result.success,
                    message = result.message,
                    roomCount = result.roomCount
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể tạo kịch bản an toàn: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> CreateWarningScenario()
        {
            try
            {
                var result = await CreateSystemDemoInternal("warning");
                return Json(new
                {
                    success = result.success,
                    message = result.message,
                    roomCount = result.roomCount
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể tạo kịch bản cảnh báo: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> CreateDangerScenario()
        {
            try
            {
                var result = await CreateSystemDemoInternal("danger");
                return Json(new
                {
                    success = result.success,
                    message = result.message,
                    roomCount = result.roomCount
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể tạo kịch bản nguy hiểm: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<JsonResult> DeleteSystemDemoData()
        {
            try
            {
                var client = GetClient();
                int deletedCount = await DeleteAdminDemoInternal(client);

                return Json(new
                {
                    success = true,
                    deletedCount = deletedCount,
                    message = deletedCount > 0
                        ? $"Đã xóa {deletedCount} bản ghi dữ liệu demo của admin."
                        : "Không có dữ liệu demo admin nào để xóa."
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Không thể xóa dữ liệu demo hệ thống: " + ex.Message
                });
            }
        }

        [HttpGet]
        public ActionResult BaoCaoAI()
        {
            return View();
        }

        [HttpGet]
        public async Task<JsonResult> GetBaoCaoAIData(string roomId = null)
        {
            try
            {
                var client = GetClient();
                var historyTable = Table.LoadTable(client, "SafeDorm_History");
                var docs = await historyTable.Scan(new ScanFilter()).GetRemainingAsync();

                var records = docs
                    .Where(d => d.ContainsKey("room_id") && d.ContainsKey("timestamp") && d.ContainsKey("temperature"))
                    .Select(d => new BaoCaoNhietDoRecord
                    {
                        RoomId = d["room_id"].AsString(),
                        Timestamp = d["timestamp"].AsLong(),
                        Temperature = d["temperature"].AsDouble()
                    })
                    .OrderBy(r => r.Timestamp)
                    .ToList();

                if (!records.Any())
                {
                    return Json(new
                    {
                        success = true,
                        totalRooms = 0,
                        alertRoomsCount = 0,
                        hottestRoom = "--",
                        hottestTemp = 0,
                        selectedRoom = "",
                        selectedRoomLabels = new List<string>(),
                        selectedRoomTemps = new List<double>(),
                        hotRooms = new List<object>(),
                        blockStats = new List<object>(),
                        aiSummary = "Chưa có dữ liệu cảm biến trong hệ thống."
                    }, JsonRequestBehavior.AllowGet);
                }

                var latestByRoom = records
                    .GroupBy(r => r.RoomId)
                    .Select(g => g.OrderByDescending(x => x.Timestamp).First())
                    .OrderByDescending(x => x.Temperature)
                    .ToList();

                var hotRooms = latestByRoom
                    .Where(x => x.Temperature >= 38)
                    .OrderByDescending(x => x.Temperature)
                    .ToList();

                if (string.IsNullOrWhiteSpace(roomId))
                {
                    roomId = hotRooms.FirstOrDefault()?.RoomId ?? latestByRoom.First().RoomId;
                }

                var selectedRoomHistory = records
                    .Where(x => string.Equals(x.RoomId, roomId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Timestamp)
                    .Take(10)
                    .OrderBy(x => x.Timestamp)
                    .ToList();

                var blockStats = hotRooms
                    .GroupBy(x => GetBlockName(x.RoomId))
                    .Select(g => new
                    {
                        BlockName = g.Key,
                        AlertCount = g.Count(),
                        MaxTemp = g.Max(x => x.Temperature)
                    })
                    .OrderByDescending(x => x.AlertCount)
                    .ThenByDescending(x => x.MaxTemp)
                    .ToList();

                var hottest = latestByRoom.First();

                string aiSummary;
                if (hotRooms.Any())
                {
                    aiSummary =
                        $"Hệ thống phát hiện <b>{hotRooms.Count}</b> phòng đang có nhiệt độ cao bất thường. " +
                        $"Phòng nóng nhất hiện tại là <b>{hottest.RoomId}</b> với mức <b>{hottest.Temperature}°C</b>. " +
                        $"Dãy cần chú ý nhất là <b>{blockStats.FirstOrDefault()?.BlockName ?? "Không rõ"}</b>. " +
                        $"Ưu tiên kiểm tra các phòng vượt ngưỡng 38°C trước.";
                }
                else
                {
                    aiSummary =
                        $"Hiện tại chưa có phòng nào vượt ngưỡng cảnh báo 38°C. " +
                        $"Phòng có nhiệt độ cao nhất là <b>{hottest.RoomId}</b> với mức <b>{hottest.Temperature}°C</b>. " +
                        $"Hệ thống đang ở trạng thái tương đối ổn định.";
                }

                return Json(new
                {
                    success = true,
                    totalRooms = latestByRoom.Count,
                    alertRoomsCount = hotRooms.Count,
                    hottestRoom = hottest.RoomId,
                    hottestTemp = hottest.Temperature,
                    selectedRoom = roomId,
                    selectedRoomLabels = selectedRoomHistory
                        .Select(x => DateTimeOffset.FromUnixTimeSeconds(x.Timestamp).ToLocalTime().ToString("HH:mm"))
                        .ToList(),
                    selectedRoomTemps = selectedRoomHistory
                        .Select(x => x.Temperature)
                        .ToList(),
                    hotRooms = hotRooms.Select(x => new
                    {
                        roomId = x.RoomId,
                        temperature = x.Temperature,
                        blockName = GetBlockName(x.RoomId),
                        time = DateTimeOffset.FromUnixTimeSeconds(x.Timestamp).ToLocalTime().ToString("HH:mm dd/MM")
                    }).ToList(),
                    blockStats = blockStats,
                    aiSummary = aiSummary
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = ex.Message
                }, JsonRequestBehavior.AllowGet);
            }
        }

        private string GetBlockName(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                return "Không rõ";

            if (roomId.Contains("-"))
                return roomId.Split('-')[0].Trim().ToUpper();

            return "Chưa gán dãy";
        }

        private class BaoCaoNhietDoRecord
        {
            public string RoomId { get; set; }
            public long Timestamp { get; set; }
            public double Temperature { get; set; }
        }

        private async Task SendEmergencyEmail(string toEmail, string roomName, double temperature)
        {
            try
            {
                string botEmail = "adminsafeguard@gmail.com";
                string botPassword = "sofq imon enbq uexp";

                var message = new System.Net.Mail.MailMessage(botEmail, toEmail)
                {
                    Subject = $"[KHẨN CẤP] CẢNH BÁO NHIỆT ĐỘ CAO - PHÒNG {roomName}",
                    IsBodyHtml = true,
                    Body = $@"
                        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: auto; border: 2px solid #dc3545; border-radius: 10px; overflow: hidden;'>
                            <div style='background-color: #dc3545; color: white; padding: 20px; text-align: center;'>
                                <h2 style='margin: 0;'>🔥 BÁO ĐỘNG AN TOÀN 🔥</h2>
                            </div>
                            <div style='padding: 20px; background-color: #f8d7da; color: #842029;'>
                                <p style='font-size: 16px;'>Hệ thống SafeGuard phát hiện nhiệt độ <strong>NÓNG BẤT THƯỜNG</strong> tại phòng của bạn.</p>
                                <h1 style='text-align: center; color: #dc3545; font-size: 48px; margin: 10px 0;'>{temperature}°C</h1>
                                <p style='font-size: 16px;'><strong>Phòng giám sát:</strong> {roomName}</p>
                                <p style='font-size: 16px;'><strong>Lời khuyên:</strong> Hãy kiểm tra ngay các thiết bị điện có khả năng sinh nhiệt hoặc ngắt cầu dao tổng nếu cần thiết.</p>
                            </div>
                            <div style='background-color: #f1f1f1; padding: 10px; text-align: center; color: #6c757d; font-size: 12px;'>
                                Hệ thống cảnh báo tự động từ SafeGuard. Vui lòng không trả lời email này.
                            </div>
                        </div>"
                };

                using (var smtp = new System.Net.Mail.SmtpClient("smtp.gmail.com", 587))
                {
                    smtp.Credentials = new System.Net.NetworkCredential(botEmail, botPassword);
                    smtp.EnableSsl = true;
                    await smtp.SendMailAsync(message);
                }

                System.Diagnostics.Debug.WriteLine("Đã gửi email khẩn cấp thành công tới: " + toEmail);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi gửi email: " + ex.Message);
            }
        }
    }
}

namespace SafeGuard.Controllers.ViewModels
{
    public class RecentActivityVM
    {
        public string Name { get; set; }
        public string Room { get; set; }
        public string Time { get; set; }
    }

    public class SensorDataVM
    {
        public string RoomId { get; set; }
        public double Temp { get; set; }
        public string Time { get; set; }
    }
}