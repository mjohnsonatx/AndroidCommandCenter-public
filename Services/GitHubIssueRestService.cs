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

    // ── LIST REPOS FOR AUTHENTICATED USER OR ORG ──────────────

    /// <summary>
    /// Lists repos the token has access to. For a personal token this returns
    /// the user's repos; for an org owner it returns org repos too.
    /// Returns repo full names like "owner/repo-name".
    /// </summary>
    public async Task<List<GitHubRepoDto>> GetReposAsync(
        string token, string? owner = null, CancellationToken ct = default)
    {
        Auth(token);
        var all = new List<GitHubRepoDto>();

        for (int page = 1; page <= 20; page++)
        {
            ct.ThrowIfCancellationRequested();

            // If owner specified, use /users/{owner}/repos
            // Otherwise use /user/repos for the authenticated user
            string url = string.IsNullOrWhiteSpace(owner)
                ? $"https://api.github.com/user/repos?per_page=100&page={page}&sort=updated&direction=desc"
                : $"https://api.github.com/users/{Uri.EscapeDataString(owner)}/repos?per_page=100&page={page}&sort=updated&direction=desc";

            LogMessage?.Invoke($"[repos] fetching page {page}…");
            var resp = await _http.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                // If /users/ 404s, try /orgs/
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    && !string.IsNullOrWhiteSpace(owner))
                {
                    url = $"https://api.github.com/orgs/{Uri.EscapeDataString(owner)}/repos?per_page=100&page={page}&sort=updated&direction=desc";
                    resp = await _http.GetAsync(url, ct);
                }
                resp.EnsureSuccessStatusCode();
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var batch = JsonSerializer.Deserialize<List<GitHubRepoDto>>(json, JsonOpts) ?? [];
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }

        LogMessage?.Invoke($"[repos] loaded {all.Count} repositories");
        return all;
    }

    // ── LIST ISSUES WITH PROPER PAGINATION ────────────────────

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