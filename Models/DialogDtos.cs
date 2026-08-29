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

/// <summary>Build Signed APK dialog. Passwords are live strings here — this
/// object never gets serialized; ApplyTo pushes them into the SecretStore.</summary>
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