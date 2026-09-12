using System;

namespace ApibotWarZ.UI.Models
{
    public class RegisteredAccount
    {
        public string Username { get; set; } = string.Empty;
        public string RegisteredAt { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Status { get; set; } = "✅ สำเร็จ (Registered)";
    }
}
