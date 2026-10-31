using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Printing;

namespace Pressroom.Server.Jobs;

public sealed class JobScheduler(IJobStore store, RenderCoordinator renderer, IPrintService printing,
    IOptions<JobsSettings> configured, ILogger<JobScheduler> logger) : BackgroundService
{
    private readonly JobsSettings _settings = configured.Value;
    private readonly ConcurrentDictionary<string, Execution> _running = new(StringComparer.Ordinal);
    private string? _lastOwner;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_settings.Enabled) store.Initialize();
        return base.StartAsync(cancellationToken);
    }

    public void Cancel(string id)
    {
        if (_running.TryGetValue(id, out var execution))
            lock (execution) { if (!execution.Finished) execution.Cancellation.Cancel(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled) return;
        var sweep = Stopwatch.StartNew();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var completed in _running.Where(pair => pair.Value.Task.IsCompleted).ToArray())
                {
                    await completed.Value.Task; // A storage failure stops the host; never lose it in an unobserved task.
                    _running.TryRemove(completed.Key, out _);
                }
                if (sweep.Elapsed >= TimeSpan.FromMinutes(1)) { store.Sweep(); sweep.Restart(); }
                while (_running.Count < _settings.Workers && !stoppingToken.IsCancellationRequested)
                {
                    var queued = store.Queued();
                    if (queued.Count == 0) break;
                    var owners = queued.Select(item => item.Owner).Distinct(StringComparer.Ordinal).ToArray();
                    var owner = owners[(Array.IndexOf(owners, _lastOwner) + 1) % owners.Length];
                    var next = queued.First(item => item.Owner == owner);
                    var slot = renderer.TryReserveBackground(_settings.ReservedSyncSlots);
                    if (slot is null) break;
                    try
                    {
                        // Do not reload a potentially large HTML payload while sync work owns capacity.
                        var request = store.ReadRequest(next.Job.Id);
                        if (request is null || !store.Start(next.Job.Id)) { slot.Dispose(); continue; }
                        var execution = new Execution(CancellationTokenSource.CreateLinkedTokenSource(stoppingToken), slot);
                        _running[next.Job.Id] = execution;
                        _lastOwner = owner;
                        execution.Task = Task.Run(() => RunAsync(next, request, execution, stoppingToken), CancellationToken.None);
                    }
                    catch { slot.Dispose(); throw; }
                }
                await Task.Delay(25, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // Shutdown preserves queued work; active work records interrupted and releases contexts.
            await Task.WhenAll(_running.Values.Select(item => item.Task));
        }
    }

    private async Task RunAsync(StoredJob stored, RenderRequest request, Execution execution, CancellationToken stoppingToken)
    {
        var id = stored.Job.Id;
        var clock = Stopwatch.StartNew();
        var printingStarted = false;
        try
        {
            if (store.Get(stored.Owner, id)?.Status != JobStatus.Rendering) return;
            execution.Cancellation.CancelAfter(request.Options!.TimeoutMs!.Value);
            var token = execution.Cancellation.Token;
            var output = await renderer.RenderReservedAsync(request, token);
            token.ThrowIfCancellationRequested();
            PrintJobResult? receipt = null;
            if (request.Printer is { } printer)
            {
                printingStarted = true;
                receipt = await printing.PrintAsync(printer, output, token);
            }
            token.ThrowIfCancellationRequested();
            store.Succeed(id, output, (long)clock.Elapsed.TotalMilliseconds, receipt);
        }
        catch (OperationCanceledException)
        {
            store.Fail(id, stoppingToken.IsCancellationRequested
                ? new(503, "Render interrupted", printingStarted ? "Check the print queue before resubmitting." : "The service stopped during rendering.", "interrupted", !printingStarted)
                : new(504, "Render deadline expired", printingStarted ? "Check the print queue before resubmitting." : "The render exceeded its processing timeout.", "deadline", !printingStarted));
        }
        catch (RenderUnavailableException) { store.Fail(id, new(503, "Renderer unavailable", "The rendering browser restarted or stopped.", "renderer_unavailable", true)); }
        catch (RenderDocumentException) { store.Fail(id, new(422, "Document could not be rendered", "A readiness condition or document operation failed.", "document", false)); }
        catch (PrintUnavailableException) { store.Fail(id, new(503, "Printing unavailable", "Check the printer configuration and service identity.", "printing_unavailable", true)); }
        catch (PrintSubmissionException) { store.Fail(id, new(502, "Print submission failed", "Check the print queue before resubmitting; pages may already have printed.", "print_submission", false)); }
        catch (Exception exception)
        {
            logger.LogError("Job {JobId} failed with {ExceptionType}", id, exception.GetType().Name);
            store.Fail(id, new(500, "Job failed", "The job could not be completed.", "internal", false));
        }
        finally
        {
            execution.Slot.Dispose();
            lock (execution) { execution.Finished = true; execution.Cancellation.Dispose(); }
            logger.LogInformation("Job {JobId} processing finished in {DurationMs} ms", id, (long)clock.Elapsed.TotalMilliseconds);
        }
    }

    private sealed class Execution(CancellationTokenSource cancellation, IDisposable slot)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IDisposable Slot { get; } = slot;
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Finished { get; set; }
    }
}
