using System.Net;
using System.Net.Http.Json;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Infrastructure;
using Pressroom.Server.Printing;
using Xunit;
using System.Runtime.CompilerServices;

namespace Pressroom.Server.Tests;

public sealed class LinuxTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux deployment contract.")]
    public async Task LinuxRejectsPrintingAndReturnsAnEmptyPrinterList()
    {
        using var app = new TestApplication();
        using var client = app.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/v1/render", new RenderRequest { Html = "hello", Printer = "office" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Windows only", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"printers\":[]}", await client.GetStringAsync("/v1/printers", TestContext.Current.CancellationToken));
        Assert.Equal(0, app.Renderer.Calls);
        new PrintingSettings().Validate();
        Assert.Throws<InvalidOperationException>(() => new PrintingSettings { SumatraPdfPath = "/tmp/printer" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PrintingSettings { Printers = new() { ["office"] = "Office" } }.Validate());
        Assert.Equal("/var/lib/pressroom", new PathsSettings().StateRoot);
    }

    [Fact]
    public void ServerConfigurationRejectsAnUnsandboxedBrowser()
        => Assert.Throws<InvalidOperationException>(() => ServerConfiguration.ValidateRenderSettings(new RenderSettings { ChromiumSandbox = false }));
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber) { if (!OperatingSystem.IsWindows()) Skip = "Windows printing contract."; }
}
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber) { if (!OperatingSystem.IsWindows()) Skip = "Windows printing contract."; }
}
