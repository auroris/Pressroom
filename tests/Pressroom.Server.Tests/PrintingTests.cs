using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Printing;
using Xunit;

namespace Pressroom.Server.Tests;

public sealed class PrintingTests
{
    [Theory]
    [InlineData(AuthMode.ApiKey)]
    [InlineData(AuthMode.Anonymous)]
    public async Task ValidKeyPrintsAndReturnsReceiptInsteadOfPdf(AuthMode mode)
    {
        using var app = new TestApplication(authMode: mode);
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        var receipt = await response.Content.ReadFromJsonAsync<PrintJobResult>(TestContext.Current.CancellationToken);
        Assert.Equal(new PrintJobResult("test-job", "office", "submitted", 1), receipt);
        Assert.Equal(1, app.Printing.Calls);
        Assert.Equal(FakeRenderer.Output.Pdf, app.Printing.LastDocument!.Pdf);
        Assert.False(app.Renderer.LastRequest!.Options!.JavaScript);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousAndUnrelatedHostPrincipalsCannotPrintOrListPrinters(bool hostPrincipal)
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous, preauthenticatedPrincipal: hostPrincipal);
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var printers = await client.GetAsync("/v1/printers", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, printers.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
        Assert.Equal(0, app.Printing.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-secret")]
    [InlineData("duplicate")]
    public async Task InvalidKeysCannotPrintInAnonymousMode(string key)
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous);
        using var client = app.CreateClient();
        if (key == "duplicate") client.DefaultRequestHeaders.Add("X-Api-Key", new[] { TestApplication.Secret, TestApplication.Secret });
        else if (key.Length == 0) client.DefaultRequestHeaders.Add("X-Test-Empty-Key", "1");
        else client.DefaultRequestHeaders.Add("X-Api-Key", key);
        using var response = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, app.Printing.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../office")]
    [InlineData("office\n")]
    [InlineData("Microsoft Print to PDF")]
    public async Task InvalidOrUnconfiguredPrinterFailsBeforeRendering(string printer)
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/v1/render", Request(printer), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("printer", out _));
        Assert.Equal(0, app.Renderer.Calls);
        Assert.Equal(0, app.Printing.Calls);
    }

    [Fact]
    public async Task PrinterDiscoveryRequiresKeyAndReturnsOnlyConfiguredIdentifiers()
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous);
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/v1/printers", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"printers\":[\"office\"]}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnonymousOpenApiRetainsExplicitPrinterAuthenticationAndDocumentsReceipt()
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous);
        using var client = app.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Empty(root.GetProperty("security").EnumerateArray());
        var security = root.GetProperty("paths").GetProperty("/v1/printers").GetProperty("get").GetProperty("security");
        Assert.True(Assert.Single(security.EnumerateArray()).TryGetProperty("ApiKey", out _));
        Assert.True(root.GetProperty("paths").GetProperty("/v1/render").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(root.GetProperty("components").GetProperty("schemas").GetProperty("RenderRequest").GetProperty("properties").TryGetProperty("printer", out _));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.BadGateway)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task PrintFailuresReturnProblemDetails(bool unavailable, HttpStatusCode status)
    {
        using var app = new TestApplication();
        app.Printing.Callback = (_, _, _) => throw (unavailable ? new PrintUnavailableException() : new PrintSubmissionException());
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task PrintUsesDeadlineAndKeepsAdmissionSlotUntilItStops()
    {
        using var app = new TestApplication();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Printing.Callback = async (_, _, cancellation) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException();
        };
        using var client = app.CreateAuthenticatedClient();
        var first = client.PostAsJsonAsync("/v1/render", Request() with { Options = new() { TimeoutMs = 1000 } }, JsonDefaults.Options, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var second = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        using var timedOut = await first;
        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Contains("Windows queue", await timedOut.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        app.Printing.Callback = null;
        using var next = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
    }

    [Fact]
    public async Task RenderFailureDoesNotStartPrintingAndPdfRequestsDoNotPrint()
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        app.Renderer.Callback = (_, _) => throw new RenderDocumentException("private document error");
        using var failed = await client.PostAsJsonAsync("/v1/render", Request(), JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal(0, app.Printing.Calls);
        app.Renderer.Callback = null;
        using var pdf = await client.PostAsJsonAsync("/v1/render", Request() with { Printer = null }, JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, app.Printing.Calls);
    }

    private static RenderRequest Request(string printer = "office") => new() { Html = "<p>Print test</p>", Printer = printer };
}

internal sealed class FakePrintService : IPrintService
{
    public IReadOnlyCollection<string> Printers { get; } = new[] { "office" };
    public int Calls { get; private set; }
    public RenderOutput? LastDocument { get; private set; }
    public Func<string, RenderOutput, CancellationToken, Task<PrintJobResult>>? Callback { get; set; }
    public Task<PrintJobResult> PrintAsync(string printer, RenderOutput document, CancellationToken cancellationToken)
    {
        Calls++;
        LastDocument = document;
        return Callback?.Invoke(printer, document, cancellationToken)
            ?? Task.FromResult(new PrintJobResult("test-job", printer, "submitted", document.PageCount));
    }
}

public sealed class PrintServiceTests : IDisposable
{
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "pressroom-print-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HelperGetsExactConfiguredPrinterAndPrivatePdfThenCleansUp(int exitCode)
    {
        var runner = new StubRunner(async (start, cancellation) =>
        {
            Assert.False(start.UseShellExecute);
            Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
            Assert.Equal(_stateRoot, start.WorkingDirectory);
            Assert.Equal(new[] { "-appdata", Path.Combine(Path.GetDirectoryName(start.ArgumentList[5])!, "profile"), "-silent", "-print-to", "Office \"Printer\" -argument" }, start.ArgumentList.Take(5));
            Assert.Equal(FakeRenderer.Output.Pdf, await File.ReadAllBytesAsync(start.ArgumentList[5], cancellation));
            return exitCode;
        });
        var service = Create(runner);
        if (exitCode == 0)
        {
            var result = await service.PrintAsync("OFFICE", FakeRenderer.Output, TestContext.Current.CancellationToken);
            Assert.Equal("submitted", result.Status);
            Assert.Equal("office", result.Printer);
            Assert.True(Guid.TryParseExact(result.JobId, "N", out _));
        }
        else await Assert.ThrowsAsync<PrintSubmissionException>(() => service.PrintAsync("office", FakeRenderer.Output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_stateRoot, "temp")));
    }

    [Fact]
    public async Task CancellationCleansUpAndUnknownPrintersNeverLaunchHelper()
    {
        var service = Create(new StubRunner((_, _) => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrintAsync("other", FakeRenderer.Output, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(_stateRoot));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrintAsync("office", FakeRenderer.Output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_stateRoot, "temp")));
    }

    [Fact]
    public async Task MissingPrintHelperIsReportedAsUnavailable()
    {
        await Assert.ThrowsAsync<PrintUnavailableException>(() => new PrintProcessRunner().RunAsync(
            new ProcessStartInfo(Path.Combine(_stateRoot, "missing.exe")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupPreservesUnexpectedVirtualPrinterOutput()
    {
        string? savedOutput = null;
        var service = Create(new StubRunner(async (start, cancellation) =>
        {
            savedOutput = Path.Combine(Path.GetDirectoryName(start.ArgumentList[5])!, "test.pdf");
            await File.WriteAllTextAsync(savedOutput, "saved output", cancellation);
            return 0;
        }));
        var result = await service.PrintAsync("office", FakeRenderer.Output, TestContext.Current.CancellationToken);
        Assert.Equal("submitted", result.Status);
        Assert.Equal("saved output", await File.ReadAllTextAsync(savedOutput!, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(savedOutput!)!));
    }

    [Fact]
    public async Task RunnerKillsTimedOutProcessBeforeReturning()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_stateRoot);
        var pidFile = Path.Combine(_stateRoot, "helper.pid");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[IO.File]::WriteAllText($env:PRESSROOM_TEST_PID_FILE, [string]$PID); Start-Sleep -Seconds 60");
        start.Environment["PRESSROOM_TEST_PID_FILE"] = pidFile;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = new PrintProcessRunner().RunAsync(start, timeout.Token);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!File.Exists(pidFile)) await Task.Delay(25, startup.Token);
        }
        finally { timeout.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public void PrinterConfigurationRejectsInvalidMappingsAndMissingExecutableSetting()
    {
        new PrintingSettings().Validate();
        foreach (var id in new[] { "", "bad id", "a/b", new string('x', 129) })
            Assert.Throws<InvalidOperationException>(() => new PrintingSettings { Printers = new() { [id] = "Printer" } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PrintingSettings { Printers = null! }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PrintingSettings { Printers = new() { ["office"] = "Printer" } }.Validate());
        foreach (var name in new[] { "", " ", "Printer\n", new string('x', 257) })
            Assert.Throws<InvalidOperationException>(() => new PrintingSettings { Printers = new() { ["office"] = name }, SumatraPdfPath = Path.Combine(_stateRoot, "SumatraPDF.exe") }.Validate());
    }

    private PrintService Create(IPrintProcessRunner runner) => new(
        Options.Create(new PrintingSettings { SumatraPdfPath = Path.Combine(_stateRoot, "SumatraPDF.exe"), Printers = new() { ["office"] = "Office \"Printer\" -argument" } }),
        Options.Create(new PathsSettings { StateRoot = _stateRoot }), runner, NullLogger<PrintService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_stateRoot)) Directory.Delete(_stateRoot, recursive: true);
    }

    private sealed class StubRunner(Func<ProcessStartInfo, CancellationToken, Task<int>> run) : IPrintProcessRunner
    {
        public Task<int> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken) => run(start, cancellationToken);
    }
}
