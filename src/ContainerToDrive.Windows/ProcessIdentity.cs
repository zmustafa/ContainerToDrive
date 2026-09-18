using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ContainerToDrive.Windows;

internal static class ProcessIdentity
{
    private const uint TokenQuery = 0x0008;
    private const uint QueryLimitedInformation = 0x1000;
    private const int TokenSessionId = 12;
    private const int TokenElevation = 20;
    private const string Unverified = "The local controller peer could not be verified.";

    internal static bool IsCurrentProcessElevated()
    {
        using var current = Process.GetCurrentProcess();
        using var token = OpenToken(current.SafeHandle);
        return TokenValue(token, TokenElevation) != 0;
    }

    internal static void ValidateServer(NamedPipeClientStream pipe, string executable)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId)) throw Untrusted();
        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid) throw Untrusted();
        ValidateToken(process);

        AppPaths.RejectReparsePoints(executable);
        var name = new StringBuilder(32768);
        var length = (uint)name.Capacity;
        if (!QueryFullProcessImageName(process, 0, name, ref length) ||
            !string.Equals(Path.GetFullPath(name.ToString()), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            throw Untrusted();

        // Keep the process object alive during verification and recheck the pipe endpoint.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var confirmed) || confirmed != processId)
            throw Untrusted();
    }

    internal static void ValidateClient(NamedPipeServerStream pipe)
    {
        if (!pipe.IsConnected || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId)) throw Untrusted();
        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid) throw Untrusted();
        ValidateToken(process);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var confirmed) || confirmed != processId)
            throw Untrusted();
    }

    internal static void ValidateOwnedChild(SafeProcessHandle process)
    {
        ValidateToken(process);
        // A caller cannot place an arbitrary same-user process into our kill-on-close job.
        if (NtQueryInformationProcess(process, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 ||
            basic.ParentProcessId != (nint)Environment.ProcessId)
            throw new UnauthorizedAccessException("Only a directly owned worker can be assigned to the job.");
        using var current = Process.GetCurrentProcess();
        if (!GetProcessTimes(process, out var childCreation, out _, out _, out _) ||
            !GetProcessTimes(current.SafeHandle, out var parentCreation, out _, out _, out _) || childCreation < parentCreation)
            throw new UnauthorizedAccessException("The worker ownership could not be verified.");
    }

    private static void ValidateToken(SafeProcessHandle process)
    {
        using var token = OpenToken(process);
        using var peer = new WindowsIdentity(token.DangerousGetHandle());
        using var current = Process.GetCurrentProcess();
        using var ownToken = OpenToken(current.SafeHandle);
        using var ownIdentity = new WindowsIdentity(ownToken.DangerousGetHandle());
        if (peer.User is null || peer.User != ownIdentity.User || peer.Owner != ownIdentity.Owner ||
            TokenValue(token, TokenSessionId) != TokenValue(ownToken, TokenSessionId) ||
            TokenValue(token, TokenElevation) != TokenValue(ownToken, TokenElevation))
            throw Untrusted();
    }

    private static SafeAccessTokenHandle OpenToken(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            token?.Dispose();
            throw Untrusted();
        }
        return token;
    }

    private static uint TokenValue(SafeAccessTokenHandle token, int informationClass)
    {
        if (!GetTokenInformation(token, informationClass, out var value, sizeof(uint), out var returned) || returned != sizeof(uint))
            throw Untrusted();
        return value;
    }

    private static UnauthorizedAccessException Untrusted() => new(Unverified);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nuint AffinityMask;
        public nint BasePriority;
        public nuint ProcessId;
        public nint ParentProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, int informationLength, out int returnLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out ProcessBasicInformation information, int informationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}