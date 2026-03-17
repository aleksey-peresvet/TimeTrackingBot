namespace TimeTrackerBot;

public record BotConfig
{
    public int PollIntervalMinutes { get; set; } = 15;
    public TimeSpan WorkStart { get; set; } = TimeSpan.FromHours(9);
    public TimeSpan WorkEnd { get; set; } = TimeSpan.FromHours(18);
    public TimeSpan LunchStart { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan LunchEnd { get; set; } = TimeSpan.FromHours(13);
    public string TargetEmail { get; set; } = "";
}

public record EmailConfig
{
    public string SmtpServer { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public bool SmtpUseSsl { get; set; } = true;
    public string ImapServer { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string Folder { get; set; } = "INBOX";
}