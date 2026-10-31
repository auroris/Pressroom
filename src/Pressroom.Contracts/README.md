# Pressroom.Contracts

Browser-independent contracts for the Pressroom HTML-to-PDF API, for .NET 8 and
.NET 10. Includes RenderRequest, page and readiness options, IPdfRenderer,
PdfRenderResult, job documents/status/errors, RequestValidator, and JsonDefaults.
No runtime package dependencies.

```sh
dotnet add package Pressroom.Contracts
```

Use [Pressroom.Client](https://www.nuget.org/packages/Pressroom.Client) to call a
running service over HTTP. It includes this package transitively.

See the [API reference](https://github.com/auroris/Pressroom/blob/main/docs/api.md).
