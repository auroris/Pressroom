using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Pressroom.Client;
using Pressroom.Contracts;
using Xunit;

namespace Pressroom.Server.Tests;

public sealed class JobsBrowserTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("PRESSROOM_RUN_BROWSER_TESTS") == "1";

    [Fact(SkipUnless = nameof(Enabled), Skip = "Enable real Chromium tests with PRESSROOM_RUN_BROWSER_TESTS=1.")]
    public async Task BatchClientLeavesSynchronousRenderingAvailable()
    {
        var state = Path.Combine(Path.GetTempPath(), "pressroom-job-browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var previous = new[] { "TEMP", "TMP", "TMPDIR", "PLAYWRIGHT_BROWSERS_PATH", "PLAYWRIGHT_NODEJS_PATH" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var config = Path.Combine(state, "settings.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            Paths = new { StateRoot = state },
            Auth = new { Mode = "ApiKey", ApiKeys = new Dictionary<string, object> { ["batch"] = new { Secret = TestApplication.Secret } } },
            Jobs = new { Enabled = true },
            Render = new
            {
                BrowsersPath = Environment.GetEnvironmentVariable("PRESSROOM_TEST_BROWSERS_PATH") ?? "browsers",
                ChromiumPath = Environment.GetEnvironmentVariable("PRESSROOM_TEST_CHROMIUM_PATH"),
                ChromiumSandbox = true, RestartAfterRenders = 500
            }
        }), TestContext.Current.CancellationToken);
        try
        {
            await using var app = new BrowserApplication(config);
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Api-Key", TestApplication.Secret);
            var client = new PressroomClient(http);
            var batchSize = Environment.GetEnvironmentVariable("PRESSROOM_JOB_ACCEPTANCE_COUNT") == "500" ? 500 : 50;
            var idle = new List<double>();
            for (var sample = 0; sample < 10; sample++)
            {
                var clock = Stopwatch.StartNew();
                await client.RenderAsync(Request("interactive"), TestContext.Current.CancellationToken);
                idle.Add(clock.Elapsed.TotalMilliseconds);
            }
            var busy = new List<double>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var syncStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var syncCount = 0;
            var recycleResponses = 0;
            var sync = Task.Run(async () =>
            {
                try
                {
                    while (!syncStop.IsCancellationRequested)
                    {
                        try
                        {
                            var clock = Stopwatch.StartNew();
                            var pdf = await client.RenderAsync(Request("interactive"), syncStop.Token);
                            busy.Add(clock.Elapsed.TotalMilliseconds);
                            Assert.Equal(1, pdf.PageCount);
                            Interlocked.Increment(ref syncCount);
                            await Task.Delay(200, syncStop.Token);
                        }
                        catch (PressroomException exception) when (exception.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                        {
                            // Preserve the sync API's existing 503 contract during planned recycling.
                            // Only this load generator retries; the public client never does so silently.
                            Interlocked.Increment(ref recycleResponses);
                            await Task.Delay(exception.RetryAfter ?? TimeSpan.FromSeconds(1), syncStop.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (syncStop.IsCancellationRequested) { }
            }, CancellationToken.None);
            try
            {
                var count = 0;
                await foreach (var result in client.RenderManyAsync(Enumerable.Range(0, batchSize).Select(index => Request($"batch {index}")), 8, timeout.Token))
                {
                    Assert.Null(result.Error);
                    Assert.Equal(1, result.Pdf!.PageCount);
                    Assert.True(result.Pdf.Bytes.AsSpan().StartsWith("%PDF"u8));
                    count++;
                }
                Assert.Equal(batchSize, count);
                Assert.True(syncCount > 0);
            }
            finally { syncStop.Cancel(); await sync; }
            static double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
            var metrics = new { batchSize, syncCount, recycleResponses, idleP95Ms = P95(idle), busyP95Ms = P95(busy), ratio = P95(busy) / P95(idle) };
            if (Environment.GetEnvironmentVariable("PRESSROOM_JOB_METRICS_PATH") is { Length: > 0 } metricsPath)
                await File.WriteAllTextAsync(metricsPath, JsonSerializer.Serialize(metrics), TestContext.Current.CancellationToken);
            if (batchSize == 500) Assert.True(metrics.ratio <= 2, $"Sync p95 ratio exceeded 2: {JsonSerializer.Serialize(metrics)}");
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(state, true);
        }
    }

    private static RenderRequest Request(string title) => new()
    {
        Html = $"<!doctype html><html><body><h1>{title}</h1><p>Ready — prêt. Éé Àà Çç Œœ.</p></body></html>",
        Options = new() { DisplayHeaderFooter = false, TimeoutMs = 10_000 }
    };
    private sealed class BrowserApplication(string config) : WebApplicationFactory<Program>
    {
        // Select the file before the entry point builds configuration; see TestHost.
        protected override IHost CreateHost(IHostBuilder builder) => base.CreateHost(TestHost.SelectConfigurationFile(builder, config));
    }
}
