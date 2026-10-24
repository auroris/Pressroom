# Source delivery verification

Verification was performed in a Linux workspace with a locally installed .NET 10
SDK. The original Windows repository was not mounted or modified. Pressroom was
created as a separate solution.

## Checks performed

- Release compilation succeeds with zero warnings or errors. The consolidated
  suite passes **83 tests, zero failed and zero skipped**: 45 contract/client tests,
  24 HTTP API tests, and 14 renderer tests (including real Chromium). The API tests use controlled renderer substitutes for authentication,
  admission, body limits, and error handling; real renderer tests exercise Chromium.
- Both Windows x64 publish profiles, framework-dependent and self-contained,
  have passed cross-compilation with locked dependency restore. The generated IIS
  entry point uses `Pressroom.Server.exe` with empty arguments; the private Windows
  driver, fonts, and selected runtime payload were verified. This verifies compilation and payload selection, not Windows
  executable behavior or IIS operation.
- The automatic publish helper was parsed with Microsoft's PowerShell parser with
  zero syntax errors. Both publish profiles, the MSBuild target file, `web.config`,
  and the sample JSON files were parsed successfully.
- A scratch MSBuild harness imported the actual publish targets and simulated only
  the external helper. It confirmed that application, private `.playwright` driver,
  browser, and generated manifest enter the publish file list. It also confirmed
  that a failed package verification prevents destination transfer. This is a
  publish-ordering test, not a Windows browser test.
- The packaged Noto Sans faces were exercised in real main-document PDFs. A
  separate footer experiment fell back to a system font despite a data-backed
  Noto rule; custom embedded header/footer fonts are therefore unsupported in this
  initial implementation.

## Response disposition checks

The consolidated passing suite verifies explicit inline and attachment responses,
the attachment default, sanitized Unicode filenames, PDF content type, and 400
responses for invalid disposition values/types. The HTTP client serializes the
choice and preserves the response disposition in buffered and streaming results.

An additional real HTTP smoke render returned a two-page bilingual PDF through the
compiled service, with readiness, authenticated info, and OpenAPI returning 200.
Invalid and missing API keys returned 401 without a Negotiate challenge in API-key
mode. Actual browser navigation and real IIS authentication remain Windows checks.

## Toolchain

- .NET SDK: 10.0.402; target framework: net10.0.
- Application version: 0.1.0.
- Microsoft.Playwright: 1.64.1; Chromium headless shell: 156.0.8078.4.
- Self-contained cross-publish includes .NET and ASP.NET Core 10.0.13.
- Locked restore was verified for normal solution development and both publish profiles.

## Browser provenance and limits

The Playwright browser CDN download failed in this environment. Real Linux browser
tests used Google's official Chrome for Testing headless shell at the exact selected
version through a test-only executable override. The root-container Linux tests
explicitly disabled the Chromium sandbox. Production defaults and the Windows
publish check retain sandboxing and normal host validation.

Visual Studio Publish must acquire the pinned Playwright package's browser payload
and pass its real Windows `--check`; there is no silent download-source or system-
browser fallback. Cross-publishing here explicitly used `PressroomBundleBrowser=false`
to inspect the Windows managed/native host output. Those temporary outputs are not
the qualified, complete IIS release and are not the delivered source ZIP.

No Windows executable, IIS app-pool identity, Windows authentication exchange,
Windows Job Object/nested-job execution, or Visual Studio UI was run here. The
clean-server Media Foundation/VC-runtime objective and full publish/IIS workflow
remain the checks in `windows-acceptance.md`.
