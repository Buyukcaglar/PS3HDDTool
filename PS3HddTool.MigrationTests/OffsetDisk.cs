using PS3HddTool.Core.Disk;

// A generated fixture at a virtual >2 TB partition offset. No file/device handles.
sealed class OffsetDisk(byte[] fixture, long shift) : IDiskSource
{
    public long TotalSize => checked(shift + fixture.LongLength);
    public int SectorSize => 512;
    public long SectorCount => TotalSize / 512;
    public string Description => "Synthetic address translation";
    public bool CanWrite => true;
    public int Writes;
    public byte[] ReadBytes(long offset, int count)
    {
        long index = offset - shift;
        if (index < 0 || count < 0 || index > fixture.LongLength - count) throw new ArgumentOutOfRangeException(nameof(offset));
        return fixture.AsSpan(checked((int)index), count).ToArray();
    }
    public byte[] ReadSectors(long startSector, int count) => ReadBytes(checked(startSector * 512), checked(count * 512));
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        long index = offset - shift;
        if (index < 0 || index > fixture.LongLength - data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        data.CopyTo(fixture.AsSpan(checked((int)index))); Writes++;
    }
    public void WriteSectors(long startSector, byte[] data) => WriteBytes(checked(startSector * 512), data);
    public void Dispose() { }
}
