using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pressroom.Rendering;
using Pressroom.Server.Printing;
using Xunit;

namespace Pressroom.Server.Tests;

public sealed class CupsPrintingTests : IDisposable
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pressroom cups tests", Guid.NewGuid().ToString("N"));

    [Theory(SkipUnless = nameof(IsLinux), Skip = "Linux CUPS process contract.")]
    [InlineData("lp", 0)]
    [InlineData("lp", 1)]
    [InlineData("lpr", 0)]
    [InlineData("lpr", 1)]
    public async Task SubmitsPrivatePdfWithLiteralArgumentsAndCleansUp(string command, int exitCode)
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, command);
        var arguments = Path.Combine(_root, "arguments");
        var capturedPdf = Path.Combine(_root, "captured.pdf");
        // A real child process records the argv it receives, without a printer dependency.
        await File.WriteAllTextAsync(executable,
            "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$PRESSROOM_TEST_ARGUMENTS\"\nfor file do :; done\ncat \"$file\" > \"$PRESSROOM_TEST_PDF\"\nexit \"$PRESSROOM_TEST_EXIT\"\n",
            TestContext.Current.CancellationToken);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new CaptureRunner(arguments, capturedPdf, exitCode);
        const string queue = "Office\";$(whoami)-option";
        var service = Create(executable, queue, runner);
        if (exitCode == 0)
        {
            var result = await service.PrintAsync("OFFICE", FakeRenderer.Output, TestContext.Current.CancellationToken);
            Assert.Equal("office", result.Printer);
            Assert.Equal("submitted", result.Status);
        }
        else
            await Assert.ThrowsAsync<PrintSubmissionException>(() => service.PrintAsync("office", FakeRenderer.Output, TestContext.Current.CancellationToken));

        var argv = await File.ReadAllLinesAsync(arguments, TestContext.Current.CancellationToken);
        Assert.Equal(command == "lp" ? new[] { "-d", queue, "--" } : new[] { "-P", queue }, argv[..^1]);
        Assert.True(Path.IsPathFullyQualified(argv[^1]));
        Assert.False(File.Exists(argv[^1]));
        Assert.Equal(FakeRenderer.Output.Pdf, await File.ReadAllBytesAsync(capturedPdf, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")));
    }

    [Theory(SkipUnless = nameof(IsLinux), Skip = "Linux CUPS configuration contract.")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lp")]
    [InlineData("/usr/bin/lp -d office")]
    [InlineData("/usr/bin/sh")]
    [InlineData("/usr/bin/LP")]
    [InlineData("/usr/bin/lp\n")]
    public void RejectsInvalidCommandPaths(string? command)
        => Assert.Throws<InvalidOperationException>(() => new PrintingSettings
        {
            CupsCommandPath = command!, Printers = new() { ["office"] = "Office" }
        }.Validate());

    [Theory(SkipUnless = nameof(IsLinux), Skip = "Linux CUPS process contract.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNonExecutableHelperIsUnavailableAndCleansUp(bool exists)
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "lp");
        if (exists)
        {
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n", TestContext.Current.CancellationToken);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var service = Create(executable, "Office", new PrintProcessRunner());
        await Assert.ThrowsAsync<PrintUnavailableException>(() => service.PrintAsync("office", FakeRenderer.Output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")));
    }

    private PrintService Create(string executable, string queue, IPrintProcessRunner runner) => new(
        Options.Create(new PrintingSettings { CupsCommandPath = executable, Printers = new() { ["office"] = queue } }),
        Options.Create(new PathsSettings { StateRoot = _root }), runner, NullLogger<PrintService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CaptureRunner(string arguments, string pdf, int exitCode) : IPrintProcessRunner
    {
        public Task<int> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
        {
            Assert.False(start.UseShellExecute);
            start.Environment["PRESSROOM_TEST_ARGUMENTS"] = arguments;
            start.Environment["PRESSROOM_TEST_PDF"] = pdf;
            start.Environment["PRESSROOM_TEST_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new PrintProcessRunner().RunAsync(start, cancellationToken);
        }
    }
}
