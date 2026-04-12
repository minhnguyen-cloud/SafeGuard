using System;

namespace SafeGuard.Models
{
    public class AlertHistoryViewModel
    {
        public DateTime TimeStamp { get; set; }
        public string RoomId { get; set; }
        public double Temperature { get; set; }
        public string Description { get; set; }
    }
}