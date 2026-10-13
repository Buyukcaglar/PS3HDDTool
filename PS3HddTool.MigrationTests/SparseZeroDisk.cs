using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PS3HddTool.Core.Migration;

// TEST ONLY. Persist actual bytes in a generated regular sparse file. All-zero
// writes become host holes, while UFS pointers/used maps still allocate them.
// No generated read responses, content deduplication, or migration shortcuts.
sealed class SparseZeroDisk : IIdentifiedDisk
{
    private readonly FileStream _file;
    private long _zeroStart = -1, _zeroEnd;
    public long ReadByteCount, WriteByteCount, ZeroWriteByteCount;
    public string Identity { get; }
    public long TotalSize => _file.Length;
    public int SectorSize => 512;
    public long SectorCount => TotalSize / 512;
    public string Description => "test-only zero-compacting regular image";
    public bool CanWrite { get; }
    internal SparseZeroDisk(DirectEndpoint endpoint, bool writable)
    {
        if (endpoint.Kind != "image" || endpoint.Crypto != "plain") throw new Exception("Zero compaction is plaintext generated-image-only.");
        string path = RegularImage.CheckedPath(endpoint.Path);
        if ((File.GetAttributes(path) & FileAttributes.SparseFile) == 0) throw new Exception("Test image must already be sparse.");
        using (var identity = new DirectDisk(endpoint, false)) Identity = identity.Identity;
        _file = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.None, 1, FileOptions.RandomAccess);
        CanWrite = writable;
    }
    public byte[] ReadBytes(long offset, int count)
    {
        Bounds(offset, count);
        if (_zeroStart >= 0 && offset < _zeroEnd && offset + count > _zeroStart) DrainZeros();
        byte[] bytes = new byte[count]; _file.Position = offset; _file.ReadExactly(bytes); ReadByteCount += count; return bytes;
    }
    public byte[] ReadSectors(long startSector, int count) => ReadBytes(checked(startSector * 512), checked(count * 512));
    public void WriteSectors(long startSector, byte[] data) => WriteBytes(checked(startSector * 512), data);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (!CanWrite) throw new IOException("Read-only generated source.");
        Bounds(offset, data.Length);
        if (offset % 512 != 0 || data.Length % 512 != 0) throw new IOException("Unaligned test write.");
        WriteByteCount += data.Length;
        if (!data.IsEmpty && data.IndexOfAnyExcept((byte)0) < 0)
        {
            ZeroWriteByteCount += data.Length;
            if (_zeroStart >= 0 && offset != _zeroEnd) DrainZeros();
            if (_zeroStart < 0) _zeroStart = offset;
            _zeroEnd = offset + data.Length;
        }
        else
        {
            if (_zeroStart >= 0 && offset < _zeroEnd && offset + data.Length > _zeroStart) DrainZeros();
            _file.Position = offset; _file.Write(data);
        }
    }
    private void Bounds(long offset, int count)
    { if (offset < 0 || count < 0 || offset > TotalSize - count) throw new IOException("Image bounds."); }
    private void DrainZeros()
    {
        if (_zeroStart < 0) return;
        byte[] range = new byte[16]; BinaryPrimitives.WriteInt64LittleEndian(range, _zeroStart);
        BinaryPrimitives.WriteInt64LittleEndian(range.AsSpan(8), _zeroEnd);
        if (!DeviceIoControl(_file.SafeFileHandle, 0x980C8, range, range.Length, [], 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Generated-file zero compaction failed.");
        _zeroStart = -1;
    }
    public void Flush() { DrainZeros(); if (CanWrite) _file.Flush(true); }
    public void Dispose() { Flush(); _file.Dispose(); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
