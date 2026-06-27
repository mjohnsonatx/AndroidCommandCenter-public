using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public class AutomationConfig
{
    public string RemoteName { get; set; } = "origin";
    public string SourceBranch { get; set; } = "master";
    public TestFailureAction FailureAction { get; set; } = TestFailureAction.AutomaticBounceBack;
    public EmailNotificationConfig Email { get; set; } = new();

    [JsonIgnore]
    public bool CanSendEmail =>
        Email.SendEmail &&
        !string.IsNullOrWhiteSpace(Email.SmtpHost) &&
        Email.SmtpPort > 0 &&
        !string.IsNullOrWhiteSpace(Email.FromAddress) &&
        !string.IsNullOrWhiteSpace(Email.ToAddress);
}

public class EmailNotificationConfig
{
    public bool SendEmail { get; set; } = true;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string? EncryptedPassword { get; set; }
    public string FromAddress { get; set; } = "";
    public string ToAddress { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => Dec(EncryptedPassword);
        set => EncryptedPassword = Enc(value);
    }

    private static string Enc(string? p) => string.IsNullOrEmpty(p) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(p), null, DataProtectionScope.CurrentUser));

    private static string Dec(string? c)
    {
        if (string.IsNullOrEmpty(c)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(c), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
