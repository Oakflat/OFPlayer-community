using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OFPlayer.WinUI.Core.Transport;

/// <summary>
/// One Windows lifetime group for this process's private helpers. The parent alone
/// holds its non-inheritable handle: a parent crash closes it and reaps the helpers.
/// This is kernel ownership, not a promise about Task Manager's visual grouping.
/// Never attach shell-launched browsers, installers or pre-existing processes.
/// </summary>
public static class OwnedChildProcessGroup
{
    private static readonly Lazy<JobHandle> Group = new(CreateGroup);
    public static string Name { get; } = $"Local\\OFPlayer.Children.{Environment.ProcessId}.{Guid.NewGuid():N}";

    /// <summary>Call immediately after starting a private helper, before sending work.</summary>
    public static void Attach(Process child)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (AssignProcessToJobObject(Group.Value, child.SafeHandle)) return;
            var error = Marshal.GetLastWin32Error();
            if (child.HasExited) return; // Short-lived version/probe commands may already be done.
            throw new Win32Exception(error, "Cannot attach the private OFPlayer helper to its owner.");
        }
        catch
        {
            // The new child has not received work. Do not leave it running unowned.
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            throw;
        }
    }

    public static bool Contains(Process child)
    {
        if (!OperatingSystem.IsWindows() || !Group.IsValueCreated) return false;
        if (!IsProcessInJob(child.SafeHandle, Group.Value, out var member))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return member;
    }

    private static JobHandle CreateGroup()
    {
        var job = CreateJobObject(0, Name);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }
        return job;
    }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObject(nint attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, JobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
