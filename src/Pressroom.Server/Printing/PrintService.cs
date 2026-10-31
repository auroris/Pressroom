using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Infrastructure;

namespace Pressroom.Server.Printing;

public interface IPrintService
{
    IReadOnlyCollection<string> Printers { get; }
    Task<PrintJobResult> PrintAsync(string printer, RenderOutput document, CancellationToken cancellationToken);
}

public sealed class PrintUnavailableException : Exception;
public sealed class PrintSubmissionException : Exception;

public sealed class PrintService : IPrintService
{
    private readonly PrintingSettings _settings;
    private readonly string _stateRoot;
    private readonly IPrintProcessRunner _runner;
    private readonly ILogger<PrintService> _logger;

    public PrintService(IOptions<PrintingSettings> settings, IOptions<PathsSettings> paths,
        IPrintProcessRunner runner, ILogger<PrintService> logger)
    {
        settings.Value.Validate();
        _settings = settings.Value;
        _stateRoot = ServerConfiguration.ResolveStateRoot(paths.Value);
        _runner = runner;
        _logger = logger;
        Printers = Array.AsReadOnly(_settings.Printers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public IReadOnlyCollection<string> Printers { get; }

    public async Task<PrintJobResult> PrintAsync(string printer, RenderOutput document, CancellationToken cancellationToken)
    {
        // Resolve only from configuration. A request must never supply a queue name or command.
        var identifier = Printers.FirstOrDefault(id => id.Equals(printer, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The printer is not configured.", nameof(printer));
        var jobId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_stateRoot, "temp", $"print-{jobId}");
        var file = Path.Combine(directory, $"{jobId}.pdf");
        var profile = Path.Combine(directory, "profile");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsLinux())
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else
                Directory.CreateDirectory(profile);
            await File.WriteAllBytesAsync(file, document.Pdf, cancellationToken);
            var start = new ProcessStartInfo(OperatingSystem.IsLinux() ? _settings.CupsCommandPath : _settings.SumatraPdfPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // A virtual printer's Save As dialog must not default to a disposable job folder.
                WorkingDirectory = _stateRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (OperatingSystem.IsLinux())
                CupsPrintCommand.AddArguments(start, _settings.Printers[identifier], file);
            else
            {
                // A private profile avoids shared settings, remembered documents, and existing UI instances.
                foreach (var argument in new[] { "-appdata", profile, "-silent", "-print-to", _settings.Printers[identifier], file })
                    start.ArgumentList.Add(argument);
            }
            var exitCode = await _runner.RunAsync(start, cancellationToken);
            if (exitCode != 0)
            {
                _logger.LogWarning("Print job {JobId} failed with exit code {ExitCode}", jobId, exitCode);
                throw new PrintSubmissionException();
            }
            // Once submission succeeds, cancellation cannot safely retract a spooled job.
            _logger.LogInformation("Print job {JobId} submitted to {Printer}", jobId, identifier);
            return new PrintJobResult(jobId, identifier, "submitted", document.PageCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Print job {JobId} failed with {ExceptionType}", jobId, exception.GetType().Name);
            throw new PrintUnavailableException();
        }
        finally
        {
            try
            {
                File.Delete(file);
                if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                // Preserve unexpected files, including a PDF manually saved by a virtual printer.
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Do not turn an accepted print into a failure that encourages a duplicate retry.
                _logger.LogWarning("Print job {JobId} temporary cleanup failed with {ExceptionType}", jobId, exception.GetType().Name);
            }
        }
    }
}

public interface IPrintProcessRunner
{
    Task<int> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken);
}

public sealed class PrintProcessRunner : IPrintProcessRunner
{
    public async Task<int> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || !File.Exists(start.FileName))
            throw new PrintUnavailableException();
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new PrintUnavailableException();
        }
        catch (Win32Exception) { throw new PrintUnavailableException(); }

        // Drain without buffering or logging document-dependent diagnostics.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                // Keep the admission slot until the helper has stopped using its PDF.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }
}
