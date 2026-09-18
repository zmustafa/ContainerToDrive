using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContainerToDrive.IntegrationTests;

internal static class OwnedProcessCommandLine
{
    internal static string Read(Process process)
    {
        // Query only the worker identified by authenticated core/pid, with its handle held open.
        // No WMI, shell, process-name enumeration, privileges, or inspection of unrelated processes.
        const int capacity = 128 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var status = NtQueryInformationProcess(process.SafeHandle, 60, buffer, capacity, out var returned);
            if (status != 0)
                throw new InvalidOperationException($"Owned worker command-line query failed (NTSTATUS 0x{status:X8}).");
            if (returned < Marshal.SizeOf<UnicodeString>() || returned > capacity)
                throw new InvalidDataException("Unexpected owned worker command-line response size.");
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = text.Buffer.ToInt64() - buffer.ToInt64();
            if (text.Length == 0 || text.Length % 2 != 0 || text.MaximumLength < text.Length ||
                offset < Marshal.SizeOf<UnicodeString>() || offset > returned - text.Length)
                throw new InvalidDataException("Unexpected owned worker command-line string bounds.");
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2)
                ?? throw new InvalidDataException("The owned worker command line was unavailable.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        nint information, int length, out int returned);
}