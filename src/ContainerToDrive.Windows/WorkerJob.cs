using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContainerToDrive.Windows;

/// <summary>Owns only explicitly assigned direct children; close means interruption, not a clean unmount.</summary>
public sealed class WorkerJob : IDisposable
{
    private readonly object _gate = new();
    private readonly SafeJobHandle _handle;

    public WorkerJob()
    {
        // Unnamed and non-inheritable: no unrelated process can accidentally share this job.
        _handle = CreateJobObject(nint.Zero, null);
        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "The worker job could not be created.");
        }
        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x00002000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "The worker job limits could not be applied.");
        }
    }

    /// <summary>Assign immediately after spawn and before provisioning a secret or starting a mount.</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            var handle = process.SafeHandle;
            ProcessIdentity.ValidateOwnedChild(handle);
            if (!AssignProcessToJobObject(_handle, handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The owned worker could not be assigned to its job.");
        }
    }

    public void Dispose()
    {
        lock (_gate) _handle.Dispose();
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}