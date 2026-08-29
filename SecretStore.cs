using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidCommandCenter.Services;

/// <summary>
/// Secret storage backed by the desktop keyring (libsecret / secret-tool).
///
/// Secrets are NEVER written to settings.json. The JSON holds only an opaque
/// handle; the value lives in the keyring. If the keyring is unavailable we fall
/// back to an AES-GCM file encrypted with a key in ~/.config (mode 0600) and
/// report DEGRADED, so the UI can tell the user their signing password is
/// protected only by file permissions.
/// </summary>
public sealed class SecretStore
{
    public enum Backend { Keyring, EncryptedFile, Unavailable }

    private const string SchemaAttr = "android-command-center";
    private static readonly Lazy<Backend> _backend = new(Detect);
    public static Backend ActiveBackend => _backend.Value;

    public static string BackendDescription => ActiveBackend switch
    {
        Backend.Keyring       => "System keyring (libsecret)",
        Backend.EncryptedFile => "Encrypted file (DEGRADED — keyring unavailable)",
        _                     => "UNAVAILABLE — secrets cannot be stored"
    };

    private readonly Dictionary<string, string> _memo = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private static Backend Detect()
    {
        if (Platform.Which("secret-tool") is null) return Backend.EncryptedFile;
        try
        {
            // A store+lookup round trip is the only reliable probe: secret-tool
            // exists even when no keyring daemon is running (e.g. headless ssh).
            var probe = Guid.NewGuid().ToString("N");
            if (RawStore("__probe__", probe) && RawLookup("__probe__") == probe)
            {
                RawClear("__probe__");
                return Backend.Keyring;
            }
        }
        catch { }
        return Backend.EncryptedFile;
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        lock (_gate)
        {
            if (_memo.TryGetValue(key, out var cached)) return cached;
            string v = ActiveBackend == Backend.Keyring
                ? RawLookup(key) ?? ""
                : FileBackend.Get(key);
            _memo[key] = v;
            return v;
        }
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate)
        {
            _memo[key] = value ?? "";
            if (string.IsNullOrEmpty(value)) { Delete(key); return; }
            bool ok = ActiveBackend == Backend.Keyring
                ? RawStore(key, value)
                : FileBackend.Set(key, value);
            if (!ok)
                throw new IOException(
                    $"Could not persist secret '{key}' to {BackendDescription}. " +
                    "It is held in memory for this session only.");
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            _memo.Remove(key);
            if (ActiveBackend == Backend.Keyring) RawClear(key);
            else FileBackend.Delete(key);
        }
    }

    // ── secret-tool shell-out ──────────────────────────────────────────
    private static bool RawStore(string key, string value)
    {
        var psi = Psi("secret-tool", "store", "--label", $"ACC: {key}", SchemaAttr, key);
        psi.RedirectStandardInput = true;
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardInput.Write(value);   // no newline: secret-tool takes raw stdin
        p.StandardInput.Close();
        return p.WaitForExit(5000) && p.ExitCode == 0;
    }

    private static string? RawLookup(string key)
    {
        var psi = Psi("secret-tool", "lookup", SchemaAttr, key);
        psi.RedirectStandardOutput = true;
        using var p = Process.Start(psi);
        if (p == null) return null;
        string outp = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000) || p.ExitCode != 0) return null;
        return outp;                    // secret-tool lookup adds no trailing newline
    }

    private static void RawClear(string key)
    {
        try
        {
            using var p = Process.Start(Psi("secret-tool", "clear", SchemaAttr, key));
            p?.WaitForExit(5000);
        }
        catch { }
    }

    private static ProcessStartInfo Psi(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // ── AES-GCM file fallback ──────────────────────────────────────────
    private static class FileBackend
    {
        private static string KeyFile  => Path.Combine(Paths.ConfigDir, "secrets.key");
        private static string DataFile => Path.Combine(Paths.ConfigDir, "secrets.enc");
        private static readonly object Gate = new();

        private static byte[] Key()
        {
            lock (Gate)
            {
                if (File.Exists(KeyFile))
                {
                    var k = File.ReadAllBytes(KeyFile);
                    if (k.Length == 32) return k;
                }
                var fresh = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(KeyFile, fresh);
                if (!Platform.IsWindows)
                    File.SetUnixFileMode(KeyFile,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return fresh;
            }
        }

        private static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(DataFile)) return new();
                var blob = File.ReadAllBytes(DataFile);
                if (blob.Length < 28) return new();
                var nonce = blob.AsSpan(0, 12).ToArray();
                var tag   = blob.AsSpan(12, 16).ToArray();
                var ct    = blob.AsSpan(28).ToArray();
                var pt    = new byte[ct.Length];
                using var aes = new AesGcm(Key(), 16);
                aes.Decrypt(nonce, ct, tag, pt);
                return System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(pt) ?? new();
            }
            catch { return new(); }   // corrupt/rotated key -> treat as empty
        }

        private static bool Save(Dictionary<string, string> map)
        {
            try
            {
                var pt = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(map);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ct = new byte[pt.Length];
                var tag = new byte[16];
                using var aes = new AesGcm(Key(), 16);
                aes.Encrypt(nonce, pt, ct, tag);

                var tmp = DataFile + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    fs.Write(nonce); fs.Write(tag); fs.Write(ct);
                }
                if (!Platform.IsWindows)
                    File.SetUnixFileMode(tmp,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(tmp, DataFile, overwrite: true);  // atomic replace
                return true;
            }
            catch { return false; }
        }

        public static string Get(string k) => Load().TryGetValue(k, out var v) ? v : "";

        public static bool Set(string k, string v)
        {
            lock (Gate) { var m = Load(); m[k] = v; return Save(m); }
        }

        public static void Delete(string k)
        {
            lock (Gate) { var m = Load(); if (m.Remove(k)) Save(m); }
        }
    }
}