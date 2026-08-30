using System.Diagnostics;

namespace AndroidCommandCenter.Services;

/// <summary>
/// Reads the active GitHub CLI token without sending it through ProcessRunner,
/// whose normal output fan-out is intentionally visible in the application log.
/// </summary>
public sealed class GitHubCliCredentialService
{
    public async Task<string> ReadTokenAsync(CancellationToken ct)
    {
        CliCommand command = ResolveCommand();

        var psi = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in command.PrefixArguments)
            psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("token");
        psi.ArgumentList.Add("--hostname");
        psi.ArgumentList.Add("github.com");
        psi.Environment["GH_PROMPT_DISABLED"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start GitHub CLI.");
        string output = await process.StandardOutput.ReadToEndAsync(ct);
        string error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "GitHub CLI is not signed in with a usable token. Run " +
                "`gh auth login --hostname github.com --git-protocol https --web`, then try again. " +
                FirstLine(error));

        string token = output.Trim();
        if (token.Length == 0)
            throw new InvalidOperationException("GitHub CLI returned an empty token.");
        return token;
    }

    private static CliCommand ResolveCommand()
    {
        // A native install can invoke gh directly.
        if (Platform.Which("gh") is { } gh)
            return new CliCommand(gh, []);

        // Rider's Flatpak has a sandboxed /usr/bin. Run gh on the Fedora host so
        // it can also see the host GitHub CLI configuration and keyring login.
        if (Platform.Which("flatpak-spawn") is { } flatpakSpawn)
            return new CliCommand(flatpakSpawn,
                ["--host", "--env=GH_PROMPT_DISABLED=1", "gh"]);

        // Some sandbox configurations expose host binaries directly even when
        // flatpak-spawn is unavailable.
        const string hostGh = "/run/host/usr/bin/gh";
        if (File.Exists(hostGh))
            return new CliCommand(hostGh, []);

        throw new InvalidOperationException(
            "GitHub CLI was not found in this app or on the Fedora host. " +
            "Install it with `sudo dnf install gh`, then restart Rider.");
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private sealed record CliCommand(string Executable, IReadOnlyList<string> PrefixArguments);
}
