using Newtonsoft.Json;

namespace SafeGuard.Models
{
    public class ChatRequestViewModel
    {
        [JsonProperty("question")]
        public string Question { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }
    }
}