namespace Pressroom.Server.Authentication;

public sealed class AuthSettings
{
    public bool WindowsEnabled { get; set; } = true;
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.Ordinal);
    public bool RequireGroup { get; set; }
    public string? AccessGroup { get; set; }
}

public static class AuthenticationNames
{
    public const string Selector = "Pressroom";
    public const string ApiKey = "Pressroom.ApiKey";
    public const string ApiKeyHeader = "X-Api-Key";
    public const string BranchClaim = "pressroom:authentication";

    public static string SelectScheme(bool apiKeyHeaderPresent, bool windowsEnabled) =>
        apiKeyHeaderPresent || !windowsEnabled ? ApiKey : Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme;
}
