using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pressroom.Contracts;

[JsonConverter(typeof(JobStatusJsonConverter))]
public enum JobStatus { Queued, Rendering, Succeeded, Failed, Cancelled, Expired }

public sealed class JobStatusJsonConverter : JsonConverter<JobStatus>
{
    public override JobStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } text)
            foreach (var value in Enum.GetValues<JobStatus>())
                if (value.ToString().ToLowerInvariant() == text) return value;
        throw new JsonException("Unknown job status.");
    }
    public override void Write(Utf8JsonWriter writer, JobStatus value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown job status.");
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}

public sealed record JobError(int Status, string Title, string? Detail = null, string? Code = null,
    bool? Retryable = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public string Type { get; init; } = "about:blank";
}

public sealed record JobLinks(string Self, string Result);

public sealed record RenderJob
{
    public string Id { get; init; } = "";
    public JobStatus Status { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public int? QueuePosition { get; init; }
    public int? PageCount { get; init; }
    public long? DurationMs { get; init; }
    public string FileName { get; init; } = "document.pdf";
    public string? Printer { get; init; }
    public PrintJobResult? PrintJob { get; init; }
    public JobError? Error { get; init; }
    public JobLinks? Links { get; init; }
    [JsonIgnore] public bool IsTerminal => Status is not (JobStatus.Queued or JobStatus.Rendering);
}

public sealed record RenderJobPage(IReadOnlyList<RenderJob> Items, string? NextAfter);

public static class JobValidation
{
    public static bool IsId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsIdempotencyKey(string? key) => key is { Length: >= 1 and <= 128 } && key.All(c => c is >= '!' and <= '~');
}
