using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Pressroom.Rendering;

/// <summary>Tracks all descendants, retaining their /proc start ticks across reparenting.</summary>
internal sealed class LinuxProcessTree
{
    private readonly Dictionary<int, ulong> _owned = [];
    private readonly object _sync = new();

    public LinuxProcessTree(int driverId)
    {
        var process = Read(driverId) ?? throw new InvalidOperationException("The private driver exited before it could be tracked.");
        _owned.Add(driverId, process.Started);
    }

    public void CaptureDescendants()
    {
        lock (_sync)
        {
            var snapshot = Directory.EnumerateDirectories("/proc")
                .Select(path => int.TryParse(Path.GetFileName(path), out var pid) ? Read(pid) : null)
                .OfType<Entry>().ToDictionary(entry => entry.Id);
            var live = _owned.Where(pair => snapshot.TryGetValue(pair.Key, out var entry) && entry.Started == pair.Value)
                .Select(pair => pair.Key).ToHashSet();
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in snapshot.Values)
                    if (live.Contains(entry.Parent) && live.Add(entry.Id))
                    {
                        _owned[entry.Id] = entry.Started;
                        changed = true;
                    }
            } while (changed);
        }
    }

    public async Task<bool> TerminateAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        CaptureDescendants();
        Signal(15); // SIGTERM gives Chromium and Node a bounded graceful exit.
        while (clock.Elapsed < timeout / 2 && Alive()) await Task.Delay(25);
        CaptureDescendants(); // Include children created while SIGTERM was in flight.
        Signal(9);
        while (clock.Elapsed < timeout && Alive()) await Task.Delay(25);
        return !Alive();
    }

    private bool Alive()
    {
        lock (_sync) return _owned.Any(pair => Read(pair.Key) is { State: not ('Z' or 'X') } entry && entry.Started == pair.Value);
    }

    private void Signal(int signal)
    {
        lock (_sync)
            foreach (var pair in _owned)
            {
                // pidfds bind a signal to one process, even if its PID is subsequently reused.
                var fd = PidFdOpen(pair.Key, 0);
                if (fd < 0) continue;
                try
                {
                    if (Read(pair.Key) is { } entry && entry.Started == pair.Value)
                        PidFdSendSignal(fd, signal, IntPtr.Zero, 0);
                }
                finally { Close(fd); }
            }
    }

    private static Entry? Read(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var end = stat.LastIndexOf(')');
            if (end < 0 || end + 2 >= stat.Length) return null;
            var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent)
                || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return null;
            return new(pid, parent, started, fields[0][0]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private sealed record Entry(int Id, int Parent, ulong Started, char State);
    // Linux x64 syscall numbers, supported by the documented Ubuntu/Debian kernels.
    private static int PidFdOpen(int pid, uint flags) => (int)SyscallOpen(434, pid, flags);
    private static int PidFdSendSignal(int fd, int signal, IntPtr info, uint flags) => (int)SyscallSignal(424, fd, signal, info, flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpen(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallSignal(long number, int fd, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
