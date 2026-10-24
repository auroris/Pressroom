using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pressroom.Rendering;

/// <summary>
/// Kernel-owned lifetime boundary for the private driver and every Chromium descendant,
/// including orphaned descendants. Assigned before the driver launches any browser.
/// </summary>
internal sealed class WindowsProcessJob : IDisposable
{
    private readonly JobHandle _handle;

    public WindowsProcessJob(Process driver)
    {
        _handle = new JobHandle(CreateJobObject(IntPtr.Zero, null));
        try
        {
            if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // No BREAKAWAY_OK flags: browser children must stay in this owned job.
            if (!AssignProcessToJobObject(_handle, driver.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Win32Exception exception)
        {
            _handle.Dispose();
            throw new InvalidOperationException("Pressroom could not put its private browser driver in an owned Windows Job Object. Check application-pool job restrictions; browser isolation will not be disabled.", exception);
        }
    }

    public async Task<bool> TerminateAsync(TimeSpan timeout)
    {
        if (_handle.IsClosed || _handle.IsInvalid) return false;
        if (!TerminateJobObject(_handle, 1)) return false;
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (!QueryInformationJobObject(_handle, 1, out var accounting,
                (uint)Marshal.SizeOf<BasicAccountingInformation>(), out _)) return false;
            if (accounting.ActiveProcesses == 0) return true;
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25));
        } while (elapsed.Elapsed < timeout);
        return false;
    }

    public void Dispose() => _handle.Dispose();

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle(IntPtr nativeHandle) : base(true) => SetHandle(nativeHandle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(JobHandle job, int informationClass, out BasicAccountingInformation information, uint length, out uint returnedLength);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
