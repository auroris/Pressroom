using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Pressroom.Contracts;
using Pressroom.Rendering;
using UglyToad.PdfPig;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Pressroom.Rendering.Tests;

public sealed class RendererUnitTests
{
    [Theory]
    [InlineData("<!doctype html><html><head><BASE href='https://example.com'></head></html>", true)]
    [InlineData("<!-- <base href='https://example.com'> --><p>Static</p>", false)]
    [InlineData("<script>const x = '<base href=\"https://example.com\">';</script>", false)]
    [InlineData("<head><base\nhref='https://example.com'></head>", true)]
    public void BaseValidationParsesHtmlInsteadOfMatchingText(string html, bool expected)
        => Assert.Equal(expected, DocumentValidator.HasBaseElement(html));

    [Fact]
    public void PdfOptionsKeepDefaultsAndCustomPaperDimensions()
    {
        var defaults = BrowserRenderer.MapPdfOptions(new RenderOptions());
        Assert.Equal("Letter", defaults.Format);
        Assert.True(defaults.Tagged);
        Assert.True(defaults.Outline);
        Assert.Null(defaults.Path);
        var custom = BrowserRenderer.MapPdfOptions(new RenderOptions
        {
            PageSize = PageSize.Custom("4in", "6in"),
            PreferCssPageSize = true,
            Margins = new() { Top = "5mm", Bottom = "5mm", Left = "5mm", Right = "5mm" }
        });
        Assert.Null(custom.Format);
        Assert.Equal("4in", custom.Width);
        Assert.Equal("6in", custom.Height);
        Assert.True(custom.PreferCSSPageSize);
        Assert.Equal("5mm", custom.Margin?.Top);
    }

    [Fact]
    public void InvalidSettingsFailBeforeBrowserStartup()
    {
        Assert.Throws<InvalidOperationException>(() => new RenderSettings { Concurrency = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new RenderSettings { DefaultTimeoutMs = 60_000, MaxTimeoutMs = 30_000 }.Validate());
    }
}

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!BrowserFixture.Enabled)
            Skip = "Set PRESSROOM_RUN_BROWSER_TESTS=1 after installing the matching browser to run real Chromium tests.";
    }
}

public sealed class BrowserFixture : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("PRESSROOM_RUN_BROWSER_TESTS") == "1";
    public BrowserRenderer Renderer { get; private set; } = null!;
    private string _stateRoot = "";
    private readonly Dictionary<string, string?> _originalEnvironment = [];

    public async ValueTask InitializeAsync()
    {
        if (!Enabled) return;
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR", "PLAYWRIGHT_BROWSERS_PATH", "PLAYWRIGHT_NODEJS_PATH" })
            _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
        _stateRoot = Path.Combine(Path.GetTempPath(), "pressroom-integration", Guid.NewGuid().ToString("N"));
        Renderer = new(
            Options.Create(new RenderSettings
            {
                BrowsersPath = Environment.GetEnvironmentVariable("PRESSROOM_TEST_BROWSERS_PATH") ?? "browsers",
                ChromiumPath = Environment.GetEnvironmentVariable("PRESSROOM_TEST_CHROMIUM_PATH"),
                ChromiumSandbox = Environment.GetEnvironmentVariable("PRESSROOM_TEST_SANDBOX") != "false",
                RestartAfterRenders = 5,
                Concurrency = 2
            }),
            Options.Create(new PathsSettings { StateRoot = _stateRoot }),
            Options.Create(new FontsSettings()),
            NullLogger<BrowserRenderer>.Instance);
        await Renderer.StartAsync(CancellationToken.None);
    }

    public async Task ReadyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!Renderer.GetSnapshot().IsReady) await Task.Delay(25, timeout.Token);
    }

    public async Task<RenderOutput> RenderAsync(string html, RenderOptions? options = null, CancellationToken cancellation = default)
    {
        await ReadyAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return await Renderer.RenderAsync(new() { Html = html, Options = options ?? new() }, deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (!Enabled) return;
        await Renderer.StopAsync(CancellationToken.None);
        Renderer.Dispose();
        foreach (var pair in _originalEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (Directory.Exists(_stateRoot)) Directory.Delete(_stateRoot, true);
    }
}

public sealed class BrowserIntegrationTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    [BrowserFact]
    public async Task StaticBilingualDocumentHasEmbeddedFontTwoLetterPagesTagsOutlineAndPageNumbers()
    {
        var result = await fixture.RenderAsync("""
            <!doctype html><html lang="fr"><head><meta charset="utf-8"><title>Ordres / Orders</title>
            <style>body { font-family:'Noto Sans', sans-serif; } .page { break-before:page; }</style></head>
            <body><h1>Orders / Ordres</h1><p>École. Québec. Français. Œuvre.</p>
            <img width="20" height="20" src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='20' height='20'%3E%3Crect width='20' height='20' fill='red'/%3E%3C/svg%3E">
            <section class="page"><h2>Deuxième page</h2><p>Prêt à imprimer.</p></section></body></html>
            """);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(0, result.BlockedResources);
        using var pdf = PdfDocument.Open(result.Pdf);
        Assert.Equal(612, pdf.GetPage(1).Width, precision: 0);
        Assert.Equal(792, pdf.GetPage(1).Height, precision: 0);
        var text = string.Join(" ", pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Québec", text);
        Assert.Contains("Page 1 of 2", text);
        Assert.Contains("Page 2 of 2", text);
        Assert.Contains(pdf.GetPage(1).Letters, letter => letter.FontName?.Contains("NotoSans", StringComparison.Ordinal) == true);
        var syntax = Encoding.Latin1.GetString(result.Pdf);
        Assert.Contains("/StructTreeRoot", syntax);
        Assert.Contains("/Outlines", syntax);
    }

    [BrowserFact]
    public async Task PrintAndScreenMediaSelectDifferentContentAndCustomPaperWorks()
    {
        const string html = "<style>.screen{display:none}@media screen{.screen{display:block}.print{display:none}}</style><p class='print'>PRINT_MARKER</p><p class='screen'>SCREEN_MARKER</p>";
        var options = new RenderOptions
        {
            PageSize = PageSize.Custom("4in", "6in"), DisplayHeaderFooter = false,
            Margins = new() { Top = "5mm", Bottom = "5mm", Left = "5mm", Right = "5mm" }
        };
        using var print = PdfDocument.Open((await fixture.RenderAsync(html, options)).Pdf);
        using var screen = PdfDocument.Open((await fixture.RenderAsync(html, options with { Media = "screen" })).Pdf);
        Assert.Contains("PRINT_MARKER", print.GetPage(1).Text);
        Assert.DoesNotContain("SCREEN_MARKER", print.GetPage(1).Text);
        Assert.Contains("SCREEN_MARKER", screen.GetPage(1).Text);
        Assert.Equal(288, screen.GetPage(1).Width, precision: 0);
        Assert.Equal(432, screen.GetPage(1).Height, precision: 0);
    }


    [BrowserFact]
    public async Task ReadinessFailureIsBoundedAndNextRenderSucceeds()
    {
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RenderDocumentException>(() => fixture.RenderAsync("<p>Never ready</p>", new()
        {
            Readiness = new() { Function = "false", TimeoutMs = 150 }
        }));
        Assert.Contains("readiness", error.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Renderer.GetSnapshot().Active);
        Assert.Equal(1, (await fixture.RenderAsync("<p>Recovered</p>")).PageCount);
    }

    [BrowserFact]
    public async Task CancellationClosesContextBeforeReusingCapacity()
    {
        await fixture.ReadyAsync();
        using var cancellation = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RenderAsync("<p>Waiting</p>", new()
        {
            Readiness = new() { Function = "false", TimeoutMs = 10_000 }
        }, cancellation.Token));
        Assert.Equal(0, fixture.Renderer.GetSnapshot().Active);
        var result = await fixture.RenderAsync("<p>Capacity released</p>");
        Assert.Equal(1, result.PageCount);
    }

    [BrowserFact]
    public async Task NetworkLocalFileWebSocketFramePopupAndCssRequestsCannotReachListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await fixture.RenderAsync($$"""
            <html><head><style>@import url('http://127.0.0.1:{{port}}/css');</style></head><body>
            <img src="http://127.0.0.1:{{port}}/image"><iframe src="http://127.0.0.1:{{port}}/frame"></iframe>
            <img src="file:///etc/passwd"><p>Network isolation</p><script>
            fetch('http://127.0.0.1:{{port}}/fetch').catch(() => {});
            window.open('http://127.0.0.1:{{port}}/popup');
            const ws = new WebSocket('ws://127.0.0.1:{{port}}/socket'); ws.onerror = () => {};
            if (navigator.serviceWorker) navigator.serviceWorker.register('http://127.0.0.1:{{port}}/worker.js').catch(() => {});
            </script></body></html>
            """, new() { Readiness = new() { DelayMs = 300 } });
        Assert.True(result.BlockedResources >= 5);
        Assert.False(listener.Pending());
        Assert.Equal(1, result.PageCount);
    }

    [BrowserFact]
    public async Task ScriptErrorsReturnOnlyContentFreeDiagnostics()
    {
        var error = await Assert.ThrowsAsync<RenderDocumentException>(() => fixture.RenderAsync("<script>throw new Error('SECRET_DOCUMENT_VALUE')</script>"));
        Assert.DoesNotContain("SECRET_DOCUMENT_VALUE", error.ToString());
        Assert.Contains("JavaScript", error.Message);
    }

    [BrowserFact]
    public async Task PlannedRecycleDrainsConcurrentRendersAndReturnsReady()
    {
        await fixture.ReadyAsync();
        var previousRestart = fixture.Renderer.GetSnapshot().LastRestartUtc;
        for (var batch = 0; batch < 3; batch++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => fixture.RenderAsync($"<h1>Concurrent {batch}-{i}</h1>",
                new() { Readiness = new() { DelayMs = 100 } })));
            Assert.All(results, result => Assert.Equal(1, result.PageCount));
            await fixture.ReadyAsync();
        }
        var snapshot = fixture.Renderer.GetSnapshot();
        Assert.True(snapshot.LastRestartUtc > previousRestart);
        Assert.Equal(0, snapshot.Active);
        Assert.Equal(0, snapshot.Queued);
    }

    [BrowserFact]
    public async Task BrowserCrashFailsActiveRenderAndSupervisorWarmsReplacement()
    {
        await fixture.ReadyAsync();
        var previousRestart = fixture.Renderer.GetSnapshot().LastRestartUtc;
        var rendering = fixture.RenderAsync("<p>Active during browser crash</p>", new()
        {
            Readiness = new() { Function = "false", TimeoutMs = 10_000 }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (fixture.Renderer.GetSnapshot().Active == 0) await Task.Delay(10, deadline.Token);

        // Fault injection only: obtain this fixture's owned browser and crash it through
        // CDP. No production testing endpoint and no machine-wide process-name kill.
        var generation = typeof(BrowserRenderer).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Renderer)!;
        var browser = (IBrowser)generation.GetType().GetProperty("Browser")!.GetValue(generation)!;
        var cdp = await browser.NewBrowserCDPSessionAsync();
        await Assert.ThrowsAnyAsync<PlaywrightException>(() => cdp.SendAsync("Browser.crash"));
        await Assert.ThrowsAsync<RenderUnavailableException>(() => rendering);
        await fixture.ReadyAsync();
        Assert.True(fixture.Renderer.GetSnapshot().LastRestartUtc > previousRestart);
        Assert.Equal(1, (await fixture.RenderAsync("<p>Browser recovered</p>")).PageCount);
    }
}
