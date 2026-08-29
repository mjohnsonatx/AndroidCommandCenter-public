namespace AndroidCommandCenter.Models;

public static class GitNames
{
    /// <summary>
    /// Conservative validation for names interpolated into git commands.
    /// Rejects anything that could be read as an option, a path escape, or a
    /// shell metacharacter. Deliberately stricter than git's own rules.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Length > 255) return false;
        if (v.StartsWith('-')) return false;                 // option injection
        if (v.Contains("..")) return false;                  // git refuses anyway
        if (v.StartsWith('/') || v.EndsWith('/')) return false;
        if (v.EndsWith(".lock")) return false;
        foreach (var c in v)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (c is '"' or '\'' or '\\' or '~' or '^' or ':' or '?' or '*'
                or '[' or ']' or '$' or '`' or ';' or '|' or '&'
                or '(' or ')' or '<' or '>' or '!' or '#') return false;
        }
        return true;
    }

    public static string Require(string? value, string label) =>
        IsValid(value)
            ? value!.Trim()
            : throw new ArgumentException($"Invalid git {label}: '{value}'.", label);
}