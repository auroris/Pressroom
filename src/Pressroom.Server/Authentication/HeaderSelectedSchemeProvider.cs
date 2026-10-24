using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Options;

namespace Pressroom.Server.Authentication;

/// <summary>
/// Negotiate is a request handler as well as an authentication handler. The standard middleware
/// invokes request handlers before policy-scheme selection. Exclude it from that unconditional
/// pass; the selected Windows branch invokes it explicitly in Program.
/// </summary>
public sealed class HeaderSelectedSchemeProvider(IOptions<AuthenticationOptions> options)
    : AuthenticationSchemeProvider(options)
{
    public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
        (await base.GetRequestHandlerSchemesAsync())
            .Where(scheme => scheme.Name != NegotiateDefaults.AuthenticationScheme);
}
