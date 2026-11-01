# Pressroom.Contracts

Request and response contracts for the Pressroom HTML-to-PDF API, for .NET 8 and
.NET 10. Includes `RenderRequest`, page and readiness options, `IPdfRenderer`,
`PdfRenderResult`, job records, `RequestValidator`, and `JsonDefaults`. The project
uses the .NET base class library.

This remains a separate source project shared by the client, server, and renderer.
It is not published as a separate NuGet package: `AurorisNet.Pressroom.Client`
bundles `Pressroom.Contracts.dll` for each supported framework.

```sh
dotnet add package AurorisNet.Pressroom.Client --version <version>
```

The `Pressroom.Contracts` namespace is unchanged. See the
[client README](../Pressroom.Client/README.md) for installation from GitHub builds.

See the [API reference](https://github.com/auroris/Pressroom/blob/main/docs/development/api.md).
