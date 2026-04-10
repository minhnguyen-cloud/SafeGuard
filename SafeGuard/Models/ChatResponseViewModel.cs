using Newtonsoft.Json;

namespace SafeGuard.Models
{
    public class ChatResponseViewModel
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("reply")]
        public string Reply { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }
    }
}