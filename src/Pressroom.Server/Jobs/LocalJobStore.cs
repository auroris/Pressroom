using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pressroom.Contracts;
using Pressroom.Rendering;
using Pressroom.Server.Infrastructure;

namespace Pressroom.Server.Jobs;

/// <summary>Single-instance durable store. All transitions and quota checks share one lock.</summary>
public sealed class LocalJobStore(IOptions<JobsSettings> configured, IOptions<PathsSettings> paths, TimeProvider time,
    ILogger<LocalJobStore> logger) : IJobStore, IDisposable
{
    private const long MetadataAllowance = 16 * 1024;
    private readonly JobsSettings _settings = configured.Value;
    private readonly string _root = Path.Combine(ServerConfiguration.ResolveStateRoot(paths.Value), "jobs");
    private readonly object _sync = new();
    private readonly Dictionary<string, StoredJob> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _downloads = new(StringComparer.Ordinal);
    private FileStream? _instanceLock;
    private long _nextSequence;
    private DateTimeOffset Now => time.GetUtcNow();
    private long Used => _jobs.Values.Sum(job => MetadataAllowance + job.RequestBytes + job.ResultBytes);
    private string FilePath(string id, string name) => Path.Combine(_root, id, name);

    public void Initialize()
    {
        lock (_sync)
        {
            if (!_settings.Enabled || _instanceLock is not null) return;
            PrivateFiles.Directory(_root);
            var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            _instanceLock = new FileStream(Path.Combine(_root, ".instance.lock"), lockOptions);
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Job storage contains a symbolic link or junction.");
                var id = Path.GetFileName(directory);
                if (id.StartsWith(".pending-", StringComparison.Ordinal)) { Directory.Delete(directory, true); continue; }
                if (!JobValidation.IsId(id)) continue;
                PrivateFiles.Directory(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Job storage contains a symbolic link or junction.");
                foreach (var temporary in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(temporary);
                var stored = JsonSerializer.Deserialize<StoredJob>(File.ReadAllBytes(FilePath(id, "job.json")), JsonDefaults.Options)
                    ?? throw new InvalidOperationException("Job metadata is missing or corrupt.");
                if (stored.Job.Id != id || stored.SchemaVersion != 1) throw new InvalidOperationException("Job metadata has an unsupported schema or does not match its storage directory.");
                _jobs.Add(id, stored with { RequestBytes = Length(id, "request.json"), ResultBytes = Length(id, "result.pdf") });
                _nextSequence = Math.Max(_nextSequence, stored.Sequence);
                if (stored.Job.Status == JobStatus.Rendering)
                    Fail(id, new(503, "Render interrupted", stored.Job.Printer is null ? "The service stopped during this job."
                        : "The service stopped during a print job. Check the print queue before resubmitting.", "interrupted", stored.Job.Printer is null));
                else if (stored.Job.Status == JobStatus.Queued && !File.Exists(FilePath(id, "request.json")))
                    throw new InvalidOperationException("An acknowledged queued job has no stored request.");
                else if (stored.Job.Status == JobStatus.Succeeded)
                    RemoveFile(id, "request.json");
            }
            Sweep();
        }
    }

    public RenderJob Submit(string owner, RenderRequest request, string? idempotencyKey)
    {
        lock (_sync)
        {
            Sweep();
            var hash = idempotencyKey is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));
            var existing = hash is null ? null : _jobs.Values.FirstOrDefault(item => item.Owner == owner && item.IdempotencyHash == hash);
            if (existing is not null) return View(existing, QueuePositions());
            var queued = _jobs.Values.Where(item => item.Job.Status == JobStatus.Queued).ToArray();
            if (queued.Length >= _settings.MaxQueued || queued.Count(item => item.Owner == owner) >= _settings.MaxQueuedPerKey)
                throw new JobCapacityException();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonDefaults.Options);
            if (Used + bytes.Length + MetadataAllowance > _settings.MaxStoredBytes) throw new JobCapacityException();
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var job = new RenderJob
            {
                Id = id, Status = JobStatus.Queued, CreatedUtc = Now, ExpiresUtc = Now + _settings.MaxQueueAge,
                FileName = RequestValidator.SanitizeFileName(request.FileName), Disposition = request.Disposition, Printer = request.Printer,
                Links = new($"/v1/jobs/{id}", $"/v1/jobs/{id}/result")
            };
            var stored = new StoredJob(job, owner, hash, bytes.Length, Sequence: ++_nextSequence);
            var pending = Path.Combine(_root, ".pending-" + id);
            PrivateFiles.Directory(pending);
            try
            {
                PrivateFiles.Write(Path.Combine(pending, "request.json"), bytes);
                PrivateFiles.Write(Path.Combine(pending, "job.json"), JsonSerializer.SerializeToUtf8Bytes(stored, JsonDefaults.Options));
                Directory.Move(pending, Path.Combine(_root, id));
                PrivateFiles.SyncDirectory(_root);
                _jobs.Add(id, stored);
            }
            finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
            return View(stored, QueuePositions());
        }
    }

    public RenderJob? Get(string owner, string id)
    {
        lock (_sync)
        {
            Sweep();
            return _jobs.TryGetValue(id, out var job) && job.Owner == owner ? View(job, QueuePositions()) : null;
        }
    }

    // Positions come from one ordering per operation. Ranking each rendered job separately made a
    // List over a large queue quadratic while holding the lock the scheduler also needs.
    private Dictionary<string, int> QueuePositions() => Queued().Select((job, index) => (job.Job.Id, Position: index + 1))
        .ToDictionary(pair => pair.Id, pair => pair.Position, StringComparer.Ordinal);

    private static RenderJob View(StoredJob stored, IReadOnlyDictionary<string, int> positions) => stored.Job with
    {
        QueuePosition = stored.Job.Status == JobStatus.Queued ? positions[stored.Job.Id] : null
    };

    public RenderJobPage List(string owner, JobStatus? status, int limit, string? after)
    {
        lock (_sync)
        {
            Sweep();
            var all = _jobs.Values.Where(item => item.Owner == owner).OrderByDescending(item => item.Job.CreatedUtc)
                .ThenByDescending(item => item.Job.Id, StringComparer.Ordinal).ToArray();
            IEnumerable<StoredJob> page = all;
            if (after is not null)
            {
                var index = Array.FindIndex(all, item => item.Job.Id == after);
                if (index < 0) return new([], null);
                page = all.Skip(index + 1);
            }
            var selected = page.Where(item => status is null || item.Job.Status == status).Take(limit + 1).ToArray();
            var positions = QueuePositions();
            return new(selected.Take(limit).Select(item => View(item, positions)).ToArray(), selected.Length > limit ? selected[limit - 1].Job.Id : null);
        }
    }

    public IReadOnlyList<StoredJob> Queued()
    {
        lock (_sync) return _jobs.Values.Where(item => item.Job.Status == JobStatus.Queued)
            .OrderBy(item => item.Sequence).ToArray();
    }

    public RenderRequest? ReadRequest(string id)
    {
        lock (_sync) return _jobs.TryGetValue(id, out var job) && job.Job.Status == JobStatus.Queued
            ? JsonSerializer.Deserialize<RenderRequest>(File.ReadAllBytes(FilePath(id, "request.json")), JsonDefaults.Options) : null;
    }

    public bool Start(string id)
    {
        lock (_sync)
        {
            Sweep();
            if (!_jobs.TryGetValue(id, out var stored) || stored.Job.Status != JobStatus.Queued) return false;
            Save(stored with { Job = stored.Job with { Status = JobStatus.Rendering, StartedUtc = Now, ExpiresUtc = Now + _settings.ResultRetention } });
            return true;
        }
    }

    public void Succeed(string id, RenderOutput output, long durationMs, PrintJobResult? printJob)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var stored) || stored.Job.Status != JobStatus.Rendering) return;
            if (printJob is null)
            {
                if (Used + output.Pdf.Length > _settings.MaxStoredBytes)
                {
                    Fail(id, new(429, "Job storage full", "The result exceeds the available storage budget.", "storage_full", true));
                    return;
                }
                PrivateFiles.Write(FilePath(id, "result.pdf"), output.Pdf);
            }
            // Commit terminal metadata before removing the request. Recovery never rerenders a success.
            Save(stored with
            {
                Job = stored.Job with { Status = JobStatus.Succeeded, CompletedUtc = Now, ExpiresUtc = Now + _settings.ResultRetention,
                    PageCount = output.PageCount, DurationMs = durationMs, PrintJob = printJob },
                ResultBytes = printJob is null ? output.Pdf.Length : 0
            });
            RemoveFile(id, "request.json");
        }
    }

    public void Fail(string id, JobError error)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var stored) || stored.Job.IsTerminal) return;
            Save(stored with { Job = stored.Job with { Status = JobStatus.Failed, Error = error, CompletedUtc = Now,
                    ExpiresUtc = Now + _settings.ResultRetention }, RequestExpiresUtc = Now + _settings.RequestRetention });
            RemoveFile(id, "result.pdf");
        }
    }

    public bool Cancel(string owner, string id)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var stored) || stored.Owner != owner) return false;
            if (stored.Job.Status is JobStatus.Cancelled or JobStatus.Expired) return true;
            Save(stored with { Job = stored.Job with { Status = JobStatus.Cancelled, CompletedUtc = Now, ExpiresUtc = Now + _settings.ResultRetention,
                Error = null }, RequestExpiresUtc = null });
            RemoveFile(id, "request.json");
            if (!_downloads.Contains(id)) RemoveFile(id, "result.pdf");
            return true;
        }
    }

    public JobDownload? OpenResult(string owner, string id)
    {
        lock (_sync)
        {
            Sweep();
            if (!_jobs.TryGetValue(id, out var stored) || stored.Owner != owner || stored.Job.Status != JobStatus.Succeeded
                || stored.Job.Printer is not null || !_downloads.Add(id)) return null;
            try
            {
                var stream = new FileStream(FilePath(id, "result.pdf"), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                return new(stored.Job, stream, () => ReleaseDownload(id));
            }
            catch { _downloads.Remove(id); throw; }
        }
    }

    private void ReleaseDownload(string id)
    {
        lock (_sync)
        {
            _downloads.Remove(id);
            if (_jobs.TryGetValue(id, out var job) && job.Job.Status is JobStatus.Expired or JobStatus.Cancelled) RemoveFile(id, "result.pdf");
        }
    }

    public void CompleteDownload(string id)
    {
        lock (_sync)
            if (_settings.DeleteResultOnDownload && _jobs.TryGetValue(id, out var stored) && stored.Job.Status == JobStatus.Succeeded)
                Expire(stored, "downloaded", "The result was deleted after download.");
    }

    public void Sweep()
    {
        lock (_sync)
            foreach (var stored in _jobs.Values.ToArray())
            {
                var id = stored.Job.Id;
                // Sweeping runs inside every jobs operation. One job's files held open elsewhere (a
                // transient Windows sharing violation, a backup or scanner) must not fail them all:
                // leave that job as it is for the next sweep and carry on with the others.
                try
                {
                    if (stored.RemoveUtc <= Now && !_downloads.Contains(id))
                    {
                        DeleteJobDirectory(id);
                        _jobs.Remove(id);
                    }
                    else if (stored.Job.Status != JobStatus.Rendering && stored.Job.Status != JobStatus.Expired && stored.Job.ExpiresUtc <= Now)
                        Expire(stored, stored.Job.Status == JobStatus.Queued ? "queue_expired" : "expired", "The job exceeded its configured retention or queue age.");
                    else if (stored.RequestExpiresUtc <= Now) RemoveFile(id, "request.json");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Metadata only: no exception message or storage path reaches the log.
                    logger.LogWarning("Job {JobId} storage cleanup failed with {ExceptionType}; retrying on a later sweep", id, exception.GetType().Name);
                }
            }
    }

    private void Expire(StoredJob stored, string code, string detail)
    {
        Save(stored with { Job = stored.Job with { Status = JobStatus.Expired, CompletedUtc = stored.Job.CompletedUtc ?? Now,
            Error = new(410, "Job expired", detail, code, false), ExpiresUtc = Now }, RemoveUtc = Now + _settings.ResultRetention });
        RemoveFile(stored.Job.Id, "request.json");
        if (!_downloads.Contains(stored.Job.Id)) RemoveFile(stored.Job.Id, "result.pdf");
    }

    private void DeleteJobDirectory(string id)
    {
        // Metadata goes last. If removal stops partway, the surviving job.json still describes an
        // expired tombstone, so a restart recovers and retries it rather than finding a job directory
        // without metadata, which Initialize rejects as corrupt storage.
        var directory = Path.Combine(_root, id);
        if (!Directory.Exists(directory)) return; // Already removed, for example by an earlier partial attempt.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Path.GetFileName(entry) == "job.json") continue;
            // Recursive deletion removes links themselves and never follows them.
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        File.Delete(Path.Combine(directory, "job.json"));
        Directory.Delete(directory, recursive: false);
    }

    private long Length(string id, string name) => File.Exists(FilePath(id, name)) ? new FileInfo(FilePath(id, name)).Length : 0;
    private void RemoveFile(string id, string name)
    {
        File.Delete(FilePath(id, name));
        var stored = _jobs[id];
        _jobs[id] = name == "request.json" ? stored with { RequestBytes = 0, RequestExpiresUtc = null } : stored with { ResultBytes = 0 };
    }
    private void Save(StoredJob stored)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored, JsonDefaults.Options);
        if (bytes.Length * 2 > MetadataAllowance) throw new InvalidOperationException("Job metadata exceeded its reserved storage allowance.");
        PrivateFiles.Write(FilePath(stored.Job.Id, "job.json"), bytes);
        _jobs[stored.Job.Id] = stored;
    }

    public JobCapacity Capacity
    {
        get
        {
            lock (_sync) return new(_settings.Enabled, _jobs.Values.Count(item => item.Job.Status == JobStatus.Queued),
                _jobs.Values.Count(item => item.Job.Status == JobStatus.Rendering), _jobs.Count, Used, _settings.Workers, _settings.MaxQueued);
        }
    }

    public void Dispose() => _instanceLock?.Dispose();
}
