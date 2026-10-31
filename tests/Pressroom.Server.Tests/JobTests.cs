using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pressroom.Client;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Authentication;
using Pressroom.Server.Jobs;
using Xunit;

namespace Pressroom.Server.Tests;

public sealed class JobHttpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RenderRequest Request(string text = "Hello") => new() { Html = $"<p>{text}</p>" };

    [Fact]
    public async Task PrintingJobsRequireAKeyAndReturnAReceiptWithoutAPdf()
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous, jobsEnabled: true);
        using var anonymous = app.CreateClient();
        var request = Request() with { Printer = "office" };
        using var rejected = await anonymous.PostAsJsonAsync("/v1/jobs", request, JsonDefaults.Options, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(0, app.Renderer.Calls);
        Assert.Equal(0, app.Printing.Calls);
        using var http = app.CreateAuthenticatedClient();
        using var unknown = await http.PostAsJsonAsync("/v1/jobs", request with { Printer = "unknown" }, JsonDefaults.Options, Token);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(0, app.Printing.Calls);
        var client = new PressroomClient(http);
        var job = await client.SubmitAsync(request, cancellationToken: Token);
        var completed = await client.WaitAsync(job.Id, Token, TimeSpan.FromSeconds(10));
        Assert.Equal(JobStatus.Succeeded, completed.Status);
        Assert.Equal(new PrintJobResult("test-job", "office", "submitted", 1), completed.PrintJob);
        Assert.Equal(1, app.Printing.Calls);
        Assert.Equal(FakeRenderer.Output.Pdf, app.Printing.LastDocument!.Pdf);
        using var download = await http.GetAsync($"/v1/jobs/{job.Id}/result", Token);
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
    }

    [Fact]
    public async Task JobsAreDisabledByDefault()
    {
        using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/v1/jobs", Request(), Token)).StatusCode);
        using var info = JsonDocument.Parse(await http.GetStringAsync("/v1/info", Token));
        Assert.False(info.RootElement.GetProperty("jobs").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task JobLinksHonorAnIisApplicationBasePath()
    {
        using var app = new TestApplication(jobsEnabled: true, pathBase: "/pressroom");
        using var http = app.CreateAuthenticatedClient();
        var client = new PressroomClient(http);
        using var response = await http.PostAsJsonAsync("/v1/jobs", Request(), JsonDefaults.Options, Token);
        var job = (await response.Content.ReadFromJsonAsync<RenderJob>(JsonDefaults.Options, Token))!;
        var path = $"/pressroom/v1/jobs/{job.Id}";
        Assert.Equal(path, response.Headers.Location!.OriginalString);
        Assert.Equal(path, job.Links!.Self);
        Assert.Equal(path + "/result", (await client.GetJobAsync(job.Id, Token)).Links!.Result);
        Assert.Equal(path, Assert.Single((await client.ListJobsAsync(cancellationToken: Token)).Items).Links!.Self);
    }

    [Fact]
    public async Task LifecycleIdempotencyIsolationAndCounters()
    {
        using var app = new TestApplication(jobsEnabled: true);
        using var http = app.CreateAuthenticatedClient();
        var client = new PressroomClient(http);
        using var other = app.CreateClient();
        other.DefaultRequestHeaders.Add("X-Api-Key", TestApplication.StaticSecret);
        var capacity = app.Services.GetRequiredService<RenderCoordinator>();
        var blocker = capacity.TryReserveBackground(0)!;
        Assert.NotNull(blocker);
        RenderJob job;
        try
        {
            job = await client.SubmitAsync(Request() with { FileName = "rapport/été\r\n2026.pdf" }, "same", Token);
            Assert.Equal(JobStatus.Queued, job.Status);
            Assert.Equal(1, job.QueuePosition);
            Assert.Equal(job.Id, (await client.SubmitAsync(Request("changed"), "same", Token)).Id);
            var early = await Assert.ThrowsAsync<PressroomException>(() => client.DownloadAsync(job.Id, Token));
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.NotNull(early.RetryAfter);
            Assert.Single((await client.ListJobsAsync(cancellationToken: Token)).Items);
            foreach (var path in new[] { $"v1/jobs/{job.Id}", $"v1/jobs/{job.Id}/result" })
                Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path, Token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"v1/jobs/{job.Id}", Token)).StatusCode);
            Assert.Empty((await new PressroomClient(other).ListJobsAsync(cancellationToken: Token)).Items);
            using var info = JsonDocument.Parse(await http.GetStringAsync("/v1/info", Token));
            Assert.Equal(1, info.RootElement.GetProperty("jobs").GetProperty("queued").GetInt32());
        }
        finally { blocker.Dispose(); }
        var completed = await client.WaitAsync(job.Id, Token, TimeSpan.FromSeconds(10));
        Assert.Equal(JobStatus.Succeeded, completed.Status);
        Assert.Equal(1, completed.PageCount);
        Assert.NotNull(completed.StartedUtc);
        Assert.NotNull(completed.CompletedUtc);
        using var jobJson = JsonDocument.Parse(await http.GetStringAsync($"v1/jobs/{job.Id}", Token));
        Assert.False(jobJson.RootElement.TryGetProperty("disposition", out _));
        using var response = await http.GetAsync($"v1/jobs/{job.Id}/result", Token);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("rapport_été__2026.pdf", response.Content.Headers.ContentDisposition.FileNameStar);
        var pdf = await client.DownloadAsync(job.Id, Token);
        Assert.Equal(FakeRenderer.Output.Pdf, pdf.Bytes);
        Assert.Equal("rapport_été__2026.pdf", pdf.FileName);
        await client.CancelAsync(job.Id, Token);
        await client.CancelAsync(job.Id, Token);
        Assert.Equal(HttpStatusCode.Gone, (await Assert.ThrowsAsync<PressroomException>(() => client.DownloadAsync(job.Id, Token))).StatusCode);
    }

    [Fact]
    public async Task QueuedAndRenderingCancellationLeaveSyncCapacityAvailable()
    {
        using var app = new TestApplication(jobsEnabled: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Renderer.Callback = async (request, token) =>
        {
            if (request.Html.Contains("slow"))
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { stopped.TrySetResult(); }
            }
            return FakeRenderer.Output;
        };
        using var http = app.CreateAuthenticatedClient();
        var client = new PressroomClient(http);
        var first = await client.SubmitAsync(Request("slow"), cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var second = await client.SubmitAsync(Request("queued"), cancellationToken: Token);
        await client.CancelAsync(second.Id, Token);
        Assert.Equal(JobStatus.Cancelled, (await client.GetJobAsync(second.Id, Token)).Status);
        Assert.Equal(1, (await client.RenderAsync(Request("interactive"), Token)).PageCount);
        await client.CancelAsync(first.Id, Token);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(JobStatus.Cancelled, (await client.GetJobAsync(first.Id, Token)).Status);
    }

    [Fact]
    public async Task QueueLimitsAnonymousOwnershipAndDownloadDeletion()
    {
        using var app = new TestApplication(authMode: AuthMode.Anonymous, jobsEnabled: true,
            configureJobs: settings => { settings.MaxQueued = 2; settings.MaxQueuedPerKey = 1; settings.DeleteResultOnDownload = true; });
        using var http = app.CreateClient();
        using var anonymous = app.CreateClient();
        var client = new PressroomClient(http);
        var blocker = app.Services.GetRequiredService<RenderCoordinator>().TryReserveBackground(0)!;
        var job = await client.SubmitAsync(Request(), cancellationToken: Token);
        try
        {
            Assert.Equal(job.Id, (await new PressroomClient(anonymous).GetJobAsync(job.Id, Token)).Id);
            var full = await Assert.ThrowsAsync<PressroomException>(() => client.SubmitAsync(Request(), cancellationToken: Token));
            Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
            Assert.NotNull(full.RetryAfter);
        }
        finally { blocker.Dispose(); }
        await client.WaitAsync(job.Id, Token, TimeSpan.FromSeconds(10));
        await client.DownloadAsync(job.Id, Token);
        Assert.Equal(HttpStatusCode.Gone, (await Assert.ThrowsAsync<PressroomException>(() => client.DownloadAsync(job.Id, Token))).StatusCode);
    }

    [Fact]
    public async Task InvalidRequestsAndKeysAreRejectedBeforeStorage()
    {
        using var app = new TestApplication(jobsEnabled: true);
        using var http = app.CreateAuthenticatedClient();
        foreach (var request in new[] { Request() with { Html = "" }, Request() with { Html = "<base href='https://bad.invalid'>" },
            Request() with { Options = new() { Readiness = new() { Function = "true" } } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/v1/jobs", request, JsonDefaults.Options, Token)).StatusCode);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/jobs") { Content = JsonContent.Create(Request(), options: JsonDefaults.Options) };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "one", "two" });
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(message, Token)).StatusCode);
        Assert.Empty((await new PressroomClient(http).ListJobsAsync(cancellationToken: Token)).Items);
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("attachment")]
    public async Task RemovedDispositionIsRejectedBeforeStorage(string disposition)
    {
        using var app = new TestApplication(jobsEnabled: true);
        using var http = app.CreateAuthenticatedClient();
        using var response = await http.PostAsJsonAsync("/v1/jobs", new { html = "<p>Hello</p>", disposition }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await new PressroomClient(http).ListJobsAsync(cancellationToken: Token)).Items);
        Assert.Equal(0, app.Renderer.Calls);
    }

    [Fact]
    public async Task BatchCompletesAndSurfacesDocumentErrorsWithoutRetrying()
    {
        using var app = new TestApplication(jobsEnabled: true);
        app.Renderer.Callback = (request, _) => request.Html.Contains("bad") ? throw new RenderDocumentException("private page text") : Task.FromResult(FakeRenderer.Output);
        using var http = app.CreateAuthenticatedClient();
        var client = new PressroomClient(http);
        var results = new List<RenderManyResult>();
        await foreach (var result in client.RenderManyAsync(new[] { Request("one"), Request("bad"), Request("two") }, 2, Token)) results.Add(result);
        Assert.Equal(2, results.Count(item => item.Pdf is not null));
        var failure = Assert.Single(results, item => item.Error is not null).Error!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failure.StatusCode);
        Assert.Equal("document", failure.Code);
        Assert.False(failure.Retryable);
        Assert.DoesNotContain("private", failure.Message);
        Assert.Equal(3, app.Renderer.Calls);
    }
}

public sealed class JobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pressroom-jobs-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private readonly JobsSettings _settings = new() { Enabled = true, ResultRetention = TimeSpan.FromSeconds(5), RequestRetention = TimeSpan.FromSeconds(2), MaxQueueAge = TimeSpan.FromSeconds(3) };
    private LocalJobStore Store(ILogger<LocalJobStore>? logger = null) => new(Options.Create(_settings), Options.Create(new PathsSettings { StateRoot = _root }), _clock,
        logger ?? NullLogger<LocalJobStore>.Instance);
    private static RenderRequest Request(string text = "one") => new() { Html = text, Options = new() { TimeoutMs = 5000 } };

    [Theory]
    [InlineData("inline")]
    [InlineData("attachment")]
    public void RestartReadsLegacyDispositionWithoutExposingOrPersistingIt(string disposition)
    {
        string queued, completed;
        using (var first = Store())
        {
            first.Initialize();
            queued = first.Submit("key:a", Request(), "once").Id;
            completed = first.Submit("key:a", Request(), null).Id;
            first.Start(completed);
            first.Succeed(completed, FakeRenderer.Output, 1, null);
        }
        foreach (var id in new[] { queued, completed })
        {
            var path = Path.Combine(_root, "jobs", id, "job.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["job"]!["disposition"] = disposition;
            File.WriteAllText(path, json.ToJsonString());
        }
        var requestPath = Path.Combine(_root, "jobs", queued, "request.json");
        var requestJson = JsonNode.Parse(File.ReadAllText(requestPath))!;
        requestJson["disposition"] = disposition;
        File.WriteAllText(requestPath, requestJson.ToJsonString());

        using var restored = Store();
        restored.Initialize();
        Assert.Equal(Request(), restored.ReadRequest(queued));
        Assert.Equal(queued, restored.Submit("key:a", Request(), "once").Id);
        using var download = restored.OpenResult("key:a", completed);
        Assert.NotNull(download);
        using var bytes = new MemoryStream();
        download.Stream.CopyTo(bytes);
        Assert.Equal(FakeRenderer.Output.Pdf, bytes.ToArray());
        restored.Start(queued);
        restored.Succeed(queued, FakeRenderer.Output, 1, null);
        Assert.DoesNotContain("\"disposition\"", File.ReadAllText(Path.Combine(_root, "jobs", queued, "job.json")));
        Assert.DoesNotContain("\"disposition\"", JsonSerializer.Serialize(restored.Get("key:a", completed), JsonDefaults.Options));
    }

    [Fact]
    public void RestartRecoversQueuedAndMarksInFlightInterrupted()
    {
        string queued, active;
        using (var first = Store())
        {
            first.Initialize();
            queued = first.Submit("key:a", Request(), "once").Id;
            active = first.Submit("key:b", Request(), null).Id;
            Assert.True(first.Start(active));
            using var duplicate = Store();
            Assert.Throws<IOException>(() => duplicate.Initialize());
        }
        using var restored = Store();
        restored.Initialize();
        Assert.Equal(queued, Assert.Single(restored.Queued()).Job.Id);
        var interrupted = restored.Get("key:b", active)!;
        Assert.Equal(JobStatus.Failed, interrupted.Status);
        Assert.Equal("interrupted", interrupted.Error!.Code);
        Assert.True(interrupted.Error.Retryable);
        Assert.Equal(queued, restored.Submit("key:a", Request(), "once").Id);
        Assert.Null(restored.Get("key:b", queued));
    }

    [Fact]
    public void SweeperExpiresQueueRequestsResultsAndTombstones()
    {
        using var store = Store();
        store.Initialize();
        var queued = store.Submit("key:a", Request(), null).Id;
        var failed = store.Submit("key:a", Request(), null).Id;
        store.Start(failed);
        store.Fail(failed, new(503, "Unavailable", Code: "unavailable", Retryable: true));
        var done = store.Submit("key:a", Request(), null).Id;
        store.Start(done);
        store.Succeed(done, FakeRenderer.Output, 1, null);
        Assert.False(File.Exists(Path.Combine(_root, "jobs", done, "request.json")));
        _clock.Advance(TimeSpan.FromSeconds(4));
        store.Sweep();
        Assert.Equal(JobStatus.Expired, store.Get("key:a", queued)!.Status);
        Assert.False(File.Exists(Path.Combine(_root, "jobs", failed, "request.json")));
        _clock.Advance(TimeSpan.FromSeconds(2));
        store.Sweep();
        Assert.False(File.Exists(Path.Combine(_root, "jobs", done, "result.pdf")));
        Assert.Equal(JobStatus.Expired, store.Get("key:a", done)!.Status);
        _clock.Advance(TimeSpan.FromSeconds(6));
        store.Sweep();
        Assert.Equal(0, store.Capacity.Stored);
    }

    [Fact]
    public async Task RestartResumesOneHundredQueuedJobsAndRetainsOneInterruptedFailure()
    {
        _settings.MaxQueueAge = TimeSpan.FromMinutes(10);
        string interrupted;
        using (var first = Store())
        {
            first.Initialize();
            interrupted = first.Submit("a", Request("interrupted"), null).Id;
            first.Start(interrupted);
            for (var index = 0; index < 100; index++) first.Submit("a", Request(index.ToString()), null);
        }
        using var restored = Store();
        var renderer = new FakeRenderer();
        var coordinator = new RenderCoordinator(renderer, Options.Create(new RenderSettings()));
        using var scheduler = new JobScheduler(restored, coordinator, new FakePrintService(), Options.Create(_settings), NullLogger<JobScheduler>.Instance);
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (restored.Capacity.Queued + restored.Capacity.Rendering != 0) await Task.Delay(25, timeout.Token);
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100, renderer.Calls);
        Assert.Equal(100, restored.List("a", JobStatus.Succeeded, 100, null).Items.Count);
        var failure = Assert.Single(restored.List("a", JobStatus.Failed, 100, null).Items);
        Assert.Equal(interrupted, failure.Id);
        Assert.Equal("interrupted", failure.Error!.Code);
        Assert.True(failure.Error.Retryable);
    }

    [Fact]
    public void StorageBudgetIncludesRequestsMetadataAndResults()
    {
        _settings.MaxStoredBytes = 1024 * 1024;
        using var store = Store();
        store.Initialize();
        var id = store.Submit("key:a", Request(new string('x', 800_000)), null).Id;
        Assert.Throws<JobCapacityException>(() => store.Submit("key:a", Request(new string('x', 800_000)), null));
        store.Start(id);
        store.Succeed(id, new(new byte[800_000], 1, 0), 1, null);
        Assert.Equal("storage_full", store.Get("key:a", id)!.Error!.Code);
        Assert.True(store.Capacity.StoredBytes <= _settings.MaxStoredBytes);
        Assert.False(File.Exists(Path.Combine(_root, "jobs", id, "result.pdf")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(_root, "jobs", id)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_root, "jobs", id, "request.json")));
        }
        else
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var directory = new DirectoryInfo(Path.Combine(_root, "jobs", id));
            var acl = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
            Assert.True(acl.AreAccessRulesProtected);
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                Assert.Equal(identity.User, rule.IdentityReference);
        }
    }

    [Fact]
    public async Task SchedulerRotatesOwnersAndKeepsSyncReservation()
    {
        using var store = Store();
        store.Initialize();
        _settings.MaxQueueAge = TimeSpan.FromMinutes(10);
        var order = new ConcurrentQueue<string>();
        var renderer = new FakeRenderer { Callback = (request, _) => { order.Enqueue(request.Html); return Task.FromResult(FakeRenderer.Output); } };
        var coordinator = new RenderCoordinator(renderer, Options.Create(new RenderSettings { Concurrency = 2 }));
        using var blocker = coordinator.TryReserveBackground(0)!;
        store.Submit("a", Request("a1"), null);
        store.Submit("a", Request("a2"), null);
        store.Submit("b", Request("b1"), null);
        using var scheduler = new JobScheduler(store, coordinator, new FakePrintService(), Options.Create(_settings), NullLogger<JobScheduler>.Instance);
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(order);
        blocker.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (store.Capacity.Queued + store.Capacity.Rendering != 0) await Task.Delay(25, timeout.Token);
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
        var owners = order.Select(item => item[0]).ToArray();
        Assert.Equal(3, owners.Length);
        Assert.NotEqual(owners[0], owners[1]);
        Assert.Equal(new[] { "a1", "b1", "a2" }, order);
    }

    [Fact]
    public void SweepLeavesAnUndeletableJobForALaterSweepAndKeepsServingTheRest()
    {
        var logger = new ListLogger<LocalJobStore>();
        using var store = Store(logger);
        store.Initialize();
        var stuck = store.Submit("key:a", Request(), null).Id;
        var other = store.Submit("key:a", Request(), null).Id;
        _clock.Advance(TimeSpan.FromSeconds(4));
        store.Sweep();
        Assert.Equal(JobStatus.Expired, store.Get("key:a", stuck)!.Status);
        var directory = Path.Combine(_root, "jobs", stuck);
        using (BlockDeletion(directory))
        {
            _clock.Advance(TimeSpan.FromSeconds(6));
            // Every jobs operation sweeps first; none of them may fail because of the one held job.
            Assert.Equal(stuck, Assert.Single(store.List("key:a", null, 10, null).Items).Id);
            Assert.Null(store.Get("key:a", other));
            Assert.False(Directory.Exists(Path.Combine(_root, "jobs", other)));
            store.Submit("key:b", Request(), null);
            // Metadata is removed last, so a restart at this point would still recognise the tombstone.
            Assert.True(File.Exists(Path.Combine(directory, "job.json")));
        }
        Assert.NotEmpty(logger.Entries);
        foreach (var (level, message) in logger.Entries)
        {
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains(stuck, message);
            Assert.Contains(OperatingSystem.IsWindows() ? nameof(IOException) : nameof(UnauthorizedAccessException), message);
            Assert.DoesNotContain(_root, message);
        }
        store.Sweep();
        Assert.Null(store.Get("key:a", stuck));
        Assert.False(Directory.Exists(directory));
        Assert.Equal(1, store.Capacity.Stored);
    }

    // Holds the job's metadata open without delete sharing on Windows, or removes write
    // permission from its directory on Linux, so that removing the directory fails.
    private static IDisposable BlockDeletion(string directory)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(Path.Combine(directory, "job.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var restore = new Restore(() =>
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        });
        try
        {
            File.Create(Path.Combine(directory, "probe")).Dispose();
            File.Delete(Path.Combine(directory, "probe"));
        }
        catch (UnauthorizedAccessException) { return restore; }
        restore.Dispose();
        Assert.Skip("Directory permissions do not restrict this user (root); the deletion failure cannot be simulated.");
        return restore;
    }

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    [Fact]
    public void ListReportsGlobalQueuePositionsForAFewHundredQueuedJobs()
    {
        _settings.MaxQueueAge = TimeSpan.FromMinutes(10);
        _settings.MaxQueued = _settings.MaxQueuedPerKey = 1000;
        using var store = Store();
        store.Initialize();
        var ids = Enumerable.Range(0, 300).Select(index => store.Submit(index % 2 == 0 ? "a" : "b", Request(), null).Id).ToArray();
        Assert.True(store.Start(ids[10]));
        // Positions are global submission order among queued jobs; a started job has none.
        var expected = ids.Where((_, index) => index != 10).Select((id, index) => (id, Position: (int?)(index + 1)))
            .ToDictionary(pair => pair.id, pair => pair.Position);
        expected[ids[10]] = null;
        foreach (var owner in new[] { "a", "b" })
        {
            var items = store.List(owner, null, 500, null).Items;
            Assert.Equal(150, items.Count);
            foreach (var item in items) Assert.Equal(expected[item.Id], item.QueuePosition);
        }
        var page = store.List("b", JobStatus.Queued, 20, null);
        Assert.Equal(20, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(expected[item.Id], item.QueuePosition));
        Assert.Equal(299, store.Get("b", ids[299])!.QueuePosition);
        Assert.Equal(1, store.Get("a", ids[0])!.QueuePosition);
    }

    [Fact]
    public async Task StorageFailuresDuringExecutionAreRecordedWithoutStoppingTheScheduler()
    {
        _settings.MaxQueueAge = TimeSpan.FromMinutes(10);
        using var inner = Store();
        var store = new FaultingJobStore(inner);
        var logger = new ListLogger<JobScheduler>();
        var coordinator = new RenderCoordinator(new FakeRenderer(), Options.Create(new RenderSettings { Concurrency = 2 }));
        using var scheduler = new JobScheduler(store, coordinator, new FakePrintService(), Options.Create(_settings), logger);
        inner.Initialize();
        var unrecorded = inner.Submit("a", Request("unrecorded"), null).Id;
        var recorded = inner.Submit("a", Request("recorded"), null).Id;
        var healthy = inner.Submit("a", Request("healthy"), null).Id;
        store.SucceedFailures.Add(unrecorded);
        store.SucceedFailures.Add(recorded);
        store.FailFailures.Add(unrecorded);
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (inner.Get("a", healthy)!.Status != JobStatus.Succeeded) await Task.Delay(25, timeout.Token);

        // The scheduler survived both failures, released their render slots, and kept dispatching.
        Assert.False(scheduler.ExecuteTask!.IsCompleted);
        var failed = inner.Get("a", recorded)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal("internal", failed.Error!.Code);
        // Neither outcome could be stored; the job stays in flight until restart marks it interrupted.
        Assert.Equal(JobStatus.Rendering, inner.Get("a", unrecorded)!.Status);
        Assert.Equal(FakeRenderer.Output.Pdf, (await coordinator.RenderAsync(Request("sync"), timeout.Token)).Pdf);
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(scheduler.ExecuteTask.IsCompletedSuccessfully);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(recorded) && entry.Message.Contains(nameof(IOException)));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(unrecorded) && entry.Message.Contains("could not be recorded"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("simulated storage failure"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    /// <summary>Delegates to the real store, throwing a storage exception from selected transitions.</summary>
    private sealed class FaultingJobStore(LocalJobStore inner) : IJobStore
    {
        public ConcurrentBag<string> SucceedFailures { get; } = [];
        public ConcurrentBag<string> FailFailures { get; } = [];
        public void Initialize() => inner.Initialize();
        public RenderJob Submit(string owner, RenderRequest request, string? idempotencyKey) => inner.Submit(owner, request, idempotencyKey);
        public RenderJob? Get(string owner, string id) => inner.Get(owner, id);
        public RenderJobPage List(string owner, JobStatus? status, int limit, string? after) => inner.List(owner, status, limit, after);
        public IReadOnlyList<StoredJob> Queued() => inner.Queued();
        public RenderRequest? ReadRequest(string id) => inner.ReadRequest(id);
        public bool Start(string id) => inner.Start(id);
        public void Succeed(string id, RenderOutput output, long durationMs, PrintJobResult? printJob)
        {
            if (SucceedFailures.Contains(id)) throw new IOException("simulated storage failure");
            inner.Succeed(id, output, durationMs, printJob);
        }
        public void Fail(string id, JobError error)
        {
            if (FailFailures.Contains(id)) throw new IOException("simulated storage failure");
            inner.Fail(id, error);
        }
        public bool Cancel(string owner, string id) => inner.Cancel(owner, id);
        public JobDownload? OpenResult(string owner, string id) => inner.OpenResult(owner, id);
        public void CompleteDownload(string id) => inner.CompleteDownload(id);
        public void Sweep() => inner.Sweep();
        public JobCapacity Capacity => inner.Capacity;
    }
}

/// <summary>Captures formatted log messages so tests can check they carry metadata only.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.Where(entry => entry.Level >= LogLevel.Warning).ToArray();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception.Message)));
}
