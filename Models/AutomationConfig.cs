using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public sealed class AutomationConfig
{
    public string RemoteName { get; set; } = "origin";
    public string SourceBranch { get; set; } = "staging";
    public bool RecurringRunsEnabled { get; set; }
    public string RecurringRunTimes { get; set; } = "02:00";
    public string LastScheduledRunKey { get; set; } = "";
    public TestFailureAction FailureAction { get; set; } = TestFailureAction.AutomaticBounceBack;
    public EmailNotificationConfig Email { get; set; } = new();

    [JsonIgnore]
    public bool SmtpComplete =>
        !string.IsNullOrWhiteSpace(Email.SmtpHost) &&
        Email.SmtpPort > 0 &&
        !string.IsNullOrWhiteSpace(Email.FromAddress) &&
        !string.IsNullOrWhiteSpace(Email.ToAddress);
}

public sealed class EmailNotificationConfig
{
    public bool SendEmail { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string ToAddress { get; set; } = "";

    [JsonPropertyName("EncryptedPassword")] public string? LegacyEncryptedPassword { get; set; }
}