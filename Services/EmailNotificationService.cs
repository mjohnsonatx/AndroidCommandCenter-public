using AndroidCommandCenter.Models;
using System.IO;
using System.Net;
using System.Net.Mail;

namespace AndroidCommandCenter.Services;

public class EmailNotificationService
{
    private const int MaxInlineLogChars = 12000;

    public event Action<string>? LogMessage;

    public async Task SendPipelineLog(
        AutomationConfig automation,
        string projectName,
        bool success,
        string status,
        string logPath,
        CancellationToken ct)
    {
        if (!automation.Email.SendEmail)
        {
            LogMessage?.Invoke("Email notification disabled.");
            return;
        }

        if (!automation.CanSendEmail)
        {
            LogMessage?.Invoke("[WARN] Email notification is enabled but SMTP settings are incomplete.");
            return;
        }

        string subject = $"Android Command Center: {projectName} automated pipeline {(success ? "passed" : "failed")}";
        string logText = File.Exists(logPath)
            ? await File.ReadAllTextAsync(logPath, ct)
            : "Pipeline log file was not found.";

        string inlineLog = logText.Length > MaxInlineLogChars
            ? logText[^MaxInlineLogChars..]
            : logText;

        using var message = new MailMessage
        {
            From = new MailAddress(automation.Email.FromAddress),
            Subject = subject,
            Body =
                $"Project: {projectName}{Environment.NewLine}" +
                $"Status: {status}{Environment.NewLine}" +
                $"Log: {logPath}{Environment.NewLine}{Environment.NewLine}" +
                $"Recent log output:{Environment.NewLine}{Environment.NewLine}{inlineLog}"
        };

        foreach (string recipient in SplitAddresses(automation.Email.ToAddress))
            message.To.Add(recipient);

        if (File.Exists(logPath))
            message.Attachments.Add(new Attachment(logPath));

        using var client = new SmtpClient(automation.Email.SmtpHost, automation.Email.SmtpPort)
        {
            EnableSsl = automation.Email.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(automation.Email.UserName))
            client.Credentials = new NetworkCredential(automation.Email.UserName, automation.Email.Password);

        LogMessage?.Invoke($"Sending pipeline log email to {automation.Email.ToAddress}...");
        await client.SendMailAsync(message, ct);
        LogMessage?.Invoke("Pipeline log email sent.");
    }

    private static IEnumerable<string> SplitAddresses(string addresses) =>
        addresses
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => !string.IsNullOrWhiteSpace(a));
}
