using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
            job = await client.SubmitAsync(Request(), "same", Token);
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
        Assert.Equal(FakeRenderer.Output.Pdf, (await client.DownloadAsync(job.Id, Token)).Bytes);
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
    private LocalJobStore Store() => new(Options.Create(_settings), Options.Create(new PathsSettings { StateRoot = _root }), _clock);
    private static RenderRequest Request(string text = "one") => new() { Html = text, Options = new() { TimeoutMs = 5000 } };

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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
