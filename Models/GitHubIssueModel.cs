using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public class GitHubIssueModel
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "open";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("labels")] public List<GitHubLabelDto> Labels { get; set; } = [];
    [JsonPropertyName("user")] public GitHubUserDto? User { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("comments")] public int CommentCount { get; set; }
    [JsonPropertyName("pull_request")] public object? PullRequest { get; set; }

    [JsonIgnore] public bool IsPullRequest => PullRequest != null;
    [JsonIgnore]
    public string LabelsSummary =>
        string.Join(", ", Labels.Select(l => l.Name));
    [JsonIgnore]
    public string CreatedDisplay =>
        CreatedAt.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string Author => User?.Login ?? "";
}

public class GitHubLabelDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "";
}

public class GitHubUserDto
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
}

public class GitHubCommentDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("user")] public GitHubUserDto? User { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }

    [JsonIgnore]
    public string Header =>
        $"@{User?.Login ?? "unknown"}  ·  {CreatedAt:yyyy-MM-dd HH:mm}";
}