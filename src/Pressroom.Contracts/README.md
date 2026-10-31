# Pressroom.Contracts

Browser-independent contracts for the Pressroom HTML-to-PDF API, for .NET 8 and
.NET 10. Includes RenderRequest, page and readiness options, IPdfRenderer,
PdfRenderResult, job documents/status/errors, RequestValidator, and JsonDefaults.
No runtime package dependencies.

Download both `Pressroom.Contracts` and `Pressroom.Client` `.nupkg` files from a
GitHub release, or the `nuget-packages` artifact of the GitHub **Packages**
workflow, into a local feed directory; these builds are not published to NuGet.org.

```sh
dotnet add package Pressroom.Contracts --version <version> --source ./packages
```

Use `Pressroom.Client` to call a running service over HTTP. It includes this
package transitively.

See the [API reference](https://github.com/auroris/Pressroom/blob/main/docs/development/api.md).
