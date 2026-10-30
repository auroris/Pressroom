using System.Net;
using System.Text;
using System.Text.Json;
using Pressroom.Client;

namespace Pressroom.Contracts.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task PrintSendsPrinterAndReadsSubmissionReceipt()
    {
        using var http = CreateClient(async (request, cancellation) =>
        {
            Assert.Equal("https://example.invalid/pressroom/v1/render", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal("office", body.RootElement.GetProperty("printer").GetString());
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"jobId":"job-1","printer":"office","status":"submitted","pageCount":2}""", Encoding.UTF8, "application/json")
            };
        });
        var result = await new PressroomClient(http).PrintAsync(new() { Html = "<p>Print</p>", Printer = "office" }, TestContext.Current.CancellationToken);
        Assert.Equal(new PrintJobResult("job-1", "office", "submitted", 2), result);
    }

    [Fact]
    public async Task PdfMethodsRejectPrinterBeforeAnyRequestAndPrintRequiresPrinter()
    {
        using var http = CreateClient((_, _) => throw new InvalidOperationException("No request should be sent."));
        var client = new PressroomClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.RenderAsync(new() { Html = "test", Printer = "office" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RenderStreamAsync(new() { Html = "test", Printer = "office" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PrintAsync(new() { Html = "test" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PrintAsync(new() { Html = "test", Printer = "" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrintFailuresArePreservedAndNeverRetried()
    {
        var calls = 0;
        using var http = CreateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("""{"title":"Print submission failed","detail":"Check the queue before retrying."}""", Encoding.UTF8, "application/problem+json")
            });
        });
        var error = await Assert.ThrowsAsync<PressroomException>(() => new PressroomClient(http).PrintAsync(new() { Html = "test", Printer = "office" }, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal("Check the queue before retrying.", error.Detail);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PrintRejectsPdfResponse()
    {
        using var http = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("%PDF", Encoding.ASCII, "application/pdf")
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new PressroomClient(http).PrintAsync(new() { Html = "test", Printer = "office" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamingInterfaceLeavesPdfUnreadAndOwnsItsLifetime()
    {
        var stream = new TrackingStream("%PDF-stream"u8.ToArray());
        using var http = CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("application/pdf");
            return Task.FromResult(response);
        });
        IPdfRenderer renderer = new PressroomClient(http);
        var result = await renderer.RenderAsync(new RenderRequest { Html = "<p>Stream</p>", Disposition = PdfDisposition.Inline }, TestContext.Current.CancellationToken);
        Assert.True(result.PdfStream.CanRead);
        Assert.Equal(0, stream.Position);
        Assert.False(stream.WasDisposed);
        Assert.Equal(PdfDisposition.Inline, result.Disposition);
        await result.DisposeAsync();
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task RenderUsesConfiguredApplicationPathAndReadsPdfMetadata()
    {
        using var http = CreateClient(async (request, cancellation) =>
        {
            Assert.Equal("https://example.invalid/pressroom/v1/render", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellation));
            Assert.Equal("<p>Résumé</p>", payload.RootElement.GetProperty("html").GetString());
            Assert.Equal("inline", payload.RootElement.GetProperty("disposition").GetString());
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-test"u8.ToArray()) };
            result.Content.Headers.ContentType = new("application/pdf");
            result.Content.Headers.ContentDisposition = new("inline") { FileNameStar = "résumé.pdf" };
            result.Headers.Add("X-Render-Pages", "2");
            result.Headers.Add("X-Render-Duration-Ms", "153");
            return result;
        });
        var pdf = await new PressroomClient(http).RenderAsync(new() { Html = "<p>Résumé</p>", Disposition = PdfDisposition.Inline }, TestContext.Current.CancellationToken);
        Assert.Equal("%PDF-test", Encoding.UTF8.GetString(pdf.Bytes));
        Assert.Equal("résumé.pdf", pdf.FileName);
        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(153, pdf.DurationMs);
        Assert.Equal(PdfDisposition.Inline, pdf.Disposition);
    }

    [Fact]
    public async Task ProblemDetailsAndRetryAfterSurviveInTypedException()
    {
        using var http = CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"title":"Renderer unavailable","detail":"Try again shortly."}""", Encoding.UTF8, "application/problem+json")
            };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        var failure = await Assert.ThrowsAsync<PressroomException>(() => new PressroomClient(http).RenderAsync(new() { Html = "hello" }, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), failure.RetryAfter);
        Assert.Equal("Try again shortly.", failure.Detail);
    }

    [Fact]
    public async Task HostGeneratedNonJsonErrorRetainsHttpStatus()
    {
        using var http = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new StringContent("<html>IIS request too large</html>", Encoding.UTF8, "text/html")
        }));
        var failure = await Assert.ThrowsAsync<PressroomException>(() => new PressroomClient(http).RenderAsync(new() { Html = "hello" }, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, failure.StatusCode);
    }

    [Fact]
    public async Task UnreadyServiceIsFalseForCallerOwnedFallback()
    {
        using var http = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.False(await new PressroomClient(http).IsReadyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationReachesHttpTransport()
    {
        using var cts = new CancellationTokenSource();
        using var http = CreateClient(async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PressroomClient(http).RenderAsync(new() { Html = "hello" }, cts.Token));
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new Handler(handler)) { BaseAddress = new("https://example.invalid/pressroom/") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
