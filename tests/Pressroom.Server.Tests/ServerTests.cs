using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server;
using Pressroom.Server.Authentication;
using Pressroom.Server.Infrastructure;
using Pressroom.Server.Printing;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Pressroom.Server.Tests;

public sealed class ServerTests
{
    [Fact]
    public void TestHostLoadsOnlyPackagedDefaultsAndItsOwnSettingsFile()
    {
        // The harness adds no configuration sources of its own, so the files present here are
        // exactly those the entry point opened. The machine-wide default must never be one of them.
        using var app = new TestApplication();
        var configuration = (IConfigurationRoot)app.Services.GetRequiredService<IConfiguration>();
        var files = configuration.Providers.OfType<FileConfigurationProvider>()
            .Select(provider => provider.Source.FileProvider!.GetFileInfo(provider.Source.Path!).PhysicalPath!)
            .Select(path => Path.GetFullPath(path)).ToArray();
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.json")), Path.GetFullPath(app.SettingsPath) }, files);
        Assert.DoesNotContain(Path.GetFullPath(ApplicationConfiguration.DefaultConfigurationFile), files);
        Assert.Equal(TestApplication.Secret, configuration["Auth:ApiKeys:integration-test:Secret"]);
    }

    [Fact]
    public async Task HealthIsAnonymousButInfoAndOpenApiRequireAuthentication()
    {
        using var app = new TestApplication();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/health", TestContext.Current.CancellationToken)).StatusCode);
        foreach (var path in new[] { "/v1/info", "/openapi/v1.json" })
        {
            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(response.Headers.WwwAuthenticate);
        }
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApplication.Secret);
        using var info = await client.GetAsync("/v1/info", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.DoesNotContain(TestApplication.Secret, await info.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        Assert.Equal(2, document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("PageSize").GetProperty("oneOf").GetArrayLength());
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Single(schemes.EnumerateObject());
        Assert.Equal("apiKey", schemes.GetProperty("ApiKey").GetProperty("type").GetString());
        Assert.Single(document.RootElement.GetProperty("security").EnumerateArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-key-with-enough-length")]
    public async Task InvalidKeyCannotUsePreexistingWindowsPrincipalOrChallengeNegotiate(string key)
    {
        using var app = new TestApplication(preauthenticatedPrincipal: true, javaScriptEnabled: true);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/render") { Content = JavaScriptBody(true) };
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        if (key.Length == 0)
            request.Headers.Add("X-Test-Empty-Key", "1");
        request.Headers.TryAddWithoutValidation("Authorization", "Negotiate deliberately-not-a-token");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task ValidKeyIgnoresWindowsCredentials()
    {
        using var app = new TestApplication(preauthenticatedPrincipal: true);
        using var client = app.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Negotiate deliberately-not-a-token");
        using var response = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task WindowsPrincipalWithoutKeyCannotAuthorizeRequest()
    {
        using var app = new TestApplication(preauthenticatedPrincipal: true);
        using var client = app.CreateClient();
        using var response = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task AnonymousModeAllowsRequestsWithoutKeysAndDocumentsItsSecurity()
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous, configureKey: false);
        using var client = app.CreateClient();
        foreach (var path in new[] { "/v1/health", "/v1/info" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        using var response = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        Assert.Empty(document.RootElement.GetProperty("security").EnumerateArray());
    }

    [Theory]
    [InlineData(AuthMode.ApiKey, "")]
    [InlineData(AuthMode.ApiKey, "wrong-key-with-enough-length")]
    [InlineData(AuthMode.Anonymous, "")]
    [InlineData(AuthMode.Anonymous, "wrong-key-with-enough-length")]
    public async Task SuppliedInvalidKeyIsRejectedInEitherMode(AuthMode mode, string key)
    {
        using var app = new TestApplication(authMode: mode, javaScriptEnabled: true);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/render") { Content = JavaScriptBody(true) };
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        if (key.Length == 0)
            request.Headers.Add("X-Test-Empty-Key", "1");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Anonymous)]
    public async Task DuplicateKeysAreRejectedInEitherMode(AuthMode mode)
    {
        using var app = new TestApplication(authMode: mode, javaScriptEnabled: true);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/render") { Content = JavaScriptBody(true) };
        request.Headers.TryAddWithoutValidation("X-Api-Key", new[] { TestApplication.Secret, TestApplication.Secret });
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task AnonymousModeAlsoAcceptsAConfiguredKey()
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous);
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Anonymous)]
    public async Task UnpermittedKeyCannotEnableJavaScriptInTheRequest(AuthMode mode)
    {
        using var app = new TestApplication(authMode: mode, preauthenticatedPrincipal: true);
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JavaScriptBody(true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(app.Renderer.LastRequest!.Options!.JavaScript);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey, true)]
    [InlineData(AuthMode.ApiKey, false)]
    [InlineData(AuthMode.Anonymous, true)]
    [InlineData(AuthMode.Anonymous, false)]
    public async Task OptedInKeyHonorsTheRequestedJavaScriptSetting(AuthMode mode, bool enabled)
    {
        using var app = new TestApplication(authMode: mode, javaScriptEnabled: true);
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JavaScriptBody(enabled, enabled ? "window.readyToPrint === true" : null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(enabled, app.Renderer.LastRequest!.Options!.JavaScript);
        Assert.Equal(enabled ? "window.readyToPrint === true" : null, app.Renderer.LastRequest.Options.Readiness!.Function);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Anonymous)]
    public async Task JavaScriptPermissionDoesNotApplyToAnotherKey(AuthMode mode)
    {
        using var app = new TestApplication(authMode: mode, javaScriptEnabled: true);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApplication.StaticSecret);
        using var response = await client.PostAsync("/v1/render", JavaScriptBody(true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(app.Renderer.LastRequest!.Options!.JavaScript);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousCallerCannotBorrowJavaScriptPermissionFromConfiguredKeysOrHostPrincipal(bool hostPrincipal)
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous, javaScriptEnabled: true, preauthenticatedPrincipal: hostPrincipal);
        using var client = app.CreateClient();
        using var response = await client.PostAsync("/v1/render", JavaScriptBody(true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(app.Renderer.LastRequest!.Options!.JavaScript);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey, false)]
    [InlineData(AuthMode.ApiKey, true)]
    [InlineData(AuthMode.Anonymous, false)]
    [InlineData(AuthMode.Anonymous, true)]
    public async Task OmittedOptionsUseOnlyTheAuthenticatedKeysJavaScriptPermission(AuthMode mode, bool enabled)
    {
        using var app = new TestApplication(authMode: mode, javaScriptEnabled: enabled);
        using var client = mode == AuthMode.Anonymous && !enabled ? app.CreateClient() : app.CreateAuthenticatedClient();
        using var body = new StringContent("""{"html":"<p>Static</p>"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/render", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(enabled, app.Renderer.LastRequest!.Options!.JavaScript);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey, false)]
    [InlineData(AuthMode.ApiKey, true)]
    [InlineData(AuthMode.Anonymous, false)]
    [InlineData(AuthMode.Anonymous, true)]
    public async Task ReadinessFunctionCannotBypassDisabledJavaScript(AuthMode mode, bool requested)
    {
        using var app = new TestApplication(authMode: mode);
        using var client = mode == AuthMode.Anonymous ? app.CreateClient() : app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JavaScriptBody(requested, "window.readyToPrint = true"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("options.readiness.function", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Theory]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Anonymous)]
    public async Task HealthRemainsAnonymousWhenAnInvalidKeyIsSupplied(AuthMode mode)
    {
        using var app = new TestApplication(authMode: mode);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key-with-enough-length");
        using var response = await client.GetAsync("/v1/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Theory]
    [InlineData("WindowsEnabled")]
    [InlineData("RequireGroup")]
    [InlineData("AccessGroup")]
    public void LegacyWindowsAuthenticationSettingsAreRejectedByProductionBinding(string setting)
    {
        using var app = new TestApplication();
        using var client = app.CreateClient();
        // Exercise the application's registered binder directly, before Main's startup
        // exception handling can dispose the host and obscure the validation failure.
        app.Services.GetRequiredService<IConfiguration>()["Auth:" + setting] = "true";
        using var scope = app.Services.CreateScope();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<AuthSettings>>().Value);
        Assert.Contains(setting, exception.ToString());
    }

    [Fact]
    public void UnknownKeyPermissionsAreRejectedByProductionBinding()
    {
        using var app = new TestApplication();
        using var client = app.CreateClient();
        app.Services.GetRequiredService<IConfiguration>()["Auth:ApiKeys:integration-test:AllowScripts"] = "true";
        using var scope = app.Services.CreateScope();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<AuthSettings>>().Value);
        Assert.Contains("AllowScripts", exception.ToString());
    }

    [Fact]
    public async Task StaticHtmlReturnsPdfAndMetadataWithoutReadinessFlag()
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("1", response.Headers.GetValues("X-Render-Pages").Single());
        Assert.True(long.Parse(response.Headers.GetValues("X-Render-Duration-Ms").Single()) >= 0);
        Assert.Equal("document.pdf", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition.DispositionType);
        Assert.Null(app.Renderer.LastRequest!.Options!.Readiness!.Function);
        Assert.False(app.Renderer.LastRequest.Options.JavaScript);
        Assert.Equal("Letter", app.Renderer.LastRequest.Options.PageSize!.Name);
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("attachment")]
    public async Task CallerCanSelectDispositionWithSafeUnicodeFileName(string disposition)
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        var json = JsonSerializer.Serialize(new
        {
            html = "<!doctype html><html><body><p>Rapport</p></body></html>",
            fileName = "rapport/été\r\n2026.pdf",
            disposition
        }, JsonDefaults.Options);
        using var response = await client.PostAsync("/v1/render", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        var header = response.Content.Headers.ContentDisposition!;
        Assert.Equal(disposition, header.DispositionType);
        Assert.Equal("rapport_été__2026.pdf", header.FileNameStar);
        Assert.All(header.FileName!, character => Assert.True(character is >= ' ' and <= '~'));
        Assert.DoesNotContain('\r', header.ToString());
        Assert.DoesNotContain('\n', header.ToString());
        Assert.Equal("1", response.Headers.GetValues("X-Render-Pages").Single());
    }

    [Fact]
    public async Task OmittedDispositionDefaultsToAttachment()
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render",
            new StringContent("{\"html\":\"<p>Default download</p>\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("document.pdf", response.Content.Headers.ContentDisposition.FileNameStar);
    }

    [Theory]
    [InlineData("\"download\"")]
    [InlineData("\"INLINE\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task InvalidDispositionIsRejectedBeforeRendering(string jsonDisposition)
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        var json = "{\"html\":\"<p>Invalid disposition</p>\",\"disposition\":" + jsonDisposition + "}";
        using var response = await client.PostAsync("/v1/render", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Theory]
    [InlineData("{\"html\":\"<p>Hello</p>\",\"unexpected\":true}")]
    [InlineData("{\"html\":\"<p>Hello</p>\",\"options\":{\"margins\":{\"top\":\"6in\",\"bottom\":\"6in\"}}}")]
    [InlineData("{\"html\":\"<base href='https://example.org'><p>Hello</p>\"}")]
    [InlineData("{\"html\":\"<p>Hello</p>\",\"options\":{\"javaScript\":false,\"readiness\":{\"function\":\"true\"}}}")]
    public async Task InvalidRequestsNeverReachBrowser(string json)
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task EncodedJsonLimitAlsoAppliesWithoutContentLength()
    {
        using var app = new TestApplication(maxBodyBytes: 256);
        using var client = app.CreateAuthenticatedClient();
        // Escaped JSON bytes substantially exceed the decoded HTML character count.
        var json = "{\"html\":\"" + string.Concat(Enumerable.Repeat("\\u00e9", 50)) + "\"}";
        using var response = await client.PostAsync("/v1/render", new StreamingJsonContent(json), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task UnsupportedCompressionIsRejectedBeforeRenderer()
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        using var body = JsonBody();
        body.Headers.ContentEncoding.Add("gzip");
        using var response = await client.PostAsync("/v1/render", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task SaturationReturns429WithoutChangingHealthAndReleasesAfterCompletion()
    {
        using var app = new TestApplication();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Renderer.Callback = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return FakeRenderer.Output;
        };
        using var client = app.CreateAuthenticatedClient();
        var first = client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var excess = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, excess.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), excess.Headers.RetryAfter!.Delta);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/health", TestContext.Current.CancellationToken)).StatusCode);
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task InfoCountsRequestsWaitingForCoordinatorCapacity()
    {
        using var app = new TestApplication(queueLimit: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Renderer.Callback = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return FakeRenderer.Output;
        };
        using var client = app.CreateAuthenticatedClient();
        var first = client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                using var info = JsonDocument.Parse(await client.GetStringAsync("/v1/info", deadline.Token));
                if (info.RootElement.GetProperty("queued").GetInt32() == 1) break;
                await Task.Delay(10, deadline.Token);
            }
            using var excess = await client.PostAsync("/v1/render", JsonBody(), deadline.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, excess.StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
        using var idle = JsonDocument.Parse(await client.GetStringAsync("/v1/info", TestContext.Current.CancellationToken));
        Assert.Equal(0, idle.RootElement.GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task OverallTimeoutCancelsRendererAndReleasesAdmission()
    {
        using var app = new TestApplication();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Renderer.Callback = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
            return FakeRenderer.Output;
        };
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JsonBody(timeoutMs: 100), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        app.Renderer.Callback = null;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task ShorterParsedDeadlineIncludesTimeAlreadySpentReadingBody()
    {
        using var app = new TestApplication(bodyReadDelayMs: 200);
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JsonBody(timeoutMs: 100), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task AdmissionRemainsHeldUntilPdfWriteCompletes()
    {
        using var app = new TestApplication(blockPdfWrites: true);
        using var client = app.CreateAuthenticatedClient();
        var first = client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        await app.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var excess = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, excess.StatusCode);
        app.WriteRelease.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task PageDiagnosticsAreNotReflectedInFailureResponse()
    {
        using var app = new TestApplication();
        app.Renderer.Callback = (_, _) => throw new RenderDocumentException("secret caller document text");
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/v1/render", JsonBody(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("secret caller document text", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static StringContent JsonBody(int? timeoutMs = null) => new(JsonSerializer.Serialize(new RenderRequest
    {
        Html = "<!doctype html><html><body><p>Hello</p></body></html>",
        Options = new RenderOptions { TimeoutMs = timeoutMs }
    }, JsonDefaults.Options), Encoding.UTF8, "application/json");

    private static StringContent JavaScriptBody(bool enabled, string? readinessFunction = null) => new(JsonSerializer.Serialize(new RenderRequest
    {
        Html = "<p>Document</p><script>window.readyToPrint = true;</script>",
        Options = new() { JavaScript = enabled, Readiness = new() { Function = readinessFunction } }
    }, JsonDefaults.Options), Encoding.UTF8, "application/json");
}

internal sealed class TestApplication(bool preauthenticatedPrincipal = false, long maxBodyBytes = 4096, bool blockPdfWrites = false, int bodyReadDelayMs = 0, AuthMode authMode = AuthMode.ApiKey, bool configureKey = true, bool javaScriptEnabled = false,
    bool jobsEnabled = false, Action<Pressroom.Server.Jobs.JobsSettings>? configureJobs = null, string? pathBase = null, int queueLimit = 0)
    : WebApplicationFactory<Program>
{
    public const string Secret = "a-test-only-secret-with-32-characters";
    public const string StaticSecret = "a-static-test-key-without-javascript-permission";
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pressroom-tests", Guid.NewGuid().ToString("N"));
    public FakeRenderer Renderer { get; } = new();
    public FakePrintService Printing { get; } = new();
    public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WriteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string SettingsPath => Path.Combine(_stateRoot, "pressroom.settings.json");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The entry point loads configuration before ConfigureWebHost callbacks run, so the file
        // must exist and be selected here. Real JSON keys then bind through production options.
        Directory.CreateDirectory(_stateRoot);
        var auth = new Dictionary<string, object>
        {
            ["Mode"] = authMode.ToString(),
            ["ApiKeys"] = configureKey ? new Dictionary<string, ApiKeySettings>
            {
                ["integration-test"] = new() { Secret = Secret, AllowJavaScript = javaScriptEnabled },
                ["static-test"] = new() { Secret = StaticSecret }
            } : new Dictionary<string, ApiKeySettings>()
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Auth = auth }));
        return base.CreateHost(TestHost.SelectConfigurationFile(builder, SettingsPath));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // These tests exercise HTTP behavior, not log sinks. Avoid opening a process-wide
            // Serilog file sink whose disposal can race the entry point's async shutdown.
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(BrowserLifecycleService)).ToArray())
                services.Remove(descriptor);
            services.RemoveAll<IHtmlRenderer>();
            services.AddSingleton<IHtmlRenderer>(Renderer);
            services.RemoveAll<IPrintService>();
            services.AddSingleton<IPrintService>(Printing);
            services.PostConfigure<PathsSettings>(options => options.StateRoot = _stateRoot);
            services.PostConfigure<RenderSettings>(options =>
            {
                options.Concurrency = jobsEnabled ? 2 : 1;
                options.QueueLimit = queueLimit;
                options.MaxBodyBytes = maxBodyBytes;
                options.DefaultTimeoutMs = 5000;
                options.MaxTimeoutMs = 10000;
            });
            services.PostConfigure<Pressroom.Server.Jobs.JobsSettings>(options =>
            {
                options.Enabled = jobsEnabled;
                configureJobs?.Invoke(options);
            });
            services.AddSingleton<IStartupFilter>(new TestStartupFilter(this, preauthenticatedPrincipal, blockPdfWrites, bodyReadDelayMs, pathBase));
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Secret);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        WriteRelease.TrySetResult();
        base.Dispose(disposing);
        if (disposing)
        {
            // Program's await-using disposal can finish just after TestServer disposal.
            // Wait briefly for the durable store's exclusive instance lock to close.
            for (var attempt = 0; Directory.Exists(_stateRoot); attempt++)
            {
                try { Directory.Delete(_stateRoot, recursive: true); }
                catch (IOException) when (attempt < 40) { Thread.Sleep(25); }
            }
        }
    }

    private sealed class TestStartupFilter(TestApplication app, bool principal, bool blockWrites, int bodyDelayMs, string? pathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, following) =>
            {
                if (pathBase is not null) context.Request.PathBase = pathBase;
                if (principal)
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.Name, "TEST\\member"),
                        new Claim(AuthenticationNames.JavaScriptClaim, "true")
                    ], "Windows"));
                // TestServer's HttpClient adapter drops zero-length request header values.
                // Recreate the header dictionary seen by the real server for this edge case.
                if (context.Request.Headers.ContainsKey("X-Test-Empty-Key"))
                    context.Request.Headers[AuthenticationNames.ApiKeyHeader] = "";
                if (blockWrites && context.Request.Path == "/v1/render")
                    context.Response.Body = new BlockingWriteStream(context.Response.Body, app.WriteEntered, app.WriteRelease);
                if (bodyDelayMs != 0)
                    context.Request.Body = new DelayedReadStream(context.Request.Body, bodyDelayMs);
                await following(context);
            });
            next(builder);
        };
    }
}

internal static class TestHost
{
    /// <summary>
    /// Selects the settings file the way an operator's --config does. WebApplicationFactory hands
    /// host configuration to Program.Main as --key=value arguments before the entry point builds its
    /// configuration, so ApplicationConfiguration never opens the machine-wide default file. That file
    /// may hold production secrets, or be unreadable by the test user (root:pressroom 0640).
    /// </summary>
    public static IHostBuilder SelectConfigurationFile(IHostBuilder builder, string path) =>
        builder.ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["config"] = path }));
}

internal sealed class FakeRenderer : IHtmlRenderer
{
    public static RenderOutput Output => new(Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"), 1, 0);
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public RenderRequest? LastRequest { get; private set; }
    public Func<RenderRequest, CancellationToken, Task<RenderOutput>>? Callback { get; set; }
    public Task<RenderOutput> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        LastRequest = request;
        return Callback?.Invoke(request, cancellationToken) ?? Task.FromResult(Output);
    }
    public RendererSnapshot GetSnapshot() => new() { IsReady = true, BrowserVersion = "test", PlaywrightVersion = "test" };
}

internal sealed class StreamingJsonContent : HttpContent
{
    private readonly string _json;
    private readonly int _delayMs;
    public StreamingJsonContent(string json, int delayMs = 0)
    {
        _json = json;
        _delayMs = delayMs;
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var bytes = Encoding.UTF8.GetBytes(_json);
        // Send a prefix before delaying: the request and its admission clock have started.
        await stream.WriteAsync(bytes.AsMemory(0, 1));
        if (_delayMs != 0)
            await Task.Delay(_delayMs);
        await stream.WriteAsync(bytes.AsMemory(1));
    }
}

internal sealed class BlockingWriteStream(Stream inner, TaskCompletionSource entered, TaskCompletionSource release) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Only successful PDF responses are blocked; 429 problem responses must remain observable.
        if (buffer.Span.StartsWith("%PDF"u8))
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
        await inner.WriteAsync(buffer, cancellationToken);
    }
}

internal sealed class DelayedReadStream(Stream inner, int delayMs) : Stream
{
    private bool _delayed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_delayed)
        {
            _delayed = true;
            await Task.Delay(delayMs, cancellationToken);
        }
        return await inner.ReadAsync(buffer, cancellationToken);
    }
}
