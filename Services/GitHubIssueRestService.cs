using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public class GitHubIssueRestService
{
    private readonly HttpClient _http = new();
    public event Action<string>? LogMessage;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private void Auth(string token)
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("User-Agent", "AndroidCommandCenter");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<string> GetAuthenticatedLoginAsync(
        string token, CancellationToken ct = default)
    {
        token = token.Trim();
        if (token.Length == 0)
            throw new InvalidOperationException("GitHub token is empty.");

        Auth(token);
        using var response = await _http.GetAsync("https://api.github.com/user", ct);
        await RequireSuccess(response, "GitHub authentication", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        string login = json.RootElement.TryGetProperty("login", out var value)
            ? value.GetString() ?? ""
            : "";
        if (login.Length == 0)
            throw new InvalidOperationException(
                "GitHub accepted the token but did not return an account login.");

        LogMessage?.Invoke($"[github] authenticated as @{login}");
        return login;
    }

    // ── LIST REPOS FOR AUTHENTICATED USER OR ORG ──────────────

    /// <summary>
    /// Lists every repository visible to the authenticated token, including
    /// private repositories. An optional owner filters that authenticated list
    /// locally. GitHub's public user-repository endpoint cannot return private
    /// repositories and must not be used for this operation.
    /// </summary>
    public async Task<List<GitHubRepoDto>> GetReposAsync(
        string token, string? owner = null, CancellationToken ct = default)
    {
        await GetAuthenticatedLoginAsync(token, ct);
        owner = owner?.Trim();
        var all = new List<GitHubRepoDto>();

        for (int page = 1; page <= 20; page++)
        {
            ct.ThrowIfCancellationRequested();

            string url = "https://api.github.com/user/repos" +
                "?visibility=all&affiliation=owner,collaborator,organization_member" +
                $"&per_page=100&page={page}&sort=updated&direction=desc";

            LogMessage?.Invoke($"[repos] fetching page {page}…");
            using var resp = await _http.GetAsync(url, ct);
            await RequireSuccess(resp, "GitHub repository request", ct);

            var json = await resp.Content.ReadAsStringAsync(ct);
            var batch = JsonSerializer.Deserialize<List<GitHubRepoDto>>(json, JsonOpts) ?? [];
            all.AddRange(string.IsNullOrWhiteSpace(owner)
                ? batch
                : batch.Where(repo => string.Equals(
                    repo.OwnerLogin, owner, StringComparison.OrdinalIgnoreCase)));
            if (batch.Count < 100) break;
        }

        var distinct = all
            .DistinctBy(repo => repo.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(repo => repo.UpdatedAt)
            .ToList();
        LogMessage?.Invoke($"[repos] loaded {distinct.Count} authenticated repositories" +
                           (string.IsNullOrWhiteSpace(owner) ? "" : $" owned by {owner}"));
        return distinct;
    }

    private static async Task RequireSuccess(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(ct);
        string detail = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var message))
                detail = message.GetString() ?? "";
        }
        catch { }

        string guidance = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? " The saved token is invalid or expired; run `gh auth login` and save a fresh `gh auth token`."
            : "";
        throw new InvalidOperationException(
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}{guidance}".Trim());
    }

    public async Task<List<GitHubIssueModel>> GetIssuesAsync(
        string owner, string repo, string token,
        string state = "open", CancellationToken ct = default)
    {
        Auth(token);
        var all = new List<GitHubIssueModel>();

        for (int page = 1; page <= 30; page++)   // up to 3000 issues
        {
            ct.ThrowIfCancellationRequested();
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}" +
                      $"/{Uri.EscapeDataString(repo)}/issues" +
                      $"?state={state}&per_page=100&page={page}&sort=updated&direction=desc";

            LogMessage?.Invoke($"[issues] fetching page {page}…");
            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var batch = JsonSerializer.Deserialize<List<GitHubIssueModel>>(json, JsonOpts) ?? [];

            // GitHub includes PRs in the issues endpoint — filter them out
            all.AddRange(batch.Where(i => !i.IsPullRequest));

            if (batch.Count < 100) break;
        }

        LogMessage?.Invoke($"[issues] {all.Count} issues loaded from {owner}/{repo}");
        return all;
    }

    // ── COMMENTS ──────────────────────────────────────────────

    public async Task<List<GitHubCommentDto>> GetCommentsAsync(
        string owner, string repo, string token,
        int issueNumber, CancellationToken ct = default)
    {
        Auth(token);
        var all = new List<GitHubCommentDto>();

        for (int page = 1; page <= 10; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}" +
                      $"/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments" +
                      $"?per_page=100&page={page}";

            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var batch = JsonSerializer.Deserialize<List<GitHubCommentDto>>(json, JsonOpts) ?? [];
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }

        return all;
    }

    public async Task<bool> AddCommentAsync(
        string owner, string repo, string token,
        int issueNumber, string body, CancellationToken ct = default)
    {
        Auth(token);
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}" +
                  $"/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments";
        var payload = new StringContent(
            JsonSerializer.Serialize(new { body }), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(url, payload, ct);
        bool ok = resp.IsSuccessStatusCode;
        LogMessage?.Invoke(ok ? $"[issues] comment added to #{issueNumber}"
                               : $"[issues] comment failed: {resp.StatusCode}");
        return ok;
    }

    public async Task<bool> UpdateStateAsync(
        string owner, string repo, string token,
        int issueNumber, string state, CancellationToken ct = default)
    {
        Auth(token);
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}" +
                  $"/{Uri.EscapeDataString(repo)}/issues/{issueNumber}";
        var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { state }), Encoding.UTF8, "application/json")
        };
        var resp = await _http.SendAsync(req, ct);
        bool ok = resp.IsSuccessStatusCode;
        LogMessage?.Invoke(ok ? $"[issues] #{issueNumber} → {state}"
                               : $"[issues] state update failed: {resp.StatusCode}");
        return ok;
    }
}
