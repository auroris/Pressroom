using System.Text.Json;
using Pressroom.Contracts;

namespace Pressroom.Contracts.Tests;

public sealed class ContractTests
{
    [Fact]
    public void MinimalStaticHtmlNeedsNoReadinessFlag()
    {
        var request = JsonSerializer.Deserialize<RenderRequest>("""{"html":"<!doctype html><html><body>Bonjour</body></html>"}""", JsonDefaults.Options)!;
        Assert.Empty(RequestValidator.Validate(request));
        Assert.Null(request.Options!.Readiness!.Function);
        Assert.Null(request.Options.TimeoutMs);
        Assert.True(request.Options.JavaScript);
        Assert.True(request.Options.Tagged);
        Assert.Equal(PdfDisposition.Attachment, request.Disposition);
    }

    [Theory]
    [InlineData("inline", PdfDisposition.Inline)]
    [InlineData("attachment", PdfDisposition.Attachment)]
    public void DispositionIsExplicitAndRoundTrips(string name, PdfDisposition expected)
    {
        var request = JsonSerializer.Deserialize<RenderRequest>($$"""{"html":"hello","disposition":"{{name}}"}""", JsonDefaults.Options)!;
        Assert.Equal(expected, request.Disposition);
        Assert.Contains($"\"disposition\":\"{name}\"", JsonSerializer.Serialize(request, JsonDefaults.Options));
    }

    [Theory]
    [InlineData("""{"html":"hello","optinos":{}}""")]
    [InlineData("""{"html":"hello","options":{"scsale":1}}""")]
    [InlineData("""{"html":"hello","options":{"pageSize":{"width":"8in","height":"11in","unit":"in"}}}""")]
    [InlineData("""{"html":"hello","options":{"pageSize":{"width":"8in","width":"9in","height":"11in"}}}""")]
    [InlineData("""{"html":"hello","options":{"scale":"1"}}""")]
    [InlineData("""{"html":"hello","disposition":"download"}""")]
    [InlineData("""{"html":"hello","disposition":0}""")]
    public void UnknownOrAmbiguousJsonIsRejected(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RenderRequest>(json, JsonDefaults.Options));

    [Theory]
    [InlineData("Letter")]
    [InlineData("Legal")]
    [InlineData("Tabloid")]
    [InlineData("A3")]
    [InlineData("A4")]
    [InlineData("A5")]
    public void NamedSizesRoundTrip(string name)
    {
        var request = new RenderRequest { Html = "hello", Options = new() { PageSize = PageSize.Named(name) } };
        var json = JsonSerializer.Serialize(request, JsonDefaults.Options);
        var copy = JsonSerializer.Deserialize<RenderRequest>(json, JsonDefaults.Options)!;
        Assert.Equal(name, copy.Options!.PageSize!.Name);
        Assert.Empty(RequestValidator.Validate(copy));
    }

    [Fact]
    public void CustomDimensionsRoundTrip()
    {
        var request = new RenderRequest { Html = "hello", Options = new() { PageSize = PageSize.Custom("8.5in", "11in") } };
        var copy = JsonSerializer.Deserialize<RenderRequest>(JsonSerializer.Serialize(request, JsonDefaults.Options), JsonDefaults.Options)!;
        Assert.Equal("8.5in", copy.Options!.PageSize!.Width);
        Assert.Equal("11in", copy.Options.PageSize.Height);
        Assert.Empty(RequestValidator.Validate(copy));
    }

    [Theory]
    [InlineData("-1in")]
    [InlineData("0in")]
    [InlineData("20em")]
    [InlineData("201in")]
    [InlineData("NaNin")]
    [InlineData("1in;background:red")]
    public void InvalidDimensionsAreFieldErrors(string width)
    {
        var request = new RenderRequest { Html = "hello", Options = new() { PageSize = PageSize.Custom(width, "11in") } };
        Assert.Contains("options.pageSize.width", RequestValidator.Validate(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.09)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScaleIsRejected(double scale)
        => Assert.Contains("options.scale", RequestValidator.Validate(new() { Html = "hello", Options = new() { Scale = scale } }));

    [Fact]
    public void ReadinessCannotWaitForJavaScriptWhenDisabled()
    {
        var errors = RequestValidator.Validate(new()
        {
            Html = "hello",
            Options = new() { JavaScript = false, Readiness = new() { Function = "window.readyToPrint === true" } }
        });
        Assert.Contains("options.readiness.function", errors);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"javaScript\":false}", false)]
    [InlineData("{\"javaScript\":true}", true)]
    public void JavaScriptRequestOptionAllowsAnExplicitOptOut(string json, bool expected)
    {
        var options = JsonSerializer.Deserialize<RenderOptions>(json, JsonDefaults.Options)!;
        Assert.Equal(expected, options.JavaScript);
        var request = new RenderRequest { Html = "<p>Chart</p>", Options = options with
        {
            Readiness = new() { Function = "window.readyToPrint === true" }
        } };
        Assert.Equal(!expected, RequestValidator.Validate(request).ContainsKey("options.readiness.function"));
    }

    [Fact]
    public void MarginsCannotConsumeTheWholePrintablePage()
    {
        var request = new RenderRequest
        {
            Html = "hello", Options = new() { PageSize = PageSize.Named("Letter"), Margins = new() { Top = "6in", Bottom = "6in" } }
        };
        Assert.Contains("options.margins", RequestValidator.Validate(request));
        // CSS can choose a different page box, so this relation cannot be inferred from Letter.
        Assert.DoesNotContain("options.margins", RequestValidator.Validate(request with { Options = request.Options with { PreferCssPageSize = true } }));
    }

    [Fact]
    public void ReadinessDelayFitsInSharedBudget()
        => Assert.Contains("options.readiness.delayMs", RequestValidator.Validate(new()
        {
            Html = "hello", Options = new() { Readiness = new() { TimeoutMs = 100, DelayMs = 101 } }
        }));

    [Fact]
    public void NullNestedOptionsReturnErrorsInsteadOfNullReferenceExceptions()
    {
        Assert.Contains("options", RequestValidator.Validate(new() { Html = "hello", Options = null }));
        var errors = RequestValidator.Validate(new() { Html = "hello", Options = new() { Margins = null, Readiness = null } });
        Assert.Contains("options.margins", errors);
        Assert.Contains("options.readiness", errors);
    }

    [Fact]
    public void CallerTimeoutCannotExceedServerMaximum()
        => Assert.Contains("options.timeoutMs", RequestValidator.Validate(new() { Html = "hello", Options = new() { TimeoutMs = 5001 } }, 5000));

    [Theory]
    [InlineData("../../CON", "_.._CON.pdf")]
    [InlineData("CON", "_CON.pdf")]
    [InlineData("folder\\orders.pdf", "folder_orders.pdf")]
    [InlineData("orders\r\nX-Test: bad.pdf", "orders__X-Test_ bad.pdf")]
    [InlineData("   ", "document.pdf")]
    [InlineData("résumé", "résumé.pdf")]
    public void AttachmentNamesAreSafeOnWindowsAndInHeaders(string input, string expected)
        => Assert.Equal(expected, RequestValidator.SanitizeFileName(input));
}
