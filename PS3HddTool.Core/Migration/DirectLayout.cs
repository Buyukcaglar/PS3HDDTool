using System.Buffers.Binary;
using System.Security.Cryptography;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public sealed record DiskRange(long Offset, long Length);
internal sealed class PlanningDisk(IDiskSource source) : IDiskSource, IDurableDisk
{
    internal readonly List<DiskRange> Writes = new();
    public long TotalSize => source.TotalSize; public int SectorSize => 512;
    public long SectorCount => source.SectorCount; public string Description => "Metadata plan"; public bool CanWrite => true;
    public byte[] ReadBytes(long offset, int count) => source.ReadBytes(offset, count);
    public byte[] ReadSectors(long startSector, int count) => source.ReadSectors(startSector, count);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data) => Writes.Add(new(offset / 512 * 512, ((offset % 512 + data.Length + 511) / 512) * 512));
    public void WriteSectors(long sector, byte[] data) => WriteBytes(checked(sector * 512), data);
    public void Flush() { } public void Dispose() { }
}

internal static class DirectLayout
{
    internal const int Chunk = 1024 * 1024;
    internal static List<DiskRange> Merge(IEnumerable<DiskRange> input)
    {
        var result = new List<DiskRange>();
        foreach (var r in input.OrderBy(r => r.Offset))
        {
            if (r.Offset < 0 || r.Length <= 0 || r.Offset % 512 != 0 || r.Length % 512 != 0) throw new InvalidDataException("Invalid sector range.");
            long end = checked(r.Offset + r.Length);
            if (result.Count > 0 && r.Offset <= checked(result[^1].Offset + result[^1].Length))
            { var old = result[^1]; result[^1] = old with { Length = Math.Max(end, old.Offset + old.Length) - old.Offset }; }
            else result.Add(r);
        }
        return result;
    }
    internal static IEnumerable<DiskRange> Chunks(IEnumerable<DiskRange> ranges)
    {
        foreach (var r in ranges)
            for (long p = r.Offset, end = checked(r.Offset + r.Length); p < end;)
            { long n = Math.Min(Chunk, end - p); yield return new(p, n); p += n; }
    }
    internal static (List<DiskRange> Metadata, Dictionary<long, long> Mapping) Plan(UfsVolume source, UfsVolume target, CancellationToken token,
        Action<MigrationProgress>? telemetry = null, string? targetRecovery = null)
    {
        var proxy = new PlanningDisk(target.Disk); var planned = new UfsVolume(proxy, target.Offset / 512);
        TargetPreservation.Inventory(planned, targetRecovery, token, telemetry); planned.ReleaseOldTree(false);
        var mapping = new Dictionary<long, long> { [2] = planned.AllocateInode(true) };
        foreach (long ino in source.Nodes.Keys.Order()) { token.ThrowIfCancellationRequested(); if (ino != 2) mapping.Add(ino, planned.AllocateInode()); }
        foreach (long ino in mapping.Values) proxy.Writes.Add(new(planned.InodeOffset(ino) / 512 * 512, 512));
        proxy.Writes.Add(new(target.Offset + 65536, 8192));
        proxy.Writes.Add(new(target.Offset + UfsVolume.I64(target.Super, 0x448) * target.Fragment, UfsVolume.I32(target.Super, 0x9C)));
        foreach (Group g in target.Groups)
        {
            proxy.Writes.Add(new(g.Offset, (g.Raw.Length + 511L) / 512 * 512));
            long backup = target.Offset + ((long)g.Number * target.Fpg + UfsVolume.I32(target.Super, 8)) * target.Fragment;
            byte[] sb = target.Disk.ReadBytes(backup, 8192);
            foreach (int p in new[] { 8, 12, 16, 20, 0x2C, 0x30, 0x34, 0xB8, 0xBC, 0x55C })
                if (UfsVolume.I32(sb, p) != UfsVolume.I32(target.Super, p)) throw new InvalidDataException("Destination backup superblock geometry mismatch.");
            if (UfsVolume.I64(sb, 0x438) != target.Size || sb[0xD1] != 1) throw new InvalidDataException("Invalid/dirty destination backup superblock.");
            proxy.Writes.Add(new(backup, 8192));
        }
        var ranges = Merge(proxy.Writes);
        if (ranges.Any(r => r.Offset < target.Offset || r.Offset + r.Length > target.Offset + target.Size * target.Fragment))
            throw new InvalidDataException("Metadata plan extends outside target volume.");
        return (ranges, mapping);
    }
    internal static List<DiskRange> DataRanges(UfsVolume volume) => Merge(volume.Nodes.Values.SelectMany(n => volume.Extents(n))
        .Select(x => new DiskRange(checked(volume.Offset + x.Fragment * volume.Fragment), (long)x.Count * volume.Fragment))
        .Concat(volume.PreservedRecoveryData is DiskRange recovery ? new[]{recovery} : Array.Empty<DiskRange>()));
    internal static List<DiskRange> Outside(UfsVolume volume)
    {
        var ranges = new List<DiskRange>(); if (volume.Offset > 0) ranges.Add(new(0, volume.Offset));
        long end = checked(volume.Offset + volume.Size * volume.Fragment);
        if (end < volume.Disk.TotalSize) ranges.Add(new(end, volume.Disk.TotalSize - end));
        return Merge(ranges);
    }
    internal static string HashRanges(IDiskSource disk, IEnumerable<DiskRange> ranges, CancellationToken token = default,
        Action<MigrationProgress>? telemetry = null, string phase = "hash-ranges")
    {
        var list = ranges.ToList();
        var meter = new ProgressMeter(telemetry, phase, "Hash protected raw sectors", list.Sum(r => r.Length));
        meter.Add(0, force: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] header = new byte[16];
        foreach (var r in Chunks(list))
        {
            token.ThrowIfCancellationRequested(); BinaryPrimitives.WriteInt64LittleEndian(header, r.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), r.Length); hash.AppendData(header);
            hash.AppendData(disk.ReadBytes(r.Offset, checked((int)r.Length)));
            meter.Add(r.Length);
        }
        meter.Add(0, force: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static string TreeDigest(UfsVolume volume)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] number = new byte[8];
        foreach (Node n in volume.Nodes.Values.OrderBy(n => n.Number))
        {
            BinaryPrimitives.WriteInt64LittleEndian(number, n.Number); hash.AppendData(number); hash.AppendData(n.Raw);
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes(n.Hash));
            if (n.Type == 4)
                foreach (Entry e in n.Entries)
                { BinaryPrimitives.WriteInt64LittleEndian(number, e.Inode); hash.AppendData(number); hash.AppendData(e.Name); hash.AppendData(new byte[] { 0, e.Type }); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
