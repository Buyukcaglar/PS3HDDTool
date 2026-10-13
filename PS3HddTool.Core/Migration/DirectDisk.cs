using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public sealed record DirectEndpoint(string Path, long PartitionSector, string Crypto = "plain", string? KeyFile = null,
    string Kind = "image", string? ExpectedSerial = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? KeyKind = null)
{
    internal ImageSpec Image => new(Path, PartitionSector, Crypto, KeyFile,KeyKind);
    internal DirectEndpoint Normalize()
    {
        if (PartitionSector < 0) throw new ArgumentOutOfRangeException(nameof(PartitionSector));
        RegularImage.ValidateKeyKind(Image);
        string path;
        if (Kind == "image") path = RegularImage.CheckedPath(Path);
        else if (Kind == "windows-device")
        {
            if (!Regex.IsMatch(Path, @"\A\\\\\.\\PhysicalDrive(0|[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(ExpectedSerial))
                throw new ArgumentException("Device endpoints require an exact Windows PhysicalDrive path and an expected hardware serial.");
            path = Path;
        }
        else throw new ArgumentException("Endpoint kind must be image or windows-device.");
        return this with { Path = path, KeyFile = KeyFile == null ? null : RegularImage.CheckedPath(KeyFile) };
    }
}

internal interface IDurableDisk { void Flush(); }
internal interface IIdentifiedDisk : IDiskSource, IDurableDisk { string Identity { get; } int PhysicalWriteUnit => 512; }

// Crypto wrappers own this view, while the transaction owns the actual handle.
internal sealed class BorrowedDisk(IDiskSource raw) : IDiskSource, IDurableDisk
{
    public long TotalSize => raw.TotalSize; public int SectorSize => raw.SectorSize;
    public long SectorCount => raw.SectorCount; public string Description => raw.Description; public bool CanWrite => raw.CanWrite;
    public byte[] ReadBytes(long offset, int count) => raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long startSector, int count) => raw.ReadSectors(startSector, count);
    public void WriteBytes(long offset, byte[] data) => raw.WriteBytes(offset, data);
    public void WriteBytes(long offset, ReadOnlySpan<byte> data) => raw.WriteBytes(offset, data);
    public void WriteSectors(long startSector, byte[] data) => raw.WriteSectors(startSector, data);
    public void Flush() => RegularImage.Flush(raw);
    public void Dispose() { }
}

internal sealed class DirectDisk : IIdentifiedDisk
{
    private readonly FileStream? _image;
    private readonly SafeFileHandle _handle;
    private readonly bool _writable;
    public long TotalSize { get; }
    public int SectorSize => 512;
    public long SectorCount => TotalSize / 512;
    public string Description { get; }
    public string Identity { get; }
    public bool CanWrite => _writable;
    public int PhysicalWriteUnit { get; } = 512;

    // This function is the only new device entry point. No enumeration or fallback probing.
    internal DirectDisk(DirectEndpoint endpoint, bool writable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Direct migration handle identity currently requires Windows.");
        endpoint = endpoint.Normalize(); _writable = writable; Description = endpoint.Kind + ": " + endpoint.Path;
        if (endpoint.Kind == "image")
        {
            _image = new FileStream(endpoint.Path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
                FileShare.None, 1, FileOptions.RandomAccess);
            _handle = _image.SafeFileHandle;
            try
            {
                TotalSize = _image.Length;
                if (!GetFileInformationByHandle(_handle, out FileInformation info)) throw new Win32Exception();
                Identity = $"image:{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}:{TotalSize}";
            }
            catch { _image.Dispose(); throw; }
        }
        else
        {
            _handle = CreateFileW(endpoint.Path, writable ? 0xC0000000u : 0x80000000u,
                0, IntPtr.Zero, 3, 0x80000000, IntPtr.Zero); // exclusive, write-through, buffered sector I/O
            if (_handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error, "Cannot acquire exclusive disk handle."); }
            try
            {
                byte[] length = Ioctl(0x7405C, [], 8); TotalSize = BinaryPrimitives.ReadInt64LittleEndian(length);
                byte[] geometry = Ioctl(0x700A0, [], 256);
                if (BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(20)) != 512)
                    throw new NotSupportedException("Direct migration requires 512-byte logical sectors.");
                byte[] alignmentQuery = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(alignmentQuery, 6); // StorageAccessAlignmentProperty
                PhysicalWriteUnit = ParseAlignment(Ioctl(0x2D1400, alignmentQuery, 128));
                byte[] attributes = Ioctl(0x700F0, [], 32);
                if ((BinaryPrimitives.ReadUInt64LittleEndian(attributes.AsSpan(8)) & 1) == 0)
                    throw new InvalidOperationException("Device must already be offline in Windows. This tool never changes disk online/offline state.");
                byte[] descriptor = Ioctl(0x2D1400, new byte[12], 4096);
                int serialAt = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(24));
                int declared = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(4));
                if (serialAt < 36 || serialAt >= declared || declared > descriptor.Length)
                    throw new InvalidDataException("Missing or truncated storage device serial descriptor.");
                int end = Array.IndexOf(descriptor, (byte)0, serialAt, declared - serialAt);
                if (end < 0) throw new InvalidDataException("Unterminated device serial.");
                string serial = Encoding.ASCII.GetString(descriptor, serialAt, end - serialAt).Trim();
                if (serial.Length == 0 || !serial.Equals(endpoint.ExpectedSerial, StringComparison.Ordinal))
                    throw new InvalidDataException("Device serial does not match the explicitly supplied identity.");
                Identity = $"device:{serial}:{TotalSize}";
            }
            catch { _handle.Dispose(); throw; }
        }
        if (TotalSize <= 0 || TotalSize % 512 != 0) { Dispose(); throw new InvalidDataException("Invalid endpoint size."); }
    }
    internal static int ParseAlignment(byte[] descriptor)
    {
        if (descriptor.Length < 28 || BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(4)) < 28 ||
            BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(4)) > descriptor.Length)
            throw new InvalidDataException("Missing/truncated storage alignment descriptor.");
        int logical = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(16));
        int physical = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(20));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(24));
        if (logical != 512 || physical < 512 || physical > 4096 || (physical & (physical - 1)) != 0 || offset != 0)
            throw new NotSupportedException("Unknown/unsupported or misaligned physical storage sectors.");
        return physical;
    }
    private byte[] Ioctl(uint code, byte[] input, int length)
    {
        byte[] output = new byte[length];
        if (!DeviceIoControl(_handle, code, input, input.Length, output, output.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Device control {code:X} failed.");
        return output.AsSpan(0, returned).ToArray();
    }
    public unsafe byte[] ReadBytes(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > TotalSize - count) throw new ArgumentOutOfRangeException(nameof(offset));
        long first = offset / 512 * 512; int length = checked((int)((offset - first + count + 511) / 512 * 512));
        byte[] data = new byte[length];
        if (length == 0) return [];
        // Raw disks can impose noncached buffer alignment even without NO_BUFFERING.
        // Keep both address and length aligned; no physical branch is exercised by image tests.
        void* aligned = _image == null ? NativeMemory.AlignedAlloc((nuint)length, 4096) : null;
        if (_image == null && aligned == null) throw new OutOfMemoryException();
        try
        {
            Span<byte> buffer = aligned == null ? data.AsSpan() : new Span<byte>(aligned, length);
            int read = 0;
            while (read < length)
            {
                int n = RandomAccess.Read(_handle, buffer[read..], first + read);
                if (n == 0 || (_image == null && n % 512 != 0)) throw new EndOfStreamException("Short endpoint read.");
                read += n;
            }
            if (aligned != null) buffer.CopyTo(data);
        }
        finally { if (aligned != null) NativeMemory.AlignedFree(aligned); }
        return data.AsSpan(checked((int)(offset - first)), count).ToArray();
    }
    public byte[] ReadSectors(long startSector, int count) => ReadBytes(checked(startSector * 512), checked(count * 512));
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public unsafe void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (!CanWrite) throw new InvalidOperationException("Source is read-only.");
        if (offset < 0 || offset % 512 != 0 || data.Length % 512 != 0 || offset > TotalSize - data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Endpoint writes must be bounded and sector aligned.");
        if (_image != null || data.Length == 0) { RandomAccess.Write(_handle, data, offset); return; }
        void* aligned = NativeMemory.AlignedAlloc((nuint)data.Length, 4096);
        if (aligned == null) throw new OutOfMemoryException();
        try { Span<byte> buffer = new(aligned, data.Length); data.CopyTo(buffer); RandomAccess.Write(_handle, buffer, offset); }
        finally { NativeMemory.AlignedFree(aligned); }
    }
    public void WriteSectors(long startSector, byte[] data) => WriteBytes(checked(startSector * 512), data);
    public void Flush()
    {
        if (!CanWrite) return;
        RandomAccess.FlushToDisk(_handle);
    }
    public void Dispose() { if (_image != null) _image.Dispose(); else _handle.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
