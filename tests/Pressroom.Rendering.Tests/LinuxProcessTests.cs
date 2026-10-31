using System.Diagnostics;
using Pressroom.Rendering;
using Xunit;

namespace Pressroom.Rendering.Tests;

public sealed class LinuxProcessTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux process ownership test.")]
    public async Task FailedShutdownKillsGrandchildrenAndPreservesUnrelatedProcesses()
    {
        using var unrelated = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "60" }, UseShellExecute = false })!;
        using var parent = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sh -c 'trap \"\" TERM; sleep 60 & wait' & echo $!; wait" },
            UseShellExecute = false, RedirectStandardOutput = true
        })!;
        try
        {
            var child = int.Parse((await parent.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))!);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var tree = new LinuxProcessTree(parent.Id);
            tree.CaptureDescendants();
            Assert.True(await tree.TerminateAsync(TimeSpan.FromSeconds(3)));
            Assert.False(unrelated.HasExited);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
            if (File.Exists($"/proc/{child}/stat"))
            {
                var stat = await File.ReadAllTextAsync($"/proc/{child}/stat", TestContext.Current.CancellationToken);
                Assert.Equal('Z', stat[stat.LastIndexOf(')') + 2]);
            }
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(true);
            if (!unrelated.HasExited) unrelated.Kill();
            await unrelated.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }
}
