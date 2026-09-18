using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ContainerToDrive.Windows;
using Microsoft.Win32.SafeHandles;

namespace ContainerToDrive.IntegrationTests;

/// <summary>A mount-point reparse tag on an owned directory, NOT a drive/volume mount or symbolic link.</summary>
internal sealed class LocalJunction : IDisposable
{
    private LocalJunction(string path) => Path = path;
    internal string Path { get; }

    internal static LocalJunction Create(LocalTestRoot root, string name, string targetName)
    {
        var path = root.PathFor(name);
        var target = root.PathFor(targetName);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("The test junction must be new.");
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The local test target must already exist.");
        AppPaths.SecureDirectory(path);
        try
        {
            // NTFS directory junctions need no SeCreateSymbolicLinkPrivilege, Developer Mode, or elevation.
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = Encoding.Unicode.GetBytes(target);
            var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003); // IO_REPARSE_TAG_MOUNT_POINT
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);
            using var handle = CreateFile(path, 0x40000000, 7, nint.Zero, 3, 0x02200000, nint.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening the local junction failed.");
            if (!DeviceIoControl(handle, 0x000900a4, buffer, buffer.Length, nint.Zero, 0, out _, nint.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Creating the local junction failed.");
            return new LocalJunction(path);
        }
        catch
        {
            Directory.Delete(path, recursive: false);
            throw;
        }
    }

    // Delete ONLY the link, never traverse the target. Any failure fails the test.
    public void Dispose() => Directory.Delete(Path, recursive: false);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength,
        nint output, int outputLength, out int returned, nint overlapped);
}