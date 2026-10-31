using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pressroom.Contracts;

public sealed record RenderRequest
{
    public string Html { get; init; } = "";
    public string? FileName { get; init; } = "document.pdf";
    public PdfDisposition Disposition { get; init; } = PdfDisposition.Attachment;
    /// <summary>Configured printer identifier. Null returns a PDF; printing requires a valid API key.</summary>
    public string? Printer { get; init; }
    public RenderOptions? Options { get; init; } = new();
}

/// <summary>Submission receipt, not confirmation of physical printing or a Windows spooler job ID.</summary>
public sealed record PrintJobResult(string JobId, string Printer, string Status, int PageCount);

[JsonConverter(typeof(PdfDispositionJsonConverter))]
public enum PdfDisposition
{
    Inline,
    Attachment
}

public sealed class PdfDispositionJsonConverter : JsonConverter<PdfDisposition>
{
    public override PdfDisposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "inline" => PdfDisposition.Inline,
            "attachment" => PdfDisposition.Attachment,
            _ => throw new JsonException("disposition must be inline or attachment.")
        } : throw new JsonException("disposition must be inline or attachment.");

    public override void Write(Utf8JsonWriter writer, PdfDisposition value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            PdfDisposition.Inline => "inline",
            PdfDisposition.Attachment => "attachment",
            _ => throw new JsonException("disposition must be inline or attachment.")
        });
}

public sealed record RenderOptions
{
    public PageSize? PageSize { get; init; }
    public bool Landscape { get; init; }
    public PageMargins? Margins { get; init; } = new();
    public bool PreferCssPageSize { get; init; }
    public bool PrintBackground { get; init; } = true;
    public double Scale { get; init; } = 1;
    public bool DisplayHeaderFooter { get; init; } = true;
    // An explicit empty template suppresses Chromium's built-in header while keeping the footer enabled.
    public string? HeaderTemplate { get; init; } = "<div></div>";
    public string? FooterTemplate { get; init; } = "<div style=\"font-size: 10px; text-align: right; width: 100%; padding-right: 20px;\">Page <span class=\"pageNumber\"></span> / <span class=\"totalPages\"></span></div>";
    public bool Tagged { get; init; } = true;
    public bool Outline { get; init; } = true;
    public string Media { get; init; } = "print";
    /// <summary>Allow document scripts if the API key permits them; false always disables them.</summary>
    public bool JavaScript { get; init; } = true;
    public string Locale { get; init; } = "en-CA";
    public string TimeZone { get; init; } = "America/Edmonton";
    public ReadinessOptions? Readiness { get; init; } = new();
    public int? TimeoutMs { get; init; }
}

public sealed record PageMargins
{
    public string Top { get; init; } = "20mm";
    public string Right { get; init; } = "15mm";
    public string Bottom { get; init; } = "20mm";
    public string Left { get; init; } = "15mm";
}

public sealed record ReadinessOptions
{
    public string? Selector { get; init; }
    public string? Function { get; init; }
    public int DelayMs { get; init; }
    public int TimeoutMs { get; init; } = 5000;
}

[JsonConverter(typeof(PageSizeJsonConverter))]
public sealed record PageSize
{
    public string? Name { get; init; }
    public string? Width { get; init; }
    public string? Height { get; init; }

    public static PageSize Named(string name) => new() { Name = name };
    public static PageSize Custom(string width, string height) => new() { Width = width, Height = height };
}

public sealed class PageSizeJsonConverter : JsonConverter<PageSize>
{
    public override PageSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return PageSize.Named(reader.GetString()!);
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("pageSize must be a named size or an object with width and height.");

        using var document = JsonDocument.ParseValue(ref reader);
        string? width = null, height = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Name is not ("width" or "height") || property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException("pageSize accepts one string width and one string height.");
            if (property.Name == "width") width = property.Value.GetString();
            else height = property.Value.GetString();
        }
        return PageSize.Custom(width!, height!);
    }

    public override void Write(Utf8JsonWriter writer, PageSize value, JsonSerializerOptions options)
    {
        if (value.Name is not null)
        {
            if (value.Width is not null || value.Height is not null)
                throw new JsonException("Named pageSize cannot also have width or height.");
            writer.WriteStringValue(value.Name);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("width", value.Width);
        writer.WriteString("height", value.Height);
        writer.WriteEndObject();
    }
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 32
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
