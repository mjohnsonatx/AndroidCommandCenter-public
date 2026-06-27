using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public enum TestFailureAction
{
    AutomaticBounceBack,  // stop the pipeline immediately
    Warning,              // show details, user chooses continue/stop
    WarningWithConfirm,   // show details, require checkbox to continue
    SilentFailure         // log and continue, no prompt
}

public class CiCdConfig
{
    // ── Signing ──
    public string KeystorePath { get; set; } = "";
    public string KeyAlias { get; set; } = "";
    public string? EncryptedStorePassword { get; set; }
    public string? EncryptedKeyPassword { get; set; }

    // ── Build ──
    public List<string> BuildVariants { get; set; } = new();

    // ── Firebase (foundational) ──
    public string FirebaseCliPath { get; set; } = "firebase";
    public string FirebaseAppId { get; set; } = "";
    public string CredentialsJsonPath { get; set; } = "";
    public string? EncryptedFirebaseToken { get; set; }

    // ── Deploy ──
    public string TesterGroups { get; set; } = "testers";
    public string TesterEmails { get; set; } = "";
    public string ReleaseNotes { get; set; } = "New build";

    // ── Pipeline test options ──
    public bool RunUnitTests { get; set; } = true;
    public bool RunInstrumentTests { get; set; } = true;
    public TestFailureAction FailureAction { get; set; } = TestFailureAction.AutomaticBounceBack;

    // ── Plain-text accessors (never serialized) ──
    [JsonIgnore] public string StorePassword { get => Dec(EncryptedStorePassword); set => EncryptedStorePassword = Enc(value); }
    [JsonIgnore] public string KeyPassword { get => Dec(EncryptedKeyPassword); set => EncryptedKeyPassword = Enc(value); }
    [JsonIgnore] public string FirebaseToken { get => Dec(EncryptedFirebaseToken); set => EncryptedFirebaseToken = Enc(value); }

    // ── Validation ──
    [JsonIgnore]
    public bool IsSigningConfigured =>
        !string.IsNullOrEmpty(KeystorePath) && File.Exists(KeystorePath) &&
        !string.IsNullOrEmpty(KeyAlias) &&
        !string.IsNullOrEmpty(StorePassword) && !string.IsNullOrEmpty(KeyPassword);

    [JsonIgnore]
    public bool IsFirebaseConfigured =>
        !string.IsNullOrEmpty(FirebaseAppId) &&
        (!string.IsNullOrEmpty(CredentialsJsonPath) || !string.IsNullOrEmpty(FirebaseToken));

    // ── DPAPI ──
    private static string Enc(string? p) => string.IsNullOrEmpty(p) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(p), null, DataProtectionScope.CurrentUser));
    private static string Dec(string? c)
    {
        if (string.IsNullOrEmpty(c)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(c), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}