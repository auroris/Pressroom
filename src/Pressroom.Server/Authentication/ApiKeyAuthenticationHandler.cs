using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pressroom.Server.Infrastructure;

namespace Pressroom.Server.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthSettings> authentication)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AuthenticationNames.ApiKeyHeader, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        // Empty, duplicate and oversized headers select this branch and fail here, never Windows.
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || values[0]!.Length > 4096)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        string? keyId = null;
        foreach (var pair in authentication.Value.ApiKeys)
        {
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value));
            var matches = CryptographicOperations.FixedTimeEquals(candidate, expected);
            if (matches)
                keyId = pair.Key;
            CryptographicOperations.ZeroMemory(expected);
        }
        CryptographicOperations.ZeroMemory(candidate);

        if (keyId is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, keyId),
            new Claim(ClaimTypes.Name, keyId),
            new Claim(AuthenticationNames.BranchClaim, AuthenticationNames.ApiKey)
        ], AuthenticationNames.ApiKey);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(Context, StatusCodes.Status401Unauthorized, "Authentication required", "Supply a valid API key or use the configured Windows authentication path.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(Context, StatusCodes.Status403Forbidden, "Access denied", "The authenticated caller is not authorized.");
}
