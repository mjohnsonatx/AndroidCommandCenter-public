using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AndroidCommandCenter.Services;

public class GitHubService
{
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private readonly HttpClient _client = new();

    public event Action<string>? LogMessage;

    public async Task<IReadOnlyList<string>> GetBranchesAsync(
        string owner,
        string repository,
        string token,
        CancellationToken ct)
    {
        owner = CleanPathSegment(owner, "owner");
        repository = CleanPathSegment(repository, "repository");

        var branches = new List<string>();
        for (int page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(ApiRoot, $"repos/{owner}/{repository}/branches?per_page=100&page={page}"));
            request.Headers.UserAgent.ParseAdd("AndroidCommandCenter");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                string message = TryReadGitHubMessage(body);
                throw new InvalidOperationException(
                    $"GitHub branch request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {message}".Trim());
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            int pageCount = 0;

            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameElement) &&
                    nameElement.GetString() is { Length: > 0 } name)
                {
                    branches.Add(name);
                    pageCount++;
                }
            }

            if (pageCount < 100)
                break;
        }

        LogMessage?.Invoke($"Loaded {branches.Count} GitHub branch(es).");
        return branches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CleanPathSegment(string value, string label)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"Invalid GitHub {label}: {value}");
        }

        return value;
    }

    private static string TryReadGitHubMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var message) &&
                !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return message.GetString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }
}
