using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pressroom.Client;

namespace Pressroom.Contracts.Tests;

public sealed class JobClientTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(JobStatus.Queued, "queued")]
    [InlineData(JobStatus.Rendering, "rendering")]
    [InlineData(JobStatus.Succeeded, "succeeded")]
    [InlineData(JobStatus.Failed, "failed")]
    [InlineData(JobStatus.Cancelled, "cancelled")]
    [InlineData(JobStatus.Expired, "expired")]
    public void StatusSerializationIsStrictAndStable(JobStatus status, string text)
    {
        Assert.Equal($"\"{text}\"", JsonSerializer.Serialize(status, JsonDefaults.Options));
        Assert.Equal(status, JsonSerializer.Deserialize<JobStatus>($"\"{text}\"", JsonDefaults.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobStatus>("0", JsonDefaults.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobStatus>("\"unknown\"", JsonDefaults.Options));
    }

    [Fact]
    public async Task SubmitCarriesIdempotencyAndWaitPollsUntilTerminal()
    {
        var polls = 0;
        using var http = Http((message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            Assert.StartsWith("/nested/v1/jobs", path);
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("one", Assert.Single(message.Headers.GetValues("Idempotency-Key")));
                return Task.FromResult(Response(JobStatus.Queued, HttpStatusCode.Accepted));
            }
            return Task.FromResult(Response(++polls < 2 ? JobStatus.Rendering : JobStatus.Succeeded));
        });
        var client = new PressroomClient(http);
        Assert.Equal(Id, (await client.SubmitAsync(new() { Html = "hello" }, "one", Token)).Id);
        Assert.Equal(JobStatus.Succeeded, (await client.WaitAsync(Id, Token, TimeSpan.FromSeconds(2))).Status);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task WaitDeadlineAndCancellationDoNotDeleteOrResubmit()
    {
        var calls = 0;
        using var http = Http((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            calls++;
            return Task.FromResult(Response(JobStatus.Queued));
        });
        var client = new PressroomClient(http);
        await Assert.ThrowsAsync<TimeoutException>(() => client.WaitAsync(Id, Token, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WaitAsync(Id, cancellation.Token));
    }

    [Fact]
    public async Task ErrorCodeAndRetryableSurviveAndFailuresAreNotRetried()
    {
        var calls = 0;
        using var http = Http((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = JsonContent.Create(new JobError(503, "Interrupted", Code: "interrupted", Retryable: true), options: JsonDefaults.Options) };
            response.Content.Headers.ContentType = new("application/problem+json");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<PressroomException>(() => new PressroomClient(http).GetJobAsync(Id, Token));
        Assert.Equal("interrupted", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BadIdentifiersAndIdempotencyKeysNeverReachTransport()
    {
        using var http = Http((_, _) => throw new InvalidOperationException());
        var client = new PressroomClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetJobAsync("../info", Token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitAsync(new() { Html = "hello" }, "bad key", Token));
    }

    [Fact]
    public async Task BatchNeverExceedsBoundAndDisposingEnumerationCancelsPolling()
    {
        var submits = 0;
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var http = Http(async (message, token) =>
        {
            if (message.Method == HttpMethod.Post) { Interlocked.Increment(ref submits); return Response(JobStatus.Queued, HttpStatusCode.Accepted); }
            pollStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Response(JobStatus.Queued);
        });
        var client = new PressroomClient(http);
        await using var iterator = client.RenderManyAsync(Enumerable.Range(0, 100).Select(_ => new RenderRequest { Html = "hello" }), 3, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var moving = iterator.MoveNextAsync().AsTask();
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(3, submits);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
    }

    private static HttpResponseMessage Response(JobStatus status, HttpStatusCode code = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(code) { Content = JsonContent.Create(new RenderJob { Id = Id, Status = status }, options: JsonDefaults.Options) };
        response.Headers.RetryAfter = new(TimeSpan.Zero);
        return response;
    }
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        => new(new Handler(callback)) { BaseAddress = new("https://example.invalid/nested/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
