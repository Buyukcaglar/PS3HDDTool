using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public sealed record DirectRequest(DirectEndpoint Source, DirectEndpoint Destination, string JournalDirectory,
    string? SourceRegistry = null, string? TargetRegistry = null, long MaxJournalBytes = 1073741824,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRecovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetPreservation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetRecovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceHashCache = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceHashCacheSha256 = null);
public sealed record DirectPlan(string SourceIdentity, string DestinationIdentity, long JournalBytes,
    long RequiredFragments, long FreeFragments, int Nodes, bool ConsoleStateRestoreRequired,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceRecoveryReport? SourceRecovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TargetPreservationReport? TargetPreservation = null);
public sealed record DirectState(int Version, DirectRequest Request, DirectPlan Plan, string Phase,
    string SourceDigest, string OriginalDataDigest, string OutsideDigest, List<DiskRange> Metadata,
    List<DiskRange> ProtectedData, List<DiskRange> Outside, string UndoSha256, long UndoLength,
    string DirtyPrimaryCiphertext, string OriginalSuperblock, List<InodeMapping> Mapping,
    string? RegistrySha256, string? TargetRegistrySha256, string? DestinationDigest = null,
    bool ConsoleRuntimeValidated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerificationSourceHashSha256 = null);
internal sealed record StateEnvelope(DirectState State, string Sha256);

internal static class DirectJournal
{
    internal const int RecordHeader = 44; // offset64, length32, SHA256(header prefix + original ciphertext)
    internal static string SaveUndo(string directory, IIdentifiedDisk target, List<DiskRange> ranges,
        Action<string>? progress, CancellationToken token, Action<MigrationProgress>? telemetry = null)
    {
        var meter = new ProgressMeter(telemetry, "journal-snapshot", "Snapshot raw metadata undo", DirectLayout.Chunks(ranges).Sum(r => r.Length + RecordHeader));
        meter.Add(0, force: true);
        string path = Path.Combine(directory, "undo.bin");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var r in DirectLayout.Chunks(ranges))
        {
            token.ThrowIfCancellationRequested(); byte[] data = target.ReadBytes(r.Offset, (int)r.Length);
            byte[] header = new byte[RecordHeader]; BinaryPrimitives.WriteInt64LittleEndian(header, r.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), data.Length);
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            checksum.AppendData(header.AsSpan(0, 12)); checksum.AppendData(data);
            checksum.GetHashAndReset().CopyTo(header, 12);
            file.Write(header); file.Write(data); digest.AppendData(header); digest.AppendData(data);
            meter.Add(header.Length + data.Length);
            progress?.Invoke("Journal snapshot chunk");
        }
        file.Flush(true); meter.Add(0, force: true); return Convert.ToHexString(digest.GetHashAndReset());
    }
    internal static void Save(string directory, DirectState state)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(state, ImageMigrator.Json);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new StateEnvelope(state, Convert.ToHexString(SHA256.HashData(payload))), ImageMigrator.Json);
        string path = Path.Combine(directory, "state.json"), temp = Path.Combine(directory, ".state-" + Guid.NewGuid().ToString("N"));
        RegularImage.CheckedPath(path, false);
        using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(envelope); f.Flush(true); }
        File.Move(temp, path, true);
        if (!File.ReadAllBytes(RegularImage.CheckedPath(path)).AsSpan().SequenceEqual(envelope))
            throw new IOException("Direct migration state readback mismatch.");
    }
    internal static DirectState Load(string directory)
    {
        var envelope = JsonSerializer.Deserialize<StateEnvelope>(File.ReadAllBytes(RegularImage.CheckedPath(Path.Combine(directory, "state.json"))), ImageMigrator.Json)
            ?? throw new InvalidDataException("Missing direct migration state.");
        var state = envelope.State;
        if (envelope.Sha256 != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, ImageMigrator.Json))) ||
            state.Version != 1 || state.ConsoleRuntimeValidated || state.UndoLength != state.Plan.JournalBytes ||
            state.Phase is not ("prepared" or "copying" or "verifying" or "verification-paused" or "committing" or "complete" or "rolling-back" or "rolled-back"))
            throw new InvalidDataException("Corrupt/unsupported direct migration state.");
        return state;
    }
    internal static FileStream OpenUndo(string directory) => new(RegularImage.CheckedPath(Path.Combine(directory, "undo.bin")),
        FileMode.Open, FileAccess.Read, FileShare.Read);
    internal static void ValidateUndo(string directory, DirectState state, IIdentifiedDisk target, FileStream? undo = null,
        Action<MigrationProgress>? telemetry = null)
    {
        MigrationTelemetry.Send(telemetry, new("validate-undo", "Validate complete undo journal before writes"));
        using var owned = undo == null ? OpenUndo(directory) : null; var file = undo ?? owned!;
        if (target.Identity != state.Plan.DestinationIdentity) throw new InvalidDataException("Destination handle identity changed; recovery refused.");
        byte[] original = Convert.FromBase64String(state.OriginalSuperblock);
        if (original.Length != 8192 || UfsVolume.I32(original, 0x34) % target.PhysicalWriteUnit != 0 ||
            checked(state.Request.Destination.PartitionSector * 512) % target.PhysicalWriteUnit != 0)
            throw new InvalidDataException("Recovery physical write-unit alignment mismatch.");
        ValidateLayout(state, target.TotalSize);
        file.Position = 0;
        if (file.Length != state.UndoLength || Convert.ToHexString(SHA256.HashData(file)) != state.UndoSha256)
            throw new InvalidDataException("Undo journal is truncated or corrupt; destination not touched.");
        // Validate every record and its exact planned address BEFORE issuing any write.
        foreach (var _ in Records(file, state)) { }
        if (DirectLayout.HashRanges(target, state.ProtectedData, telemetry: telemetry, phase: "validate-protected-data") != state.OriginalDataDigest ||
            DirectLayout.HashRanges(target, state.Outside, telemetry: telemetry, phase: "validate-outside") != state.OutsideDigest)
            throw new InvalidDataException("Protected destination data changed; recovery refused.");
    }
    private static void ValidateLayout(DirectState state, long diskSize)
    {
        byte[] sb = Convert.FromBase64String(state.OriginalSuperblock);
        if (sb.Length != 8192 || UfsVolume.I32(sb, 0x55C) != 0x19540119 || sb[0xD1] != 1 ||
            Convert.FromBase64String(state.DirtyPrimaryCiphertext).Length != 512)
            throw new InvalidDataException("Invalid original superblock/recovery marker.");
        long start = checked(state.Request.Destination.PartitionSector * 512);
        int frag = UfsVolume.I32(sb, 0x34), ncg = UfsVolume.I32(sb, 0x2C), fpg = UfsVolume.I32(sb, 0xBC);
        int ipg = UfsVolume.I32(sb, 0xB8), iblk = UfsVolume.I32(sb, 0x10), cblk = UfsVolume.I32(sb, 12), sblk = UfsVolume.I32(sb, 8);
        long size = UfsVolume.I64(sb, 0x438), end = checked(start + size * frag), primary = checked(start + 65536);
        if (start < 0 || frag < 512 || frag > 65536 || (frag & (frag - 1)) != 0 || size <= 0 ||
            ncg <= 0 || ncg > 100000 || fpg <= 0 || ipg <= 2 || iblk <= cblk || cblk <= sblk || sblk <= 0 ||
            (size + fpg - 1) / fpg != ncg || end > diskSize || primary > end - 8192)
            throw new InvalidDataException("Invalid recovery volume bounds/geometry.");
        var allowed = new List<DiskRange> { new(primary, 8192),
            new(checked(start + UfsVolume.I64(sb, 0x448) * frag), UfsVolume.I32(sb, 0x9C)) };
        int cgSize = UfsVolume.I32(sb, 0xA0);
        if (cgSize < 168 || cgSize > 65536) throw new InvalidDataException("Invalid recovery cylinder group size.");
        for (int cg = 0; cg < ncg; cg++)
        {
            long at = checked(start + (long)cg * fpg * frag);
            allowed.Add(new(checked(at + (long)sblk * frag), 8192));
            allowed.Add(new(checked(at + (long)cblk * frag), (cgSize + 511L) / 512 * 512));
            allowed.Add(new(checked(at + (long)iblk * frag), checked((long)ipg * 256)));
        }
        var legal = DirectLayout.Merge(allowed);
        foreach (var ranges in new[] { state.Metadata, state.ProtectedData, state.Outside })
            if (!DirectLayout.Merge(ranges).SequenceEqual(ranges) || ranges.Any(r => r.Offset > diskSize - r.Length))
                throw new InvalidDataException("Invalid/unsorted recovery ranges.");
        int legalIndex = 0;
        foreach (var r in state.Metadata)
        {
            while (legalIndex < legal.Count && legal[legalIndex].Offset + legal[legalIndex].Length <= r.Offset) legalIndex++;
            if (r.Offset < start || r.Offset > end - r.Length || legalIndex >= legal.Count ||
                r.Offset < legal[legalIndex].Offset || r.Offset + r.Length > legal[legalIndex].Offset + legal[legalIndex].Length)
                throw new InvalidDataException("Recovery writes extend outside filesystem metadata.");
        }
        if (!state.Metadata.Any(r => primary >= r.Offset && primary + 512 <= r.Offset + r.Length))
            throw new InvalidDataException("Missing primary superblock before-image.");
        int index = 0;
        foreach (var r in state.ProtectedData)
        {
            if (r.Offset < start || r.Offset > end - r.Length) throw new InvalidDataException("Invalid protected-data range.");
            while (index < state.Metadata.Count && state.Metadata[index].Offset + state.Metadata[index].Length <= r.Offset) index++;
            if (index < state.Metadata.Count && state.Metadata[index].Offset < r.Offset + r.Length)
                throw new InvalidDataException("Recovery metadata overlaps protected payload.");
        }
        var outside = new List<DiskRange>();
        if (start > 0) outside.Add(new(0, start)); if (end < diskSize) outside.Add(new(end, diskSize - end));
        long bytes = DirectLayout.Chunks(state.Metadata).Sum(r => checked(r.Length + RecordHeader));
        if (!outside.SequenceEqual(state.Outside) || bytes != state.UndoLength || bytes != state.Plan.JournalBytes ||
            bytes > state.Request.MaxJournalBytes || bytes > 16L * 1024 * 1024 * 1024 || bytes <= 0)
            throw new InvalidDataException("Recovery range/budget mismatch.");
    }
    private static IEnumerable<(long Offset, byte[] Bytes)> Records(FileStream file, DirectState state)
    {
        file.Position = 0;
        foreach (var r in DirectLayout.Chunks(state.Metadata))
        {
            byte[] header = new byte[RecordHeader]; file.ReadExactly(header);
            if (BinaryPrimitives.ReadInt64LittleEndian(header) != r.Offset || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != r.Length)
                throw new InvalidDataException("Journal address/length differs from metadata plan.");
            byte[] bytes = new byte[checked((int)r.Length)]; file.ReadExactly(bytes);
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            checksum.AppendData(header.AsSpan(0, 12)); checksum.AppendData(bytes);
            if (!checksum.GetHashAndReset().AsSpan().SequenceEqual(header.AsSpan(12))) throw new InvalidDataException("Undo record checksum mismatch.");
            yield return (r.Offset, bytes);
        }
        if (file.Position != file.Length) throw new InvalidDataException("Trailing undo bytes.");
    }
    internal static DirectState Restore(string directory, DirectState state, IIdentifiedDisk target, Action<string>? progress = null, FileStream? undo = null,
        Action<MigrationProgress>? telemetry = null)
    {
        if (state.Phase == "complete") throw new InvalidOperationException("Completed migrations cannot be rolled back by this command.");
        if (state.Phase == "rolled-back") return state;
        using var owned = undo == null ? OpenUndo(directory) : null; var file = undo ?? owned!;
        ValidateUndo(directory, state, target, file, telemetry);
        long primary = checked(state.Request.Destination.PartitionSector * 512 + 65536);
        byte[] dirty = Convert.FromBase64String(state.DirtyPrimaryCiphertext);
        if (dirty.Length != 512 || primary < 0 || primary > target.TotalSize - 512) throw new InvalidDataException("Invalid recovery dirty marker.");
        foreach (var r in state.Metadata)
            if (r.Offset < 0 || r.Offset > target.TotalSize - r.Length) throw new InvalidDataException("Recovery metadata is outside destination.");
        state = state with { Phase = "rolling-back" }; Save(directory, state);
        var meter = new ProgressMeter(telemetry, "rollback-metadata", "Restore metadata before-images", state.Metadata.Sum(r => r.Length));
        meter.Add(0, force: true);
        target.WriteBytes(primary, dirty); target.Flush(); // Keep the primary dirty until all before-images are restored.
        byte[]? cleanPrimary = null;
        foreach (var record in Records(file, state))
        {
            if (primary >= record.Offset && primary < record.Offset + record.Bytes.Length)
            {
                int p = checked((int)(primary - record.Offset)); cleanPrimary = record.Bytes.AsSpan(p, 512).ToArray();
                if (p > 0) target.WriteBytes(record.Offset, record.Bytes.AsSpan(0, p));
                if (p + 512 < record.Bytes.Length) target.WriteBytes(primary + 512, record.Bytes.AsSpan(p + 512));
            }
            else target.WriteBytes(record.Offset, record.Bytes);
            meter.Add(record.Bytes.Length);
            progress?.Invoke("Rollback metadata chunk");
        }
        target.Flush(); if (cleanPrimary == null) throw new InvalidDataException("Missing primary superblock before-image.");
        meter.Add(0, force: true);
        var readback = new ProgressMeter(telemetry, "rollback-readback", "Read back restored metadata", state.Metadata.Sum(r => r.Length));
        readback.Add(0, force: true);
        // Verify while still dirty. Do not expose corrupt restored maps as a clean FS.
        foreach (var record in Records(file, state))
        {
            if (primary >= record.Offset && primary < record.Offset + record.Bytes.Length)
                dirty.CopyTo(record.Bytes, checked((int)(primary - record.Offset)));
            if (!target.ReadBytes(record.Offset, record.Bytes.Length).AsSpan().SequenceEqual(record.Bytes))
                throw new IOException("Rollback metadata readback mismatch; journal retained.");
            readback.Add(record.Bytes.Length);
        }
        readback.Add(0, force: true);
        if (DirectLayout.HashRanges(target, state.ProtectedData, telemetry: telemetry, phase: "rollback-protected-data") != state.OriginalDataDigest ||
            DirectLayout.HashRanges(target, state.Outside, telemetry: telemetry, phase: "rollback-outside") != state.OutsideDigest)
            throw new IOException("Rollback protected data readback mismatch; journal retained.");
        target.WriteBytes(primary, cleanPrimary); target.Flush();
        if (!target.ReadBytes(primary, 512).AsSpan().SequenceEqual(cleanPrimary))
        {
            // Best effort: leave an unclean primary when the final marker readback fails.
            try { target.WriteBytes(primary, dirty); target.Flush(); } catch { }
            throw new IOException("Rollback clean marker readback mismatch; journal retained.");
        }
        state = state with { Phase = "rolled-back" }; Save(directory, state);
        MigrationTelemetry.Send(telemetry, new("rolled-back", "Rollback durable and verified")); return state;
    }
}

internal sealed class JournaledTarget(IIdentifiedDisk raw, UfsVolume baseline, List<DiskRange> metadata) : IDiskSource, IDurableDisk
{
    public long TotalSize => raw.TotalSize; public int SectorSize => 512; public long SectorCount => raw.SectorCount;
    public string Description => "Journaled direct destination"; public bool CanWrite => true;
    public byte[] ReadBytes(long offset, int count) => raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long sector, int count) => raw.ReadSectors(sector, count);
    public void WriteSectors(long sector, byte[] data) => WriteBytes(checked(sector * 512), data);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (offset % 512 != 0 || data.Length % 512 != 0 || offset < baseline.Offset ||
            offset > baseline.Offset + baseline.Size * baseline.Fragment - data.Length)
            throw new InvalidDataException("Write is unaligned or outside target UFS volume.");
        // Metadata must already have a durable before-image; data must have been FREE
        // in the immutable preflight bitmap, even after retiring the previous tree.
        for (long p = offset; p < offset + data.Length;)
        {
            int index = metadata.BinarySearch(new(p, 1), RangeStartComparer.Instance);
            if (index < 0) index = ~index - 1;
            if (index >= 0 && p < metadata[index].Offset + metadata[index].Length)
            { p = Math.Min(offset + data.Length, metadata[index].Offset + metadata[index].Length); continue; }
            long fragment = (p - baseline.Offset) / baseline.Fragment;
            if (!baseline.WasFree(fragment)) throw new InvalidDataException("Attempted overwrite of original allocated/reserved destination data.");
            p = Math.Min(offset + data.Length, baseline.Offset + (fragment + 1) * baseline.Fragment);
        }
        raw.WriteBytes(offset, data);
    }
    public void Flush() => raw.Flush(); public void Dispose() { }
    private sealed class RangeStartComparer : IComparer<DiskRange>
    { internal static readonly RangeStartComparer Instance = new(); public int Compare(DiskRange? x, DiskRange? y) => x!.Offset.CompareTo(y!.Offset); }
}
