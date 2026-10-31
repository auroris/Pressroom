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
    public void LinuxAcceptsCupsPrintingConfiguration()
    {
        new PrintingSettings().Validate();
        new PrintingSettings { Printers = new() { ["office"] = "Office" } }.Validate();
        new PrintingSettings { CupsCommandPath = "/usr/bin/lpr", Printers = new() { ["office"] = "Office" } }.Validate();
        Assert.Throws<InvalidOperationException>(() => new PrintingSettings
        {
            CupsCommandPath = "lp", Printers = new() { ["office"] = "Office" }
        }.Validate());
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
