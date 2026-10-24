# Third-party components

Pressroom uses the following separately licensed components. This file is an
index, not a replacement for their licenses. The exact resolved versions are in
the committed `packages.lock.json` files and each published `release-manifest.json`.

| Component | Upstream and license information | Files retained in the release |
|---|---|---|
| Microsoft.Playwright and its JavaScript driver | [microsoft/playwright-dotnet](https://github.com/microsoft/playwright-dotnet), [microsoft/playwright](https://github.com/microsoft/playwright); Apache-2.0 | `.playwright/package/LICENSE`, driver third-party notices, NuGet metadata/notices |
| Private Node.js runtime | [nodejs/node](https://github.com/nodejs/node); MIT plus bundled dependency licenses | `.playwright/node/LICENSE` |
| Chromium headless shell | [Chromium](https://www.chromium.org/); BSD-style and component-specific licenses | Entire installer-provided browser tree, including license/credit resources |
| Playwright FFmpeg and Windows dependency scanner | [Playwright browser patches](https://github.com/microsoft/playwright/tree/main/browser_patches); component-specific licenses | Entire installer-provided companion trees and their notices |
| Noto Sans Regular, Bold, Italic, Bold Italic | [notofonts/noto-fonts](https://github.com/notofonts/noto-fonts); SIL Open Font License 1.1 | `fonts/OFL.txt`, font files, and source/hash manifest |
| PdfPig | [UglyToad/PdfPig](https://github.com/UglyToad/PdfPig); Apache-2.0 | NuGet metadata and supplied license/notices |
| AngleSharp | [AngleSharp/AngleSharp](https://github.com/AngleSharp/AngleSharp); MIT | NuGet metadata and supplied license/notices |
| Serilog and file/ASP.NET Core integrations | [Serilog](https://github.com/serilog); Apache-2.0 | NuGet metadata and supplied license/notices |
| .NET, ASP.NET Core, and Microsoft.Extensions dependencies | [dotnet](https://github.com/dotnet); MIT and component-specific notices | NuGet metadata/notices; runtime notices when publishing self-contained |

Publish copies each resolved package's NuGet specification, declared license file,
and available LICENSE/NOTICE/third-party files into `notices/packages/`, and writes
the package license declarations to `notices/packages.json`. Browser installation
uses the pinned Playwright package's own private driver. Its resources and
companion tools are preserved rather than reconstructed from arbitrary files.

No separately copied Visual C++ Redistributable or Media Foundation DLL is part
of the initial source distribution. If a later qualified renderer requires an
app-local redistributable, obtain it from Microsoft's permitted distribution,
preserve its license/notice files, and add its version and hashes to the package.

Development test dependencies are recorded in their own lock files and are not
published with the server. Pressroom's own source licensing is left to the owner;
this notice does not grant a new license to the Pressroom application.
