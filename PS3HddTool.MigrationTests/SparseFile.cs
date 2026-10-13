using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PS3HddTool.Core.Migration;

// File-only Windows sparse APIs. No physical handle, disk enumeration or volume probe.
static class SparseFile
{
    internal static void Create(string path, long length)
    {
        path = RegularImage.CheckedPath(path, false);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(file.SafeFileHandle, 0x900C4, [], 0, [], 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Sparse marking failed; refusing to extend a nonsparse file.");
        file.SetLength(length); file.Flush(true);
        if ((File.GetAttributes(path) & FileAttributes.SparseFile) == 0) throw new Exception("File is not sparse.");
    }
    internal static long Allocated(string path)
    {
        uint low = GetCompressedFileSizeW(RegularImage.CheckedPath(path), out uint high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
        return checked((long)(((ulong)high << 32) | low));
    }
    internal static string AllocatedDigest(string path)
    {
        using var file = new FileStream(RegularImage.CheckedPath(path), FileMode.Open, FileAccess.Read, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] input = new byte[16], output = new byte[65536], header = new byte[16], buffer = new byte[1024 * 1024];
        long position = 0;
        while (position < file.Length)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, position); BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8), file.Length - position);
            bool success = DeviceIoControl(file.SafeFileHandle, 0x940CF, input, input.Length, output, output.Length, out int returned, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if ((!success && error != 234) || returned % 16 != 0 || (!success && returned == 0))
                throw new Win32Exception(error, "Sparse range query failed.");
            for (int p = 0; p < returned; p += 16)
            {
                long at = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(p)), length = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(p + 8));
                if (at < position || length <= 0 || at > file.Length - length) throw new Exception("Invalid allocated file range.");
                BinaryPrimitives.WriteInt64LittleEndian(header, at); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), length); hash.AppendData(header);
                file.Position = at;
                for (long left = length; left > 0;) { int n = (int)Math.Min(buffer.Length, left); file.ReadExactly(buffer.AsSpan(0, n)); hash.AppendData(buffer.AsSpan(0, n)); left -= n; }
                position = at + length;
            }
            if (success) break;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);
}
