using System.Collections.Immutable;
using System.Linq;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

/// <summary>
/// The single place where mutable config becomes an immutable run request.
/// Must be called while the ConfigGate is held.
/// </summary>
public static class RequestFactory
{
    public static PipelineRequest Snapshot(
        ProjectProfile profile,
        CiCdConfig cfg,
        SecretStore secrets,
        string projectPath,
        string activeVariant,
        bool syncSource)
    {
        var variants = cfg.BuildVariants
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (variants.IsEmpty && !string.IsNullOrWhiteSpace(activeVariant))
            variants = [activeVariant];

        string key = profile.Key;

        return new PipelineRequest(
            ProjectPath: projectPath,
            ActiveVariant: activeVariant,
            BuildVariants: variants,
            Signing: new SigningSpec(
                profile.KeystorePath,
                profile.KeyAlias,
                secrets.Get($"{key}/store-password"),
                secrets.Get($"{key}/key-password")),
            Firebase: new FirebaseSpec(
                string.IsNullOrWhiteSpace(profile.FirebaseCliPath) ? "firebase" : profile.FirebaseCliPath,
                profile.FirebaseAppId,
                profile.FirebaseCredentialsJson,
                secrets.Get($"{key}/firebase-token"),
                cfg.TesterGroups, cfg.TesterEmails, cfg.ReleaseNotes),
            Source: new GitSourceSpec(
                syncSource,
                string.IsNullOrWhiteSpace(profile.DefaultRemote) ? "origin" : profile.DefaultRemote,
                profile.DefaultBranch,
                profile.GitHubOwner,
                profile.GitHubRepo,
                secrets.Get($"{key}/github-token")),
            RunUnitTests: cfg.RunUnitTests,
            RunInstrumentTests: cfg.RunInstrumentTests,
            FailureAction: cfg.FailureAction);
    }
}