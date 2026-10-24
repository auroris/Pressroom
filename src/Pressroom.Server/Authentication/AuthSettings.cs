namespace Pressroom.Server.Authentication;

public sealed class AuthSettings
{
    public AuthMode Mode { get; set; } = AuthMode.ApiKey;
    public Dictionary<string, ApiKeySettings> ApiKeys { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ApiKeySettings
{
    public string Secret { get; set; } = "";
    public bool AllowJavaScript { get; set; }
}

public enum AuthMode
{
    ApiKey,
    Anonymous
}

public static class AuthenticationNames
{
    public const string ApiKey = "Pressroom.ApiKey";
    public const string ApiKeyHeader = "X-Api-Key";
    public const string BranchClaim = "pressroom:authentication";
    public const string JavaScriptClaim = "pressroom:javascript";
}
