using System.Security.Cryptography;
using System.Text.Json;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public static partial class DirectMigrator
{
    /// <summary>Restart full destination verification after a durable copy checkpoint.
    /// Never copies payloads or marks a destination clean before every check passes.</summary>
    public static DirectState ResumeVerification(string journalDirectory, CancellationToken token = default,
        Action<MigrationProgress>? telemetry = null) =>
        ResumeVerificationCore(journalDirectory, token, (e, w) => new DirectDisk(e, w), telemetry);

    internal static DirectState ResumeVerificationCore(string journalDirectory, CancellationToken token,
        Func<DirectEndpoint, bool, IIdentifiedDisk> open, Action<MigrationProgress>? telemetry = null)
    {
        token.ThrowIfCancellationRequested();
        journalDirectory = RegularImage.CheckedPath(journalDirectory, false);
        var state = DirectJournal.Load(journalDirectory);
        if (state.Phase is not ("verification-paused" or "verifying"))
            throw new InvalidOperationException("Only a durable verification checkpoint can be resumed.");
        var request = Normalize(state.Request);
        if (!request.JournalDirectory.Equals(journalDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Journal path differs from its pinned request.");
        string snapshot = state.VerificationSourceHashSha256 != null
            ? Path.Combine(journalDirectory, "verification-source-hashes.json")
            : request.SourceHashCache ?? throw new InvalidDataException("No pinned source hashes; verification resume refused.");
        string snapshotHash = state.VerificationSourceHashSha256 ?? request.SourceHashCacheSha256!;
        // Handles are exclusive. Re-read state after acquisition to reject a stale
        // checkpoint if a previous owner finished/recovered between these steps.
        using var sourceRaw = open(request.Source, false);
        using var targetRaw = open(request.Destination, true);
        if (sourceRaw.Identity != state.Plan.SourceIdentity || targetRaw.Identity != state.Plan.DestinationIdentity ||
            !JsonSerializer.SerializeToUtf8Bytes(state, ImageMigrator.Json).AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(DirectJournal.Load(journalDirectory), ImageMigrator.Json)))
            throw new InvalidDataException("Endpoint identity or checkpoint changed; resume refused.");
        using var undo = DirectJournal.OpenUndo(journalDirectory);
        DirectJournal.ValidateUndo(journalDirectory, state, targetRaw, undo, telemetry);
        token.ThrowIfCancellationRequested();
        using var preservation = request.TargetPreservation == null ? null : TargetPreservation.Open(request.TargetPreservation);
        preservation?.Check(token, telemetry);
        if (preservation?.Report != state.Plan.TargetPreservation)
            throw new InvalidDataException("Preservation archive differs from checkpoint.");
        using var sourceDisk = RegularImage.WrapCrypto(new BorrowedDisk(sourceRaw), request.Source.Image);
        using var sourceView = MigrationSource.Open(sourceDisk, request.Source.PartitionSector, request.SourceRecovery, token, telemetry);
        var source = sourceView.Volume;
        source.Inventory(false, token, telemetry, "resume-source-metadata");
        SourceHashSnapshot.Apply(source, snapshot, snapshotHash, token);
        if (source.RecoveryReport != state.Plan.SourceRecovery || DirectLayout.TreeDigest(source) != state.SourceDigest)
            throw new InvalidDataException("Source metadata/hash checkpoint changed.");
        var registry = Registries(request, source);
        if (Hash(registry.Source) != state.RegistrySha256 || Hash(registry.Target) != state.TargetRegistrySha256 ||
            registry.Restore != state.Plan.ConsoleStateRestoreRequired)
            throw new InvalidDataException("Console-state snapshots changed.");
        foreach (var bundle in new[] { ("xRegistry.sys", state.RegistrySha256), ("target-xRegistry.sys", state.TargetRegistrySha256) })
            if (bundle.Item2 != null && Hash(File.ReadAllBytes(RegularImage.CheckedPath(Path.Combine(journalDirectory, bundle.Item1)))) != bundle.Item2)
                throw new InvalidDataException("Console-state bundle checksum mismatch.");

        // Only clean-marker sectors can be written by this operation, and each
        // must have a validated undo before-image. Payload writes are impossible.
        using var commitDisk = new VerificationCommitDisk(targetRaw, state);
        using var targetDisk = RegularImage.WrapCrypto(commitDisk, request.Destination.Image);
        var target = new UfsVolume(targetDisk, request.Destination.PartitionSector, allowDirty: true);
        if (target.Super[0xD1] != 0) throw new InvalidDataException("Uncommitted destination must remain dirty.");
        try
        {
            state = state with { Phase = "verifying" }; DirectJournal.Save(journalDirectory, state);
            MigrationTelemetry.Send(telemetry, new("resume-verification", "Reuse validated source hashes; restart full destination verification"));
            target.Inventory(true, token, telemetry, "verify-destination");
            byte[] expected = Convert.FromBase64String(state.OriginalSuperblock), actual = (byte[])target.Super.Clone();
            expected[0xD1] = 0; Array.Clear(expected, 0x3F0, 40); Array.Clear(actual, 0x3F0, 40);
            if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("Destination superblock settings changed.");
            ImageMigrator.Compare(source, target, state.Mapping.ToDictionary(p => p.Source, p => p.Destination), token);
            if (DirectLayout.HashRanges(targetRaw, state.ProtectedData, token, telemetry, "verify-old-data") != state.OriginalDataDigest ||
                DirectLayout.HashRanges(targetRaw, state.Outside, token, telemetry, "verify-outside") != state.OutsideDigest)
                throw new InvalidDataException("Protected destination sectors changed.");
            token.ThrowIfCancellationRequested();
            state = state with { Phase = "committing", DestinationDigest = DirectLayout.TreeDigest(target) };
            DirectJournal.Save(journalDirectory, state);
            SetClean(target, true); RegularImage.Flush(targetDisk);
            if (!targetDisk.ReadBytes(target.Offset + 65536, 512).AsSpan().SequenceEqual(target.Super.AsSpan(0, 512)))
                throw new IOException("Clean marker readback failed after resume commit.");
            state = state with { Phase = "complete" }; DirectJournal.Save(journalDirectory, state);
            return state;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && state.Phase == "verifying")
        {
            DirectJournal.Save(journalDirectory, state with { Phase = "verification-paused" });
            throw new VerificationPausedException(token);
        }
        catch (Exception error) when (error is not SimulatedProcessLossException)
        {
            try { DirectJournal.Restore(journalDirectory, state, targetRaw, undo: undo, telemetry: telemetry); }
            catch (Exception recovery) { throw new AggregateException("Verification resume failed and rollback could not finish. Keep destination offline and retain journal.", error, recovery); }
            throw;
        }
    }
}

internal sealed class VerificationCommitDisk : IDiskSource, IDurableDisk
{
    readonly IIdentifiedDisk _raw;
    readonly HashSet<long> _markers = new();
    internal VerificationCommitDisk(IIdentifiedDisk raw, DirectState state)
    {
        _raw = raw; byte[] sb = Convert.FromBase64String(state.OriginalSuperblock);
        long start = checked(state.Request.Destination.PartitionSector * 512);
        _markers.Add(checked(start + 65536));
        for (int cg = 0; cg < UfsVolume.I32(sb, 0x2C); cg++)
            _markers.Add(checked(start + ((long)cg * UfsVolume.I32(sb, 0xBC) + UfsVolume.I32(sb, 8)) * UfsVolume.I32(sb, 0x34)));
        if (_markers.Any(at => !state.Metadata.Any(r => at >= r.Offset && at + 512 <= r.Offset + r.Length)))
            throw new InvalidDataException("Missing clean-marker before-image.");
    }
    public long TotalSize => _raw.TotalSize;
    public int SectorSize => 512;
    public long SectorCount => _raw.SectorCount;
    public string Description => "Verification-only commit guard";
    public bool CanWrite => true;
    public byte[] ReadBytes(long offset, int count) => _raw.ReadBytes(offset, count);
    public byte[] ReadSectors(long sector, int count) => _raw.ReadSectors(sector, count);
    public void WriteBytes(long offset, byte[] bytes) => WriteBytes(offset, bytes.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 512 || !_markers.Contains(offset)) throw new InvalidDataException("Verification resume cannot write payload or other metadata.");
        _raw.WriteBytes(offset, bytes);
    }
    public void WriteSectors(long sector, byte[] bytes) => WriteBytes(checked(sector * 512), bytes);
    public void Flush() => _raw.Flush();
    public void Dispose() { }
}
