namespace Pressroom.Server.Infrastructure;

public static class Problems
{
    public static Task WriteAsync(HttpContext context, int status, string title, string? detail = null,
        IDictionary<string, string[]>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["requestId"] = context.TraceIdentifier };
        if (errors is not null)
            extensions["errors"] = errors;
        // Do not populate instance with the caller-controlled URL or echo parsing exceptions.
        return Results.Problem(statusCode: status, title: title, detail: detail, extensions: extensions).ExecuteAsync(context);
    }
}
