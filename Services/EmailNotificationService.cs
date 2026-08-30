using System.IO;
using AndroidCommandCenter.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AndroidCommandCenter.Services;

public sealed class EmailNotificationService
{
    private const int MaxInlineLogChars = 12000;
    private readonly SecretStore _secrets;
    public event Action<string>? LogMessage;

    public EmailNotificationService(SecretStore secrets) => _secrets = secrets;

    public const string PasswordKey = "smtp/password";

    public async Task SendPipelineLog(
        AutomationConfig automation, string projectName, bool success,
        string status, string logPath, CancellationToken ct)
    {
        if (!automation.Email.SendEmail) { LogMessage?.Invoke("Email notification disabled."); return; }
        if (!automation.SmtpComplete)
            throw new InvalidOperationException(
                "Email is enabled but SMTP settings are incomplete (host, port, from, to).");

        var e = automation.Email;
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(e.FromAddress));
        foreach (var to in Split(e.ToAddress)) msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = $"[{(success ? "PASS" : "FAIL")}] {projectName} pipeline — {status}";

        string logText = File.Exists(logPath)
            ? await File.ReadAllTextAsync(logPath, ct)
            : "Pipeline log file was not found.";
        string inline = logText.Length > MaxInlineLogChars ? logText[^MaxInlineLogChars..] : logText;

        var body = new BodyBuilder
        {
            TextBody =
                $"Project: {projectName}\nResult: {(success ? "PASSED" : "FAILED")}\n" +
                $"Status: {status}\nLog: {logPath}\n\n" +
                $"--- last {inline.Length} chars of log ---\n\n{inline}"
        };
        if (File.Exists(logPath))
        {
            try { await body.Attachments.AddAsync(logPath, ct); }
            catch (Exception ex) { LogMessage?.Invoke($"[WARN] could not attach log: {ex.Message}"); }
        }
        msg.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        // StartTls on 587, SslOnConnect on 465 — the old EnableSsl bool couldn't
        // express the difference and silently failed on one of them.
        var secure = e.SmtpPort switch
        {
            465 => SecureSocketOptions.SslOnConnect,
            _ when e.EnableSsl => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None
        };

        LogMessage?.Invoke($"Connecting to {e.SmtpHost}:{e.SmtpPort} ({secure})…");
        await client.ConnectAsync(e.SmtpHost, e.SmtpPort, secure, ct);

        if (!string.IsNullOrWhiteSpace(e.UserName))
        {
            string pw = _secrets.Get(PasswordKey);
            if (pw.Length == 0)
                throw new InvalidOperationException(
                    "SMTP username is set but no password is stored. Re-enter it in Automation settings.");
            await client.AuthenticateAsync(e.UserName, pw, ct);
        }

        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
        LogMessage?.Invoke($"Pipeline log emailed to {e.ToAddress}.");
    }

    private static IEnumerable<string> Split(string s) =>
        s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}