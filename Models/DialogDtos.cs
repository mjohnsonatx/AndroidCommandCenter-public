using System.Collections.Immutable;

namespace AndroidCommandCenter.Models;

/// <summary>Firebase Config dialog. Immutable in, nullable-out (null = cancelled).</summary>
public sealed record FirebaseConfigDraft(
    string AppId,
    string CredentialsJsonPath,
    string CliPath)
{
    public static FirebaseConfigDraft From(ProjectProfile p) =>
        new(p.FirebaseAppId, p.FirebaseCredentialsJson,
            string.IsNullOrWhiteSpace(p.FirebaseCliPath) ? "firebase" : p.FirebaseCliPath);

    public void ApplyTo(ProjectProfile p)
    {
        p.FirebaseAppId = AppId.Trim();
        p.FirebaseCredentialsJson = CredentialsJsonPath.Trim();
        p.FirebaseCliPath = string.IsNullOrWhiteSpace(CliPath) ? "firebase" : CliPath.Trim();
    }
}

/// <summary>Deploy to Firebase dialog.</summary>
public sealed record DeployDraft(
    string ApkPath,
    string TesterGroups,
    string TesterEmails,
    string ReleaseNotes)
{
    public static DeployDraft From(CiCdConfig c, string? apk) =>
        new(apk ?? "", c.TesterGroups, c.TesterEmails, c.ReleaseNotes);

    public void ApplyTo(CiCdConfig c)
    {
        c.TesterGroups = TesterGroups.Trim();
        c.TesterEmails = TesterEmails.Trim();
        c.ReleaseNotes = ReleaseNotes;
    }
}

/// <summary>Build Signed APK dialog. Passwords are live strings here; this
/// object is never serialized and ApplyTo pushes them into the SecretStore.</summary>
public sealed record SigningDraft(
    string KeystorePath,
    string StorePassword,
    string KeyAlias,
    string KeyPassword,
    ImmutableArray<string> SelectedVariants)
{
    public static SigningDraft From(ProjectProfile p, Services.SecretStore s, CiCdConfig c)
    {
        string k = p.Key;
        return new(p.KeystorePath, s.Get($"{k}/store-password"), p.KeyAlias,
                   s.Get($"{k}/key-password"), [.. c.BuildVariants]);
    }

    public void ApplyTo(ProjectProfile p, Services.SecretStore s, CiCdConfig c)
    {
        string k = p.Key;
        p.KeystorePath = KeystorePath.Trim();
        p.KeyAlias = KeyAlias.Trim();
        s.Set($"{k}/store-password", StorePassword);
        s.Set($"{k}/key-password", KeyPassword);
        c.BuildVariants = [.. SelectedVariants];
    }
}

/// <summary>Full Pipeline dialog: everything in, a complete result out.</summary>
public sealed record PipelineDialogModel(
    SigningDraft Signing,
    FirebaseConfigDraft Firebase,
    DeployTargets Deploy,
    GitSourceDraft Source,
    IReadOnlyList<string> AvailableVariants,
    bool RunUnitTests,
    bool RunInstrumentTests,
    TestFailureAction FailureAction);

public sealed record DeployTargets(string Groups, string Emails, string Notes);

public sealed record GitSourceDraft(
    bool SyncBeforeRun, string Remote, string Branch,
    string Owner, string Repository, string Token);

public sealed record PipelineDialogResult(
    SigningDraft Signing,
    FirebaseConfigDraft Firebase,
    DeployTargets Deploy,
    GitSourceDraft Source,
    bool RunUnitTests,
    bool RunInstrumentTests,
    TestFailureAction FailureAction)
{
    public bool SyncSource => Source.SyncBeforeRun;

    /// <summary>Commit every field at once — no partial writes on failure.</summary>
    public void ApplyTo(ProjectProfile p, CiCdConfig c, Services.SecretStore s)
    {
        Signing.ApplyTo(p, s, c);
        Firebase.ApplyTo(p);
        c.TesterGroups = Deploy.Groups.Trim();
        c.TesterEmails = Deploy.Emails.Trim();
        c.ReleaseNotes = Deploy.Notes;
        c.SyncBranchBeforePipeline = Source.SyncBeforeRun;
        c.PipelineRemoteName = Source.Remote.Trim();
        c.PipelineBranch = Source.Branch.Trim();
        p.GitHubOwner = Source.Owner.Trim();
        p.GitHubRepo = Source.Repository.Trim();
        if (!string.IsNullOrEmpty(Source.Token)) s.Set($"{p.Key}/github-token", Source.Token);
        c.RunUnitTests = RunUnitTests;
        c.RunInstrumentTests = RunInstrumentTests;
        c.FailureAction = FailureAction;
    }
}

/// <summary>Automation (schedule + email) dialog.</summary>
public sealed record AutomationDraft(
    string Remote, string Branch,
    string Owner, string Repository, string Token,
    bool RecurringEnabled, string RunTimes,
    TestFailureAction FailureAction,
    EmailDraft Email)
{
    public void ApplyTo(AutomationConfig a, ProjectProfile p, Services.SecretStore s)
    {
        a.RemoteName = Remote.Trim();
        a.SourceBranch = Branch.Trim();
        a.RecurringRunsEnabled = RecurringEnabled;
        a.RecurringRunTimes = RunTimes.Trim();
        a.FailureAction = FailureAction;
        p.GitHubOwner = Owner.Trim();
        p.GitHubRepo = Repository.Trim();
        if (!string.IsNullOrEmpty(Token)) s.Set($"{p.Key}/github-token", Token);
        Email.ApplyTo(a.Email, s);
    }
}

public sealed record EmailDraft(
    bool Send, string To, string From, string Host, int Port,
    bool Ssl, string UserName, string Password)
{
    public void ApplyTo(EmailNotificationConfig e, Services.SecretStore s)
    {
        e.SendEmail = Send;
        e.ToAddress = To.Trim();
        e.FromAddress = From.Trim();
        e.SmtpHost = Host.Trim();
        e.SmtpPort = Port;
        e.EnableSsl = Ssl;
        e.UserName = UserName.Trim();
        if (!string.IsNullOrEmpty(Password))
            s.Set(Services.EmailNotificationService.PasswordKey, Password);
    }
}
