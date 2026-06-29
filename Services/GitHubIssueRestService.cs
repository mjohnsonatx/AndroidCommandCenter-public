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
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("AndroidCommandCenter");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrWhiteSpace(token) ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<GitHubIssueModel>> GetIssuesAsync(
        string owner, string repo, string token,
        string state = "open", CancellationToken ct = default)
    {
        Auth(token);
        var all = new List<GitHubIssueModel>();
        for (int page = 1; page <= 10; page++)
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
            all.AddRange(batch.Where(i => !i.IsPullRequest));
            if (batch.Count < 100) break;
        }
        LogMessage?.Invoke($"[issues] {all.Count} issues loaded from {owner}/{repo}");
        return all;
    }

    public async Task<List<GitHubCommentDto>> GetCommentsAsync(
        string owner, string repo, string token,
        int issueNumber, CancellationToken ct = default)
    {
        Auth(token);
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}" +
                  $"/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments?per_page=100";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<GitHubCommentDto>>(json, JsonOpts) ?? [];
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