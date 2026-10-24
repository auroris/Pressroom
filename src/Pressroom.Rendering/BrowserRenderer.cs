using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Pressroom.Contracts;
using UglyToad.PdfPig;

namespace Pressroom.Rendering;

/// <summary>One supervised browser with bounded, isolated render contexts.</summary>
public sealed class BrowserRenderer : IHtmlRenderer, IHostedService, IDisposable
{
    // This URL is fulfilled once from memory, never fetched. A response header makes
    // the policy effective before parsing and impossible for caller markup to remove.
    private const string DocumentUrl = "https://pressroom.invalid/document";
    private const string ResourcePolicy = "default-src data:; script-src data: 'unsafe-inline' 'unsafe-eval'; "
        + "style-src data: 'unsafe-inline'; connect-src data:; frame-src data:; "
        + "object-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'";
    private const string DocumentPolicy = ResourcePolicy + "; sandbox allow-scripts";

    private readonly RenderSettings _settings;
    private readonly PathsSettings _paths;
    private readonly FontsSettings _fontsSettings;
    private readonly ILogger<BrowserRenderer> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _restartSignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _state = new();
    private TaskCompletionSource<bool> _available = NewSignal();
    private TaskCompletionSource<bool> _drained = CompletedSignal();
    private Generation? _generation;
    private FontCatalog? _fonts;
    private Task? _supervisor;
    private bool _ready, _stopping, _restarting, _failedCleanup, _started;
    private int _active, _queued, _sinceRestart;
    private long _completed;
    private DateTimeOffset? _lastRestart;
    private string? _instanceDirectory, _nodePath;

    public BrowserRenderer(IOptions<RenderSettings> settings, IOptions<PathsSettings> paths,
        IOptions<FontsSettings> fonts, ILogger<BrowserRenderer> logger)
    {
        _settings = settings.Value;
        _settings.Validate();
        _paths = paths.Value;
        _fontsSettings = fonts.Value;
        _logger = logger;
        _slots = new SemaphoreSlim(_settings.Concurrency, _settings.Concurrency);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_started || _stopping)
                throw new InvalidOperationException("The browser renderer may be started only once.");
            _started = true;
            _restarting = true;
        }
        try
        {
            InitializePaths();
            _fonts = new FontCatalog(RuntimePaths.ResolveApplicationPath(_fontsSettings.Manifest));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            deadline.CancelAfter(_settings.MaxTimeoutMs);
            await StartGenerationAsync(deadline.Token);
            _supervisor = SuperviseAsync();
        }
        catch
        {
            lock (_state) { _ready = false; _restarting = false; _available.TrySetResult(false); }
            await StopGenerationAsync();
            if (!_failedCleanup) DeleteInstanceDirectory();
            throw;
        }
    }

    public RendererSnapshot GetSnapshot()
    {
        lock (_state)
            return new()
            {
                IsReady = _ready && !_stopping && _generation?.Browser.IsConnected == true,
                BrowserVersion = _generation?.Browser.Version,
                PlaywrightVersion = typeof(Playwright).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(Playwright).Assembly.GetName().Version?.ToString() ?? "unknown",
                Active = _active,
                Queued = _queued,
                Completed = _completed,
                LastRestartUtc = _lastRestart,
                Fonts = _fonts?.Fonts ?? []
            };
    }

    public async Task<RenderOutput> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options is null) throw new ArgumentException("Render options must be validated before rendering.", nameof(request));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var processingBudget = request.Options.TimeoutMs ?? _settings.DefaultTimeoutMs;
        if (processingBudget < 1 || processingBudget > _settings.MaxTimeoutMs)
            throw new ArgumentException("The render timeout is outside the configured range.", nameof(request));
        // The server supplies its earlier admission deadline. This additional bound
        // also protects explicit local use that supplies no cancellation token.
        linked.CancelAfter(processingBudget);
        var token = linked.Token;
        var slot = false;
        var active = false;
        var capacityReusable = true;
        Generation? generation = null;
        IBrowserContext? context = null;
        Task<IBrowserContext>? creatingContext = null;
        Task<RenderOutput>? rendering = null;
        CancellationTokenRegistration browserFailure = default;
        Interlocked.Increment(ref _queued);
        try
        {
            await _slots.WaitAsync(token);
            slot = true;
            generation = await AcquireGenerationAsync(token);
            active = true;
            Interlocked.Decrement(ref _queued);
            browserFailure = generation.FailureToken.Register(() => linked.Cancel());
            token.ThrowIfCancellationRequested();
            var options = request.Options;
            creatingContext = generation.Browser.NewContextAsync(new()
            {
                JavaScriptEnabled = options.JavaScript,
                Locale = options.Locale,
                TimezoneId = options.TimeZone,
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block
            });
            Observe(creatingContext);
            context = await creatingContext.WaitAsync(token);
            context.SetDefaultTimeout(0);
            context.SetDefaultNavigationTimeout(0);
            rendering = RenderDocumentAsync(context, request, token);
            Observe(rendering);
            return await rendering.WaitAsync(token);
        }
        catch (OperationCanceledException) when (generation?.FailureToken.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new RenderUnavailableException("The rendering browser became unavailable.");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RenderUnavailableException("Pressroom is shutting down.");
        }
        catch (PlaywrightException) when (generation?.FailureToken.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new RenderUnavailableException("The rendering browser became unavailable.");
        }
        catch (PlaywrightException) when (token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw;
        }
        catch (PlaywrightException)
        {
            if (generation?.Browser.IsConnected != true)
                throw new RenderUnavailableException("The rendering browser became unavailable.");
            // Do not carry Playwright's message or inner exception: it can contain caller HTML/URLs.
            throw new RenderDocumentException("Chromium could not load or print the document with the supplied options.");
        }
        finally
        {
            await browserFailure.DisposeAsync();
            if (!active) Interlocked.Decrement(ref _queued);
            if (generation is not null)
            {
                if (context is null && creatingContext is not null)
                {
                    try { context = await creatingContext.WaitAsync(TimeSpan.FromMilliseconds(_settings.CleanupTimeoutMs)); }
                    catch { capacityReusable = await TerminateFailedGenerationAsync(generation); }
                }
                if (context is not null)
                {
                    var closing = context.CloseAsync(new() { Reason = "Pressroom render finished." });
                    Observe(closing);
                    try { await closing.WaitAsync(TimeSpan.FromMilliseconds(_settings.CleanupTimeoutMs)); }
                    catch { capacityReusable = await TerminateFailedGenerationAsync(generation); }
                }
                // Observe pending protocol work after closure. A connection failure cannot leak
                // document diagnostics through an unobserved exception handler.
                if (rendering is not null) Observe(rendering);
            }
            if (active) CompleteAttempt();
            // Failed termination permanently takes this capacity out of circulation. The service
            // remains unhealthy instead of creating another browser beside a stuck old process.
            if (slot && capacityReusable) _slots.Release();
        }
    }

    private async Task<Generation> AcquireGenerationAsync(CancellationToken token)
    {
        while (true)
        {
            Task<bool> availability;
            lock (_state)
            {
                token.ThrowIfCancellationRequested();
                if (_stopping || _failedCleanup) throw new RenderUnavailableException("The rendering browser is unavailable.");
                if (_ready && _generation?.Browser.IsConnected == true)
                {
                    if (_active++ == 0) _drained = NewSignal();
                    return _generation;
                }
                if (!_restarting) throw new RenderUnavailableException("The rendering browser is unavailable.");
                availability = _available.Task;
            }
            if (!await availability.WaitAsync(token))
                throw new RenderUnavailableException("The rendering browser could not restart.");
        }
    }

    private async Task<RenderOutput> RenderDocumentAsync(IBrowserContext context, RenderRequest request, CancellationToken token,
        bool verifySandbox = false)
    {
        var blocked = 0;
        var pageError = 0;
        var documentNavigated = 0;
        var documentSupplied = false;
        IPage? documentPage = null;
        await context.SetOfflineAsync(true);
        await context.RouteAsync(new Regex(".*"), async route =>
        {
            if (!documentSupplied && route.Request.Url == DocumentUrl
                && route.Request.IsNavigationRequest && route.Request.Method == "GET"
                && route.Request.Frame == documentPage?.MainFrame)
            {
                documentSupplied = true;
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    ContentType = "text/html; charset=utf-8",
                    Body = request.Html,
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Security-Policy"] = DocumentPolicy,
                        // Covers connections outside Playwright routing, including
                        // WebRTC/STUN/TURN, resource hints, and WebTransport.
                        // A local ReportingObserver checks enforcement during warm-up.
                        // No Reporting-Endpoints header: reports never leave the browser.
                        ["Connection-Allowlist"] = "();webrtc=block;report-to=pressroom-sandbox",
                        ["X-DNS-Prefetch-Control"] = "off",
                        ["Referrer-Policy"] = "no-referrer"
                    }
                });
            }
            else if (route.Request.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) await route.ContinueAsync();
            else
            {
                Interlocked.Increment(ref blocked);
                await route.AbortAsync("blockedbyclient");
            }
        });
        await context.RouteWebSocketAsync(new Regex(".*"), socket =>
        {
            Interlocked.Increment(ref blocked);
            // Never call ConnectToServer: Playwright keeps this entirely in the context.
            Observe(socket.CloseAsync(new() { Code = 1008, Reason = "External connections are disabled." }));
        });
        context.Page += (_, page) => page.PageError += (_, _) => Interlocked.Exchange(ref pageError, 1);
        var page = documentPage = await context.NewPageAsync();
        // CSP rejects resources before routing and can omit RequestFailed events.
        // Consume only the issue classification; never retain document URLs/text.
        var audit = await context.NewCDPSessionAsync(page);
        audit.Event("Audits.issueAdded").OnEvent += (_, payload) =>
        {
            if (payload is { } data && data.TryGetProperty("issue", out var issue)
                && issue.GetProperty("code").GetString() == "ContentSecurityPolicyIssue"
                && issue.GetProperty("details").TryGetProperty("contentSecurityPolicyIssueDetails", out var details)
                && !details.GetProperty("isReportOnly").GetBoolean()
                && details.GetProperty("contentSecurityPolicyViolationType").GetString() == "kURLViolation")
                Interlocked.Increment(ref blocked);
        };
        await audit.SendAsync("Audits.enable");
        // Local-scheme navigations (notably blob:) bypass request interception.
        // They inherit our policies, but must never replace the submitted document.
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame && frame.Url != DocumentUrl
                && !frame.Url.StartsWith(DocumentUrl + "#", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref documentNavigated, 1);
                Observe(page.CloseAsync());
            }
        };
        var options = request.Options!;
        await page.EmulateMediaAsync(new() { Media = options.Media == "screen" ? Media.Screen : Media.Print });
        await page.GotoAsync(DocumentUrl, new() { WaitUntil = WaitUntilState.Load, Timeout = 0 });
        token.ThrowIfCancellationRequested();
        // The bootstrap navigation can replace Chromium's renderer process.
        // Reapply media to the committed document before layout/readiness checks.
        await page.EmulateMediaAsync(new() { Media = options.Media == "screen" ? Media.Screen : Media.Print });
        if (verifySandbox) await VerifySandboxAsync(page, token);
        // Install packaged fonts in the loaded document as data-backed CSS.
        await page.AddStyleTagAsync(new() { Content = _fonts!.Css });
        await WaitForReadinessAsync(page, options.Readiness!, token);
        await BoundAsync(page.EvaluateAsync("async () => { await document.fonts.ready; }"), token);
        if (Volatile.Read(ref pageError) != 0)
            throw new RenderDocumentException("The document raised a JavaScript exception before printing.");
        token.ThrowIfCancellationRequested();
        var pdf = await BoundAsync(page.PdfAsync(MapPdfOptions(options, _settings.DefaultPageSize)), token);
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref documentNavigated) != 0)
            throw new RenderDocumentException("The document navigated away from its isolated render context.");
        using var document = PdfDocument.Open(pdf);
        var pages = document.NumberOfPages;
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref pageError) != 0)
            throw new RenderDocumentException("The document raised a JavaScript exception while printing.");
        return new(pdf, pages, Volatile.Read(ref blocked));
    }

    private static async Task VerifySandboxAsync(IPage page, CancellationToken token)
    {
        // Constructor-only probe: never gather ICE candidates or configure an external
        // server. An ignored/unsupported header must fail startup, including overrides.
        var enforced = await BoundAsync(page.EvaluateAsync<bool>("""
            async () => {
                let observer, connection, timeout;
                try {
                    return await new Promise(resolve => {
                        timeout = setTimeout(() => resolve(false), 1000);
                        observer = new ReportingObserver(reports => {
                            if (reports.some(report => report.type === 'connection-allowlist'
                                && report.body.connection === 'webrtc' && report.body.disposition === 'enforce'
                                && report.body.allowlist.length === 0))
                                resolve(true);
                        }, { types: ['connection-allowlist'] });
                        observer.observe();
                        connection = new RTCPeerConnection();
                    });
                } catch { return false; }
                finally {
                    clearTimeout(timeout);
                    observer?.disconnect();
                    connection?.close();
                }
            }
            """), token);
        if (!enforced)
            throw new InvalidOperationException("The browser does not enforce the required network sandbox. Use the packaged Chromium build.");
    }

    private static async Task WaitForReadinessAsync(IPage page, ReadinessOptions readiness, CancellationToken token)
    {
        if (readiness.Selector is null && readiness.Function is null && readiness.DelayMs == 0) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(readiness.TimeoutMs);
        try
        {
            if (!string.IsNullOrEmpty(readiness.Selector))
                await BoundAsync(page.WaitForSelectorAsync(readiness.Selector, new() { State = WaitForSelectorState.Visible, Timeout = 0 }), budget.Token);
            if (!string.IsNullOrEmpty(readiness.Function))
                await BoundAsync(page.WaitForFunctionAsync(readiness.Function, options: new() { Timeout = 0 }), budget.Token);
            if (readiness.DelayMs > 0) await Task.Delay(readiness.DelayMs, budget.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new RenderDocumentException("The document did not meet all readiness conditions within its readiness budget.");
        }
    }

    public static PagePdfOptions MapPdfOptions(RenderOptions options, string defaultPageSize = "Letter")
    {
        var size = options.PageSize;
        var margins = options.Margins!;
        return new()
        {
            Format = size?.Name ?? (size?.Width is null ? defaultPageSize : null),
            Width = size?.Width,
            Height = size?.Height,
            Landscape = options.Landscape,
            Margin = new() { Top = margins.Top, Right = margins.Right, Bottom = margins.Bottom, Left = margins.Left },
            PreferCSSPageSize = options.PreferCssPageSize,
            PrintBackground = options.PrintBackground,
            Scale = (float)options.Scale,
            DisplayHeaderFooter = options.DisplayHeaderFooter,
            HeaderTemplate = ProtectPrintTemplate(options.HeaderTemplate),
            FooterTemplate = ProtectPrintTemplate(options.FooterTemplate),
            Tagged = options.Tagged,
            Outline = options.Outline
            // No Path: generated documents stay in the response buffer.
        };
    }

    private void CompleteAttempt()
    {
        var restart = false;
        lock (_state)
        {
            _completed++;
            _sinceRestart++;
            if (--_active == 0) _drained.TrySetResult(true);
            if (_sinceRestart >= _settings.RestartAfterRenders && !_restarting && !_stopping && !_failedCleanup)
            {
                MarkRestarting();
                restart = true;
            }
        }
        if (restart) SignalRestart();
    }

    private void RequestRecovery(Generation generation)
    {
        // Some in-flight protocol calls may never complete when the browser crashes.
        // A generation-wide signal actively cancels their waits and starts cleanup.
        generation.MarkFailed();
        lock (_state)
        {
            if (!ReferenceEquals(generation, _generation) || _stopping || _failedCleanup || _restarting) return;
            MarkRestarting();
        }
        SignalRestart();
    }

    private void MarkRestarting()
    {
        _ready = false;
        _restarting = true;
        if (_available.Task.IsCompleted) _available = NewSignal();
    }

    private void SignalRestart()
    {
        try { _restartSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task SuperviseAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _restartSignal.WaitAsync(_shutdown.Token);
                Task drained;
                lock (_state) drained = _drained.Task;
                await drained.WaitAsync(_shutdown.Token);
                if (!await StopGenerationAsync()) break;
                for (var attempt = 1; attempt <= 3 && !_shutdown.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                        startup.CancelAfter(_settings.MaxTimeoutMs);
                        await StartGenerationAsync(startup.Token);
                        break;
                    }
                    catch (Exception exception) when (!_shutdown.IsCancellationRequested)
                    {
                        // Exception text from a browser must never become document logs.
                        _logger.LogWarning("Browser recovery attempt {Attempt} failed ({FailureType}).", attempt, exception.GetType().Name);
                        if (!await StopGenerationAsync()) return;
                        if (attempt == 3)
                        {
                            lock (_state) { _restarting = false; _available.TrySetResult(false); }
                            _logger.LogError("Browser recovery exhausted its three attempts; Pressroom remains unavailable until restarted.");
                        }
                        else await Task.Delay(250 * attempt, _shutdown.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task StartGenerationAsync(CancellationToken token)
    {
        IPlaywright? playwright = null;
        OwnedDriverProcess? driver = null;
        IBrowser? browser = null;
        Generation? generation = null;
        Task<IPlaywright>? creatingDriver = null;
        var before = OwnedDriverProcess.SnapshotChildren();
        try
        {
            creatingDriver = Playwright.CreateAsync();
            playwright = await BoundAsync(creatingDriver, token);
            driver = OwnedDriverProcess.Capture(before, _nodePath!);
            browser = await BoundAsync(playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ChromiumSandbox = _settings.ChromiumSandbox,
                ExecutablePath = string.IsNullOrWhiteSpace(_settings.ChromiumPath) ? null : RuntimePaths.ResolveApplicationPath(_settings.ChromiumPath),
                Timeout = _settings.MaxTimeoutMs
                // No Channel: the packaged default is Chromium headless shell.
            }), token);
            driver.TrackBrowserChildren();
            generation = new Generation(playwright, browser, driver, TimeSpan.FromMilliseconds(_settings.CleanupTimeoutMs));
            lock (_state) _generation = generation;
            browser.Disconnected += (_, _) => RequestRecovery(generation);
            var context = await BoundAsync(browser.NewContextAsync(new() { AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block }), token);
            try
            {
                using var warmup = CancellationTokenSource.CreateLinkedTokenSource(token);
                warmup.CancelAfter(_settings.MaxTimeoutMs);
                var rendered = await RenderDocumentAsync(context, new RenderRequest
                {
                    Html = "<!doctype html><html lang='en'><head><meta charset='utf-8'><title>Pressroom warm-up</title></head><body style=\"font-family:'Noto Sans',sans-serif\"><h1>Pressroom</h1><p>Ready — prêt. Éé Àà Çç Œœ.</p></body></html>",
                    Options = new RenderOptions { DisplayHeaderFooter = false, JavaScript = false }
                }, warmup.Token, verifySandbox: true).WaitAsync(warmup.Token);
                if (rendered.PageCount != 1 || rendered.Pdf.Length < 100)
                    throw new InvalidOperationException("The browser warm-up did not produce a one-page PDF.");
            }
            finally
            {
                var closing = context.CloseAsync();
                Observe(closing);
                await closing.WaitAsync(TimeSpan.FromMilliseconds(_settings.CleanupTimeoutMs));
            }
            token.ThrowIfCancellationRequested();
            lock (_state)
            {
                if (_stopping) throw new OperationCanceledException(_shutdown.Token);
                if (!ReferenceEquals(generation, _generation) || !browser.IsConnected || generation.FailureToken.IsCancellationRequested)
                    throw new InvalidOperationException("The rendering browser disconnected during warm-up.");
                _sinceRestart = 0;
                _lastRestart = DateTimeOffset.UtcNow;
                _ready = true;
                _restarting = false;
                _available.TrySetResult(true);
            }
            _logger.LogInformation("Browser warm-up succeeded. Chromium {BrowserVersion}; concurrency {Concurrency}.", browser.Version, _settings.Concurrency);
        }
        catch
        {
            var stopped = true;
            if (generation is not null)
                stopped = await generation.StopAsync();
            else
            {
                if (driver is null)
                {
                    try { driver = OwnedDriverProcess.Capture(before, _nodePath!); }
                    catch (InvalidOperationException) { }
                }
                if (driver is not null)
                {
                    stopped = await driver.TerminateAsync(TimeSpan.FromMilliseconds(_settings.CleanupTimeoutMs));
                    driver.Dispose();
                }
                if (playwright is null && creatingDriver?.IsCompletedSuccessfully == true)
                    playwright = creatingDriver.Result;
                // CreateAsync has no cancellation-token parameter. If it outlives the
                // deadline, clean up its eventual result and prohibit competing startup.
                if (creatingDriver is not null && !creatingDriver.IsCompleted)
                {
                    stopped = false;
                    Observe(creatingDriver.ContinueWith(async completed =>
                    {
                        if (!completed.IsCompletedSuccessfully) return;
                        try
                        {
                            using var lateDriver = OwnedDriverProcess.Capture(before, _nodePath!);
                            await lateDriver.TerminateAsync(TimeSpan.FromMilliseconds(_settings.CleanupTimeoutMs));
                        }
                        catch (InvalidOperationException) { }
                        finally { completed.Result.Dispose(); }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap());
                }
                playwright?.Dispose();
            }
            lock (_state)
            {
                _generation = null;
                _ready = false;
                if (!stopped)
                {
                    _failedCleanup = true;
                    _restarting = false;
                    _available.TrySetResult(false);
                }
            }
            if (!stopped) _logger.LogCritical("Startup cleanup could not confirm termination. Pressroom will not start a replacement browser.");
            throw;
        }
    }

    private async Task<bool> TerminateFailedGenerationAsync(Generation generation)
    {
        RequestRecovery(generation);
        var stopped = await generation.StopAsync();
        if (!stopped)
        {
            lock (_state)
            {
                _ready = false;
                _failedCleanup = true;
                _restarting = false;
                _available.TrySetResult(false);
            }
            _logger.LogCritical("The owned browser process tree could not be terminated. No replacement browser will be started.");
        }
        return stopped;
    }

    private async Task<bool> StopGenerationAsync()
    {
        Generation? generation;
        lock (_state) { generation = _generation; _generation = null; _ready = false; }
        if (generation is null) return !_failedCleanup;
        return await TerminateFailedGenerationAsync(generation);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_settings.ShutdownTimeoutMs);
        Task drained;
        lock (_state)
        {
            _stopping = true;
            _ready = false;
            _available.TrySetResult(false);
            drained = _drained.Task;
        }
        var cancelling = _shutdown.CancelAsync();
        Observe(cancelling);
        // The one shutdown clock covers drain, browser close, process-tree termination
        // and the supervisor. Reserve cleanup time instead of spending it all draining.
        var drainBudget = Math.Max(0, _settings.ShutdownTimeoutMs - (2 * _settings.CleanupTimeoutMs) - 1_000);
        try
        {
            await cancelling.WaitAsync(budget.Token);
            try { await drained.WaitAsync(TimeSpan.FromMilliseconds(drainBudget), budget.Token); }
            catch (TimeoutException) { }
            var stopping = StopGenerationAsync();
            Observe(stopping);
            await stopping.WaitAsync(budget.Token);
            if (_supervisor is not null) await _supervisor.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_state) _failedCleanup = true;
            // Continue the already-started owned-process cleanup after the host deadline,
            // without deleting its working files or opening any replacement capacity.
            var stopping = StopGenerationAsync();
            Observe(stopping);
            _logger.LogWarning("The total shutdown budget expired before cleanup could be confirmed.");
        }
        if (!_failedCleanup) DeleteInstanceDirectory();
    }

    private void InitializePaths()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Pressroom browser package requires an x64 process.");
        var stateRoot = RuntimePaths.ResolveStateRoot(_paths);
        var release = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((stateRoot + Path.DirectorySeparatorChar).StartsWith(release,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Paths:StateRoot must be outside the replaceable release directory.");
        _instanceDirectory = Path.Combine(stateRoot, "temp", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_instanceDirectory);
        // Test actual read/write/delete permissions before creating any browser process.
        var probe = Path.Combine(_instanceDirectory, "write-probe");
        File.WriteAllText(probe, "Pressroom");
        File.Delete(probe);
        var browsers = RuntimePaths.ResolveApplicationPath(_settings.BrowsersPath);
        if (string.IsNullOrWhiteSpace(_settings.ChromiumPath) && !Directory.Exists(browsers))
            throw new InvalidOperationException("The packaged browsers directory is missing. Publish Pressroom with its Visual Studio IIS folder profile to include Chromium.");
        var nodeRelative = OperatingSystem.IsWindows() ? ".playwright/node/win32_x64/node.exe" : ".playwright/node/linux-x64/node";
        _nodePath = RuntimePaths.ResolveApplicationPath(nodeRelative);
        if (!File.Exists(_nodePath) || !File.Exists(RuntimePaths.ResolveApplicationPath(".playwright/package/cli.js")))
            throw new InvalidOperationException("The private Playwright driver is missing. Copy the complete published directory, including .playwright.");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsers);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH", _nodePath);
        Environment.SetEnvironmentVariable("TEMP", _instanceDirectory);
        Environment.SetEnvironmentVariable("TMP", _instanceDirectory);
        Environment.SetEnvironmentVariable("TMPDIR", _instanceDirectory);
    }

    private void DeleteInstanceDirectory()
    {
        if (_instanceDirectory is null) return;
        try { Directory.Delete(_instanceDirectory, recursive: true); }
        catch (IOException) { _logger.LogWarning("This instance's temporary directory could not yet be removed."); }
        catch (UnauthorizedAccessException) { _logger.LogWarning("This instance's temporary directory could not be removed with the current permissions."); }
    }

    // Chromium prints headers/footers in separate documents. Their parser must see
    // the resource policy before any caller-supplied markup, too.
    private static string? ProtectPrintTemplate(string? template) => template is null ? null
        : $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ResourcePolicy}\">{template}";

    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task BoundAsync(Task task, CancellationToken token) { Observe(task); await task.WaitAsync(token); }
    private static async Task<T> BoundAsync<T>(Task<T> task, CancellationToken token) { Observe(task); return await task.WaitAsync(token); }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<bool> CompletedSignal() { var result = NewSignal(); result.SetResult(true); return result; }

    public void Dispose()
    {
        // The host calls StopAsync first. Avoid disposing semaphores underneath late HTTP continuations.
        if (_supervisor is null || _supervisor.IsCompleted) _shutdown.Dispose();
        else Observe(_supervisor.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default));
    }

    private sealed class Generation(IPlaywright playwright, IBrowser browser, OwnedDriverProcess driver, TimeSpan timeout)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _failed = new();
        private Task<bool>? _stop;
        public IBrowser Browser { get; } = browser;
        public CancellationToken FailureToken => _failed.Token;

        public void MarkFailed() => _failed.Cancel();

        public Task<bool> StopAsync()
        {
            lock (_sync) return _stop ??= StopCoreAsync();
        }

        private async Task<bool> StopCoreAsync()
        {
            try
            {
                var closing = Browser.CloseAsync();
                Observe(closing);
                await closing.WaitAsync(timeout);
            }
            catch { /* The owned OS process tree below is the bounded fallback. */ }
            var stopped = await driver.TerminateAsync(timeout);
            playwright.Dispose();
            driver.Dispose();
            return stopped;
        }
    }
}
