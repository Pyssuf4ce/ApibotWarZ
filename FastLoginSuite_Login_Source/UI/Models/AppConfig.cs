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
        public string EventId { get; set; } = "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3";
        public int BatchCooldownSeconds { get; set; } = 5;
        public int DeepCooldownEvery { get; set; } = 10;
        public int DeepCooldownSeconds { get; set; } = 15;
        public int LimitCooldownSeconds { get; set; } = 20;
        public string OperationMode { get; set; } = "all"; // "all", "harvest", "redeem_only"
    }
}
