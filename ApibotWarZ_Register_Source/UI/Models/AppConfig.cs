using System;

namespace ApibotWarZ.UI.Models
{
    public class AppConfig
    {
        public int BotThreads { get; set; } = 5;
        public bool AutoStartCaptcha { get; set; } = true;
        public string DiscordWebhook { get; set; } = string.Empty;
    }
}
