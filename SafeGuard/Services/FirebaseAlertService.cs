using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SafeGuard.Services
{
    public class FirebaseAlertService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly string _databaseUrl;
        public FirebaseAlertService()
        {
            _databaseUrl = (ConfigurationManager.AppSettings["FirebaseRealtimeDatabaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_databaseUrl);

        public async Task PublishAlertsAsync(IEnumerable<FirebaseAlertPayload> payloads, string source, string actorRole)
        {
            if (!IsConfigured)
            {
                return;
            }

            var state = await GetCurrentStateAsync() ?? new FirebaseSystemState();
            state.ActiveAlerts = state.ActiveAlerts ?? new Dictionary<string, FirebaseAlertPayload>();

            var normalizedPayloads = (payloads ?? Enumerable.Empty<FirebaseAlertPayload>())
                .Where(x => x != null)
                .Select(x =>
                {
                    x.Source = string.IsNullOrWhiteSpace(x.Source) ? source : x.Source;
                    x.ActorRole = string.IsNullOrWhiteSpace(x.ActorRole) ? actorRole : x.ActorRole;
                    x.TriggeredAt = string.IsNullOrWhiteSpace(x.TriggeredAt) ? DateTime.UtcNow.ToString("O") : x.TriggeredAt;
                    x.Threshold = x.Threshold <= 0 ? 50 : x.Threshold;
                    x.AlarmStatus = 1;
                    return x;
                })
                .ToList();

            var keysToRemove = state.ActiveAlerts
                .Where(x =>
                    string.Equals(x.Value?.Source, source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Value?.ActorRole, actorRole, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                state.ActiveAlerts.Remove(key);
            }

            foreach (var payload in normalizedPayloads)
            {
                state.ActiveAlerts[BuildAlertKey(payload)] = payload;
            }

            NormalizeState(state);
            await SaveStateAsync(state);
        }

        public async Task ClearAlertsAsync(string source, string actorRole, string roomId = null, string note = null)
        {
            if (!IsConfigured)
            {
                return;
            }

            var state = await GetCurrentStateAsync() ?? new FirebaseSystemState();
            state.ActiveAlerts = state.ActiveAlerts ?? new Dictionary<string, FirebaseAlertPayload>();

            var keysToRemove = state.ActiveAlerts
                .Where(x =>
                    string.Equals(x.Value?.Source, source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Value?.ActorRole, actorRole, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(roomId) || string.Equals(x.Value?.RoomId, roomId, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                state.ActiveAlerts.Remove(key);
            }

            state.Note = string.IsNullOrWhiteSpace(note) ? "Không có cảnh báo đang hoạt động." : note;
            NormalizeState(state);
            await SaveStateAsync(state);
        }

        private async Task<FirebaseSystemState> GetCurrentStateAsync()
        {
            var response = await _httpClient.GetAsync($"{_databaseUrl}/System.json");
            if (!response.IsSuccessStatusCode)
            {
                return new FirebaseSystemState();
            }

            var json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            {
                return new FirebaseSystemState();
            }

            return JsonConvert.DeserializeObject<FirebaseSystemState>(json) ?? new FirebaseSystemState();
        }

        private async Task SaveStateAsync(FirebaseSystemState state)
        {
            var json = JsonConvert.SerializeObject(state);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                var response = await _httpClient.PutAsync($"{_databaseUrl}/System.json", content);
                response.EnsureSuccessStatusCode();
            }
        }

        private void NormalizeState(FirebaseSystemState state)
        {
            state.ActiveAlerts = state.ActiveAlerts ?? new Dictionary<string, FirebaseAlertPayload>();

            var alerts = state.ActiveAlerts.Values
                .Where(x => x != null)
                .OrderByDescending(x => x.Temperature)
                .ThenByDescending(x => x.TriggeredAt)
                .ToList();

            state.AlarmStatus = alerts.Count;
            state.AlertCount = alerts.Count;
            state.LatestAlert = alerts.FirstOrDefault();
            state.Note = alerts.Any()
                ? $"Đang có {alerts.Count} phòng vượt ngưỡng 50 độ C."
                : (string.IsNullOrWhiteSpace(state.Note) ? "Không có cảnh báo đang hoạt động." : state.Note);
            state.UpdatedAt = DateTime.UtcNow.ToString("O");
        }

        private string BuildAlertKey(FirebaseAlertPayload payload)
        {
            return $"{payload.Source ?? "SYSTEM"}::{payload.ActorRole ?? "USER"}::{payload.RoomId ?? "UNKNOWN"}".ToUpperInvariant();
        }
    }

    public class FirebaseSystemState
    {
        public int AlarmStatus { get; set; }
        public int AlertCount { get; set; }
        public string Note { get; set; }
        public string UpdatedAt { get; set; }
        public FirebaseAlertPayload LatestAlert { get; set; }
        public Dictionary<string, FirebaseAlertPayload> ActiveAlerts { get; set; } = new Dictionary<string, FirebaseAlertPayload>();
    }

    public class FirebaseAlertPayload
    {
        public int AlarmStatus { get; set; }
        public string Source { get; set; }
        public string RoomId { get; set; }
        public string ActorRole { get; set; }
        public string Scenario { get; set; }
        public double Temperature { get; set; }
        public double Threshold { get; set; }
        public string Note { get; set; }
        public string TriggeredAt { get; set; }
    }
}
