using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pressroom.Rendering;

/// <summary>
/// Identifies only the private Node process created by this renderer. On a failed protocol
/// shutdown, this gives us an OS handle to terminate its own descendants rather than
/// searching for, or killing, unrelated Chromium processes.
/// </summary>
internal sealed class OwnedDriverProcess : IDisposable
{
    private readonly Process _process;
    private readonly DateTime _startTime;
    private readonly List<(Process Process, DateTime Started)> _browserRoots = [];
    private readonly WindowsProcessJob? _job;

    private OwnedDriverProcess(Process process)
    {
        _process = process;
        _startTime = process.StartTime;
        if (OperatingSystem.IsWindows()) _job = new WindowsProcessJob(process);
    }

    public static HashSet<int> SnapshotChildren() => ReadParents()
        .Where(pair => pair.Value == Environment.ProcessId).Select(pair => pair.Key).ToHashSet();

    public static OwnedDriverProcess Capture(HashSet<int> before, string executablePath)
    {
        foreach (var id in SnapshotChildren().Except(before))
        {
            Process? process = null;
            var matches = false;
            try
            {
                process = Process.GetProcessById(id);
                var actual = process.MainModule?.FileName;
                if (actual is not null && Path.GetFullPath(actual).Equals(executablePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    matches = true;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
            {
                // A different, short-lived child may have exited between snapshots.
            }
            if (matches)
            {
                try { return new(process!); }
                catch { process?.Dispose(); throw; }
            }
            process?.Dispose();
        }
        throw new InvalidOperationException("Pressroom could not identify its private Playwright driver process for reliable cleanup.");
    }

    public void TrackBrowserChildren()
    {
        if (_job is not null) return; // The Windows kernel tracks every descendant, including orphans.
        foreach (var pair in ReadParents().Where(pair => pair.Value == _process.Id))
        {
            try
            {
                var process = Process.GetProcessById(pair.Key);
                _browserRoots.Add((process, process.StartTime));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        if (_browserRoots.Count == 0)
            throw new InvalidOperationException("Pressroom could not identify its launched browser process for reliable cleanup.");
    }

    public async Task<bool> TerminateAsync(TimeSpan timeout)
    {
        if (_job is not null) return await _job.TerminateAsync(timeout);
        var tracked = new List<(Process Process, DateTime Started)>();
        try
        {
            var snapshotTime = DateTime.UtcNow;
            var parents = ReadParents();
            var descendants = new HashSet<int>();
            if (!_process.HasExited && _process.StartTime == _startTime) descendants.Add(_process.Id);
            foreach (var root in _browserRoots)
                if (!root.Process.HasExited && root.Process.StartTime == root.Started) descendants.Add(root.Process.Id);
            // The Linux development fallback cannot reconstruct orphan ownership once
            // every recorded root has gone. Stay unavailable rather than claiming proof
            // of a clean tree. Windows uses its kernel-owned job instead.
            if (descendants.Count == 0) return false;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var pair in parents)
                    if (descendants.Contains(pair.Value) && descendants.Add(pair.Key)) changed = true;
            }
            foreach (var id in descendants)
            {
                try
                {
                    var process = Process.GetProcessById(id);
                    // Reject a PID created after this ancestry snapshot; it could have
                    // been recycled for an unrelated process in the meantime.
                    if (process.StartTime.ToUniversalTime() > snapshotTime)
                    {
                        process.Dispose();
                        return false;
                    }
                    var currentParents = ReadParents();
                    if (!currentParents.TryGetValue(id, out var currentParent)
                        || (!new[] { _process.Id }.Contains(id) && !descendants.Contains(currentParent)))
                    {
                        process.Dispose();
                        return false;
                    }
                    tracked.Add((process, process.StartTime));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }

            // Kill the known driver tree first, preventing it from creating more children.
            if (!_process.HasExited && _process.StartTime == _startTime) _process.Kill(entireProcessTree: true);
            foreach (var item in tracked)
                if (!item.Process.HasExited && item.Process.StartTime == item.Started)
                    item.Process.Kill(entireProcessTree: true);

            using var cancellation = new CancellationTokenSource(timeout);
            await Task.WhenAll(tracked.Select(item => item.Process.WaitForExitAsync(cancellation.Token)));
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            foreach (var item in tracked) item.Process.Dispose();
        }
    }

    private static Dictionary<int, int> ReadParents()
    {
        if (OperatingSystem.IsWindows()) return ReadWindowsParents();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Pressroom supports Windows x64 deployment and Linux development.");
        var result = new Dictionary<int, int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (int.TryParse(fields[1], out var parent)) result[pid] = parent;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private static Dictionary<int, int> ReadWindowsParents()
    {
        var result = new Dictionary<int, int>();
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (!Process32First(handle, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
            while (Process32Next(handle, ref entry));
        }
        finally { CloseHandle(handle); }
        return result;
    }

    public void Dispose()
    {
        _job?.Dispose(); // KILL_ON_JOB_CLOSE is also a process-exit safety net on Windows.
        _process.Dispose();
        foreach (var root in _browserRoots) root.Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
