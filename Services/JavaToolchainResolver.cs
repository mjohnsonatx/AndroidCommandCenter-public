using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Services;

public sealed record JavaInstallation(
    string Home, int Major, string Version, string Vendor, string Source, int Rank)
{
    public string JavaPath => Path.Combine(Home, "bin", Platform.IsWindows ? "java.exe" : "java");
    public string JavacPath => Path.Combine(Home, "bin", Platform.IsWindows ? "javac.exe" : "javac");
    public bool IsJdk => File.Exists(JavacPath);
    public string Display => $"{(IsJdk ? "JDK" : "JRE")} {Major} · {Vendor} · {Home}";
}

public sealed record ProjectJavaEnvironment(
    JavaInstallation? Launcher,
    int? RequestedDaemonMajor,
    string RequestedVendor,
    IReadOnlyList<int> RequiredToolchainMajors,
    IReadOnlyList<JavaInstallation> Installations,
    string SelectionReason)
{
    public string ShortDisplay => Launcher is null
        ? "not found"
        : $"JDK {Launcher.Major} ({ShortVendor(Launcher.Vendor)})";

    public string ToolchainPaths => string.Join(',', Installations
        .Where(j => j.IsJdk)
        .Select(j => j.Home)
        .Distinct(StringComparer.Ordinal));

    public Dictionary<string, string> BuildEnvironment(
        IReadOnlyDictionary<string, string>? additions = null)
    {
        var env = additions is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(additions);
        if (Launcher is not null)
        {
            env["JAVA_HOME"] = Launcher.Home;
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            env["PATH"] = Path.Combine(Launcher.Home, "bin") + Path.PathSeparator + currentPath;
        }
        return env;
    }

    public string Report()
    {
        string daemon = RequestedDaemonMajor is int major
            ? $"JDK {major}" + (RequestedVendor.Length > 0 ? $" ({RequestedVendor})" : "")
            : "not pinned by project";
        string compile = RequiredToolchainMajors.Count == 0
            ? "not declared"
            : string.Join(", ", RequiredToolchainMajors.Select(v => $"JDK {v}"));
        return $"Gradle launcher: {Launcher?.Display ?? "NOT FOUND"}\n" +
               $"Daemon criteria: {daemon}\n" +
               $"Compile toolchains: {compile}\n" +
               $"Selection: {SelectionReason}\n" +
               "Detected JDKs:\n" +
               string.Join('\n', Installations.Select(j => $"  - {j.Display}"));
    }

    private static string ShortVendor(string vendor) =>
        vendor.Contains("JetBrains", StringComparison.OrdinalIgnoreCase) ? "JetBrains"
        : vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Microsoft"
        : vendor.Contains("Red Hat", StringComparison.OrdinalIgnoreCase) ? "Red Hat"
        : vendor.Length > 18 ? vendor[..18] : vendor;
}

/// <summary>
/// Resolves the Java runtime Gradle should launch with and advertises every
/// local JDK to Gradle's toolchain service. Modern Android projects commonly
/// use one JDK for the Gradle daemon (for example 21) while compiling source
/// with a different toolchain (for example 17).
/// </summary>
public static class JavaToolchainResolver
{
    private static readonly Regex JavaVersion = new(
        @"JAVA_VERSION\s*=\s*\""(?<version>[^\""\r\n]+)\""", RegexOptions.Compiled);
    private static readonly Regex Implementor = new(
        @"IMPLEMENTOR\s*=\s*\""(?<vendor>[^\""\r\n]+)\""", RegexOptions.Compiled);
    private static readonly Regex ToolchainCall = new(
        @"(?:jvmToolchain|JavaLanguageVersion\.of)\s*\(\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex JavaCompatibility = new(
        @"JavaVersion\.VERSION_(\d+)", RegexOptions.Compiled);

    public static ProjectJavaEnvironment Resolve(string? projectPath)
    {
        var installations = DiscoverInstallations();
        var (daemonMajor, daemonVendor) = ReadDaemonCriteria(projectPath);
        var required = ReadRequiredToolchains(projectPath);

        JavaInstallation? launcher;
        string reason;
        if (daemonMajor is int requested)
        {
            var exact = installations.Where(j => j.Major == requested && j.IsJdk).ToList();
            launcher = exact
                .OrderBy(j => VendorMatches(j.Vendor, daemonVendor) ? 0 : 1)
                .ThenBy(j => j.Rank)
                .ThenByDescending(j => j.Version, StringComparer.Ordinal)
                .FirstOrDefault();
            reason = launcher is null
                ? $"Project requests JDK {requested}, but no matching local JDK was found."
                : $"Matched gradle/gradle-daemon-jvm.properties (JDK {requested}).";
        }
        else
        {
            launcher = installations
                .Where(j => j.IsJdk)
                .OrderBy(j => j.Rank)
                .ThenByDescending(j => j.Major)
                .FirstOrDefault();
            reason = launcher is null
                ? "No local JDK was found."
                : $"No daemon criteria found; selected {launcher.Source}.";
        }

        return new(launcher, daemonMajor, daemonVendor, required,
            installations, reason);
    }

    private static List<JavaInstallation> DiscoverInstallations()
    {
        var found = new Dictionary<string, JavaInstallation>(StringComparer.Ordinal);
        void Add(string? home, string source, int rank)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            try
            {
                home = NormalizeHome(home);
                if (home is null || found.ContainsKey(home)) return;
                string release = Path.Combine(home, "release");
                string java = Path.Combine(home, "bin", Platform.IsWindows ? "java.exe" : "java");
                if (!File.Exists(release) || !File.Exists(java)) return;
                string text = File.ReadAllText(release);
                string version = JavaVersion.Match(text).Groups["version"].Value;
                if (!TryMajor(version, out int major)) return;
                string vendor = Implementor.Match(text).Groups["vendor"].Value;
                if (vendor.Length == 0) vendor = "Unknown vendor";
                found[home] = new(home, major, version, vendor, source, rank);
            }
            catch { }
        }

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME", 10);
        Add(Environment.GetEnvironmentVariable("STUDIO_JDK"), "STUDIO_JDK", 10);
        Add(Environment.GetEnvironmentVariable("ANDROID_STUDIO_JDK"), "ANDROID_STUDIO_JDK", 10);

        Add(Path.Combine(user, ".gradle", "jdks"), "Gradle-managed JDK", 5);
        foreach (var home in ChildDirectories(Path.Combine(user, ".gradle", "jdks")))
            Add(home, "Gradle-managed JDK", 5);

        Add("/opt/android-studio/jbr", "Android Studio runtime", 15);
        foreach (var home in ChildDirectories(Path.Combine(user, ".jdks")))
            Add(home, "User JDK", 20);
        foreach (var home in ChildDirectories("/usr/lib/jvm"))
            Add(home, "System JDK", 30);

        string? pathJava = Platform.Which("java");
        if (pathJava is not null)
        {
            try
            {
                var target = new FileInfo(pathJava).ResolveLinkTarget(returnFinalTarget: true);
                Add(Path.GetDirectoryName(Path.GetDirectoryName(target?.FullName ?? pathJava)),
                    "java on PATH", 25);
            }
            catch { }
        }

        return found.Values
            .OrderBy(j => j.Major)
            .ThenBy(j => j.Rank)
            .ToList();
    }

    private static string? NormalizeHome(string input)
    {
        string path = Path.GetFullPath(input.Trim());
        if (File.Exists(path))
            path = Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? path;
        if (Directory.Exists(Path.Combine(path, "Contents", "Home")))
            path = Path.Combine(path, "Contents", "Home");
        return Directory.Exists(path) ? path.TrimEnd(Path.DirectorySeparatorChar) : null;
    }

    private static IEnumerable<string> ChildDirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch { return []; }
    }

    private static (int? Major, string Vendor) ReadDaemonCriteria(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return (null, "");
        string path = Path.Combine(project, "gradle", "gradle-daemon-jvm.properties");
        if (!File.Exists(path)) return (null, "");
        try
        {
            int? major = null;
            string vendor = "";
            foreach (var line in File.ReadLines(path))
            {
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2) continue;
                if (pair[0] == "toolchainVersion" && int.TryParse(pair[1], out int value)) major = value;
                if (pair[0] == "toolchainVendor") vendor = pair[1];
            }
            return (major, vendor);
        }
        catch { return (null, ""); }
    }

    private static IReadOnlyList<int> ReadRequiredToolchains(string? project)
    {
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project)) return [];
        var versions = new SortedSet<int>();
        try
        {
            var files = Directory.GetFiles(project, "build.gradle*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(project)
                    .Where(d => !Path.GetFileName(d).StartsWith('.') &&
                                Path.GetFileName(d) is not "build" and not "node_modules")
                    .SelectMany(d => Directory.GetFiles(d, "build.gradle*", SearchOption.TopDirectoryOnly)));
            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                foreach (Match match in ToolchainCall.Matches(text))
                    if (int.TryParse(match.Groups[1].Value, out int major)) versions.Add(major);
                foreach (Match match in JavaCompatibility.Matches(text))
                    if (int.TryParse(match.Groups[1].Value, out int major)) versions.Add(major);
            }
        }
        catch { }
        return versions.ToList();
    }

    private static bool VendorMatches(string actual, string requested)
    {
        if (requested.Length == 0) return true;
        string want = requested.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        string have = actual.Replace(" ", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        return have.Contains(want, StringComparison.OrdinalIgnoreCase) ||
               requested.Equals("JETBRAINS", StringComparison.OrdinalIgnoreCase) &&
               actual.Contains("JetBrains", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMajor(string version, out int major)
    {
        major = 0;
        var parts = version.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!int.TryParse(parts[0], out major)) return false;
        if (major == 1 && parts.Length > 1) return int.TryParse(parts[1], out major);
        return true;
    }
}
