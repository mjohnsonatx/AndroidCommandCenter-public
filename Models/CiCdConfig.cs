using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public class CiCdConfig
{
    // ── Signing ──────────────────────────────────
    public string KeystorePath { get; set; } = "";
    public string KeyAlias { get; set; } = "";
    public string? EncryptedStorePassword { get; set; }
    public string? EncryptedKeyPassword { get; set; }

    // ── Firebase ─────────────────────────────────
    public string FirebaseCliPath { get; set; } = "firebase";
    public string FirebaseAppId { get; set; } = "";
    public string DistributionGroups { get; set; } = "testers";
    public string? EncryptedFirebaseToken { get; set; }
    public string ServiceAccountPath { get; set; } = "";
    public string ReleaseNotes { get; set; } = "New build";

    // ── Plain-text accessors (never serialized) ──
    [JsonIgnore]
    public string StorePassword
    {
        get => Decrypt(EncryptedStorePassword);
        set => EncryptedStorePassword = Encrypt(value);
    }
    [JsonIgnore]
    public string KeyPassword
    {
        get => Decrypt(EncryptedKeyPassword);
        set => EncryptedKeyPassword = Encrypt(value);
    }
    [JsonIgnore]
    public string FirebaseToken
    {
        get => Decrypt(EncryptedFirebaseToken);
        set => EncryptedFirebaseToken = Encrypt(value);
    }

    // ── Validation ───────────────────────────────
    [JsonIgnore]
    public bool IsSigningConfigured =>
        !string.IsNullOrEmpty(KeystorePath) && File.Exists(KeystorePath) &&
        !string.IsNullOrEmpty(KeyAlias) && !string.IsNullOrEmpty(StorePassword);

    [JsonIgnore]
    public bool IsFirebaseConfigured =>
        !string.IsNullOrEmpty(FirebaseAppId) &&
        (!string.IsNullOrEmpty(FirebaseToken) || !string.IsNullOrEmpty(ServiceAccountPath));

    // ── DPAPI helpers ────────────────────────────
    private static string Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var enc = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    private static string Decrypt(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var dec = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }
}