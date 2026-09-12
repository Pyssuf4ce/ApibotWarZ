using System;

namespace ApibotWarZ.UI.Models
{
    public class AppConfig
    {
        public int BotThreads { get; set; } = 5;
        public bool AutoStartCaptcha { get; set; } = true;
        public string DiscordWebhook { get; set; } = string.Empty;
        public string LicenseKey { get; set; } = string.Empty;
        public string FirebaseDatabaseUrl { get; set; } = string.Empty;
        public bool UseBullVpn { get; set; } = true;
        public bool UseVpnGate { get; set; } = true;
        public string LastGamePath { get; set; } = string.Empty;
        public bool UseRealChromeProfiles { get; set; } = true;
        public int ChromeBotProfileCount { get; set; } = 5;
    }
}
