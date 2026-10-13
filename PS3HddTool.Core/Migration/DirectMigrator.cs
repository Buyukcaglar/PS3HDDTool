using System.Security.Cryptography;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

// Tests throw this at a durable boundary to emulate process loss without automatic
// rollback. Production commands never create it; normal errors always try recovery.
internal sealed class SimulatedProcessLossException(string message) : IOException(message);

public sealed class VerificationPausedException(CancellationToken token)
    : OperationCanceledException("Destination verification paused; retain the offline destination and journal. Use direct-resume-verification to restart destination checks without copying again.", token);

public static partial class DirectMigrator
{
    private static DirectRequest Normalize(DirectRequest r)
    {
        MigrationSource.ValidateMode(r.SourceRecovery);
        MigrationSource.ValidateMode(r.TargetRecovery);
        if ((r.SourceHashCache == null) != (r.SourceHashCacheSha256 == null)) throw new ArgumentException("Source hash cache requires its pinned SHA256.");
        if (r.SourceHashCache != null && (r.Source.Kind != "image" || r.SourceHashCacheSha256!.Length != 64 || r.SourceHashCacheSha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsLower(c))))
            throw new ArgumentException("Source hash reuse requires a host image and uppercase SHA256.");
        if (r.SourceHashCache != null) r = r with { SourceHashCache = RegularImage.CheckedPath(r.SourceHashCache) };
        if(r.TargetRecovery is "detached-directory" && r.TargetPreservation==null)
            throw new ArgumentException("Target recovery requires a verified preservation archive.");
        if(r.TargetPreservation!=null)r=r with{TargetPreservation=TargetPreservation.DirectoryPath(r)};
        string journal = RegularImage.CheckedPath(r.JournalDirectory, false);
        if (r.MaxJournalBytes < 512 || r.MaxJournalBytes > 16L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(r.MaxJournalBytes));
        var result = r with { Source = r.Source.Normalize(), Destination = r.Destination.Normalize(), JournalDirectory = journal,
            SourceRegistry = r.SourceRegistry == null ? null : RegularImage.CheckedPath(r.SourceRegistry),
            TargetRegistry = r.TargetRegistry == null ? null : RegularImage.CheckedPath(r.TargetRegistry) };
        if (result.Source.Path.Equals(result.Destination.Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source and destination are the same endpoint.");
        ImageMigrator.RequireSameEncryption(new(result.Source.Image, result.Destination.Image, journal));
        return result;
    }
    public static DirectPlan Plan(DirectRequest request, CancellationToken token = default, Action<MigrationProgress>? telemetry = null)
    {
        token.ThrowIfCancellationRequested();
        request = Normalize(request);
        using var sourceRaw = new DirectDisk(request.Source, false); using var targetRaw = new DirectDisk(request.Destination, false);
        using var sourceDisk = RegularImage.WrapCrypto(new BorrowedDisk(sourceRaw), request.Source.Image);
        using var targetDisk = RegularImage.WrapCrypto(new BorrowedDisk(targetRaw), request.Destination.Image);
        using var sourceView = MigrationSource.Open(sourceDisk, request.Source.PartitionSector, request.SourceRecovery, token, telemetry);
        var source = sourceView.Volume; var target = new UfsVolume(targetDisk, request.Destination.PartitionSector);
        source.Inventory(false, token, telemetry, "source");
        if(request.SourceHashCache!=null)SourceHashSnapshot.Apply(source,request.SourceHashCache,request.SourceHashCacheSha256!,token);
        TargetPreservation.Inventory(target,request.TargetRecovery,token,telemetry);
        using var preservation=request.TargetPreservation==null?null:TargetPreservation.Open(request.TargetPreservation);
        preservation?.ValidateLive(targetRaw,target,request.Destination.Crypto,request.TargetRecovery,token,telemetry);
        var registry = Registries(request, source);
        var layout = DirectLayout.Plan(source, target, token, telemetry,request.TargetRecovery);
        return MakePlan(request, sourceRaw, targetRaw, source, target, layout.Metadata, registry.Restore,preservation?.Report);
    }
    private static DirectPlan MakePlan(DirectRequest request, IIdentifiedDisk sourceRaw, IIdentifiedDisk targetRaw,
        UfsVolume source, UfsVolume target, List<DiskRange> metadata, bool restore,TargetPreservationReport? preservation=null)
    {
        if (sourceRaw.Identity == targetRaw.Identity) throw new ArgumentException("Source and destination resolve to the same handle identity.");
        if (targetRaw.TotalSize < sourceRaw.TotalSize || target.Size * target.Fragment < source.Size * source.Fragment)
            throw new IOException("Direct migration refuses a smaller destination disk or volume. Check source/destination roles.");
        if (target.Fragment % targetRaw.PhysicalWriteUnit != 0 || target.Offset % targetRaw.PhysicalWriteUnit != 0)
            throw new NotSupportedException("Destination fragments/partition must align to physical write units to isolate retained old payloads.");
        if (source.Block != target.Block || source.Fragment != target.Fragment) throw new NotSupportedException("Block/fragment geometry differs.");
        long required = checked(source.Nodes.Values.Sum(n => n.Sectors) * 512 / source.Fragment);
        long free = checked(UfsVolume.I64(target.Super, 0x3F8) * target.Fpb + UfsVolume.I64(target.Super, 0x408));
        long journal = DirectLayout.Chunks(metadata).Sum(r => checked(r.Length + DirectJournal.RecordHeader));
        // Unlike image replacement, old destination data is NOT reclaimed for copying.
        if (required > free || source.Nodes.Count > (long)target.Ncg * target.Ipg - 2) throw new IOException("Insufficient originally free destination space/inodes for recoverable direct migration.");
        if (journal > request.MaxJournalBytes) throw new IOException($"Metadata undo requires {journal} bytes, exceeding the configured journal limit.");
        return new(sourceRaw.Identity, targetRaw.Identity, journal, required, free, source.Nodes.Count, restore, source.RecoveryReport,preservation);
    }
    private static (byte[]? Source, byte[]? Target, bool Restore) Registries(DirectRequest request, UfsVolume source)
    {
        byte[]? a = request.SourceRegistry == null ? null : File.ReadAllBytes(request.SourceRegistry);
        byte[]? b = request.TargetRegistry == null ? null : File.ReadAllBytes(request.TargetRegistry);
        ImageMigrator.ValidateUsers(source, a); if (b != null) _ = ConsoleState.ReadUsers(b);
        return (a, b, a != null && (b == null || !a.AsSpan().SequenceEqual(b)));
    }
    public static DirectState Run(DirectRequest request, Action<string>? progress = null, CancellationToken token = default,
        Action<MigrationProgress>? telemetry = null) =>
        RunCore(request, progress, token, (e, w) => new DirectDisk(e, w), telemetry: telemetry);

    internal static DirectState RunCore(DirectRequest request, Action<string>? progress, CancellationToken token,
        Func<DirectEndpoint, bool, IIdentifiedDisk> open, Action<string, DirectState>? persist = null,
        Action<MigrationProgress>? telemetry = null)
    {
        token.ThrowIfCancellationRequested();
        void Save(DirectState snapshot) => (persist ?? DirectJournal.Save)(request.JournalDirectory, snapshot);
        request = Normalize(request);
        if (Directory.Exists(request.JournalDirectory) || File.Exists(request.JournalDirectory)) throw new IOException("Journal directory must not exist. Recover unfinished sessions before restarting.");
        if (!Directory.Exists(Path.GetDirectoryName(request.JournalDirectory))) throw new DirectoryNotFoundException("Journal parent directory must exist.");
        using var sourceRaw = open(request.Source, false); using var targetRaw = open(request.Destination, true);
        using var sourceDisk = RegularImage.WrapCrypto(new BorrowedDisk(sourceRaw), request.Source.Image);
        using var targetDisk = RegularImage.WrapCrypto(new BorrowedDisk(targetRaw), request.Destination.Image);
        Step("Preflight direct migration");
        using var sourceView = MigrationSource.Open(sourceDisk, request.Source.PartitionSector, request.SourceRecovery, token, telemetry);
        var source = sourceView.Volume; var baseline = new UfsVolume(targetDisk, request.Destination.PartitionSector);
        source.Inventory(request.SourceHashCache==null, token, telemetry, "source");
        if(request.SourceHashCache!=null){SourceHashSnapshot.Apply(source,request.SourceHashCache,request.SourceHashCacheSha256!,token);Step("Reuse validated source hashes; contents checked during copy");}
        TargetPreservation.Inventory(baseline,request.TargetRecovery,token,telemetry);
        using var preservation=request.TargetPreservation==null?null:TargetPreservation.Open(request.TargetPreservation);
        preservation?.ValidateLive(targetRaw,baseline,request.Destination.Crypto,request.TargetRecovery,token,telemetry);
        var registry = Registries(request, source); var layout = DirectLayout.Plan(source, baseline, token, telemetry,request.TargetRecovery);
        var plan = MakePlan(request, sourceRaw, targetRaw, source, baseline, layout.Metadata, registry.Restore,preservation?.Report);
        var oldData = DirectLayout.DataRanges(baseline); var outside = DirectLayout.Outside(baseline);
        string oldDigest = DirectLayout.HashRanges(targetRaw, oldData, token, telemetry, "protect-old-data"), outsideDigest = DirectLayout.HashRanges(targetRaw, outside, token, telemetry, "protect-outside");
        byte[] dirty = baseline.Super.AsSpan(0, 512).ToArray(); dirty[0xD1] = 0;
        if (targetDisk is IPipelinedDiskSource cipher)
        { byte[] encrypted = new byte[512]; cipher.EncryptForWrite(dirty, 512, baseline.Offset + 65536, encrypted); dirty = encrypted; }
        Step("Snapshot destination metadata"); Directory.CreateDirectory(request.JournalDirectory);
        string undoHash = DirectJournal.SaveUndo(request.JournalDirectory, targetRaw, layout.Metadata, progress, token, telemetry);
        Bundle("xRegistry.sys", registry.Source); Bundle("target-xRegistry.sys", registry.Target);
        var state = new DirectState(1, request, plan, "prepared", DirectLayout.TreeDigest(source), oldDigest, outsideDigest,
            layout.Metadata, oldData, outside, undoHash, plan.JournalBytes, Convert.ToBase64String(dirty), Convert.ToBase64String(baseline.Super),
            layout.Mapping.Select(p => new InodeMapping(p.Key, p.Value)).OrderBy(p => p.Source).ToList(), Hash(registry.Source), Hash(registry.Target));
        state = state with { VerificationSourceHashSha256 = SourceHashSnapshot.Save(source, Path.Combine(request.JournalDirectory, "verification-source-hashes.json")) };
        Save(state);
        // Retain one handle through copy/commit/rollback. Deny writes and deletion
        // of the only before-images, and never reopen a potentially replaced path.
        using var undo = DirectJournal.OpenUndo(request.JournalDirectory);
        // Validate the durable snapshot completely before allowing the first destination write.
        DirectJournal.ValidateUndo(request.JournalDirectory, state, targetRaw, undo, telemetry);
        using var guardedDisk = RegularImage.WrapCrypto(new JournaledTarget(targetRaw, baseline, layout.Metadata), request.Destination.Image);
        var destination = new UfsVolume(guardedDisk, request.Destination.PartitionSector); TargetPreservation.Inventory(destination,request.TargetRecovery,token,telemetry);
        try
        {
            state = state with { Phase = "copying" }; Save(state);
            Step("Mark destination filesystem dirty"); SetClean(destination, false); RegularImage.Flush(guardedDisk);
            Step("Destination dirty marker durable");
            destination.ReleaseOldTree(false);
            var mapping = TreeTransfer.Copy(source, destination, progress, token, telemetry);
            if (!mapping.OrderBy(p => p.Key).SequenceEqual(layout.Mapping.OrderBy(p => p.Key))) throw new InvalidDataException("Allocation differs from journal metadata plan.");
            RegularImage.Flush(guardedDisk); Step("Direct data copy durable");
            Step("Rebuild allocation summaries"); destination.ReleaseRetiredData(); destination.RecalculateAndFlush();
            state = state with { Phase = "verifying" }; Save(state);
            Step("Verify direct destination");
            var result = new UfsVolume(guardedDisk, request.Destination.PartitionSector, allowDirty: true); result.Inventory(true, token, telemetry, "verify-destination");
            if (result.Super[0xD1] != 0) throw new InvalidDataException("Destination became clean before verification.");
            byte[] original = (byte[])baseline.Super.Clone(); baseline.Super[0xD1] = 0;
            try { ImageMigrator.CompareSuperblock(baseline, result); } finally { original.CopyTo(baseline.Super, 0); }
            ImageMigrator.Compare(source, result, mapping, token);
            if (DirectLayout.HashRanges(targetRaw, oldData, token, telemetry, "verify-old-data") != oldDigest || DirectLayout.HashRanges(targetRaw, outside, token, telemetry, "verify-outside") != outsideDigest)
                throw new InvalidDataException("Protected destination sectors changed.");
            state = state with { Phase = "committing", DestinationDigest = DirectLayout.TreeDigest(result) };
            Save(state); Step("Commit verified direct destination");
            SetClean(destination, true); RegularImage.Flush(guardedDisk);
            if (!guardedDisk.ReadBytes(baseline.Offset + 65536, 512).AsSpan().SequenceEqual(destination.Super.AsSpan(0, 512)))
                throw new IOException("Clean marker sector readback failed after flush.");
            Step("Direct commit durable");
            var complete = state with { Phase = "complete" }; Save(complete); state = complete;
            return state;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && state.Phase == "verifying")
        {
            // Copy and allocation summaries are durable. Cancellation during a
            // read-only verification phase must not destroy the copied tree.
            Save(state with { Phase = "verification-paused" });
            throw new VerificationPausedException(token);
        }
        catch (Exception error) when (error is not SimulatedProcessLossException)
        {
            try { DirectJournal.Restore(request.JournalDirectory, state, targetRaw, undo: undo, telemetry: telemetry); }
            catch (Exception recovery) { throw new AggregateException("Direct migration failed and rollback could not finish. Keep the destination offline and retain the journal for direct-recover.", error, recovery); }
            throw;
        }
        void Step(string stage) { token.ThrowIfCancellationRequested(); MigrationTelemetry.Send(telemetry, new(stage, stage)); progress?.Invoke(stage); token.ThrowIfCancellationRequested(); }
        void Bundle(string name, byte[]? bytes)
        {
            if (bytes == null) return;
            using var f = new FileStream(Path.Combine(request.JournalDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            f.Write(bytes); f.Flush(true);
        }
    }
    private static void SetClean(UfsVolume destination, bool clean)
    {
        if (!clean)
        {
            destination.Super[0xD1] = 0;
            WriteMarker(destination.Offset + 65536, destination.Super.AsSpan(0, 512).ToArray());
            RegularImage.Flush(destination.Disk);
        }
        // Backup copies remain in their original layout; only their clean byte changes.
        var backups = new List<(long Offset, byte[] Bytes)>();
        foreach (Group g in destination.Groups)
        {
            long at = destination.Offset + ((long)g.Number * destination.Fpg + UfsVolume.I32(destination.Super, 8)) * destination.Fragment;
            byte[] backup = destination.Disk.ReadBytes(at, 512); backup[0xD1] = clean ? (byte)1 : (byte)0;
            WriteMarker(at, backup);
            backups.Add((at, backup));
        }
        RegularImage.Flush(destination.Disk); // Backups durable before the primary can become clean.
        foreach (var backup in backups)
            if (!destination.Disk.ReadBytes(backup.Offset, 512).AsSpan().SequenceEqual(backup.Bytes))
                throw new IOException("Backup marker sector readback mismatch after flush.");
        // Primary is dirtied FIRST, but made clean LAST. Crash recovery writes it last too.
        destination.Super[0xD1] = clean ? (byte)1 : (byte)0;
        WriteMarker(destination.Offset + 65536, destination.Super.AsSpan(0, 512).ToArray());
        void WriteMarker(long at, byte[] bytes)
        {
            RegularImage.Write(destination.Disk, at, bytes);
            if (!destination.Disk.ReadBytes(at, 512).AsSpan().SequenceEqual(bytes))
                throw new IOException("Superblock marker sector readback mismatch.");
        }
    }
    /// <summary>Read the checksummed journal snapshot only. Does not open any disk or key.
    /// A saved phase is not proof of process liveness or recovery integrity.</summary>
    public static DirectState Status(string journalDirectory) => DirectJournal.Load(RegularImage.CheckedPath(journalDirectory, false));

    public static DirectState Recover(string journalDirectory, Action<MigrationProgress>? telemetry = null)
    {
        journalDirectory = RegularImage.CheckedPath(journalDirectory, false); var state = DirectJournal.Load(journalDirectory);
        if (state.Phase == "rolled-back") return state;
        if (state.Phase == "complete") throw new InvalidOperationException("Completed migration cannot be recovered/rolled back.");
        // Undo stores raw ciphertext and needs no source drive or encryption keys.
        using var target = new DirectDisk(state.Request.Destination with { KeyFile = null }, true);
        return DirectJournal.Restore(journalDirectory, state, target, telemetry: telemetry);
    }
    public static DirectState Verify(string journalDirectory, CancellationToken token = default, Action<MigrationProgress>? telemetry = null)
    {
        token.ThrowIfCancellationRequested();
        journalDirectory = RegularImage.CheckedPath(journalDirectory, false); var state = DirectJournal.Load(journalDirectory);
        if (state.Phase != "complete") throw new InvalidOperationException("Session is not complete; recover it before starting again.");
        var request = Normalize(state.Request);
        using var preservation=request.TargetPreservation==null?null:TargetPreservation.Open(request.TargetPreservation);
        preservation?.Check(token,telemetry);
        if(preservation?.Report!=state.Plan.TargetPreservation)throw new InvalidDataException("Preservation archive differs from completed session.");
        using var sourceRaw = new DirectDisk(request.Source, false); using var targetRaw = new DirectDisk(request.Destination, false);
        if (sourceRaw.Identity != state.Plan.SourceIdentity || targetRaw.Identity != state.Plan.DestinationIdentity)
            throw new InvalidDataException("Source/destination identity differs from the completed session.");
        using var sourceDisk = RegularImage.WrapCrypto(new BorrowedDisk(sourceRaw), request.Source.Image);
        using var targetDisk = RegularImage.WrapCrypto(new BorrowedDisk(targetRaw), request.Destination.Image);
        using var sourceView = MigrationSource.Open(sourceDisk, request.Source.PartitionSector, request.SourceRecovery, token, telemetry);
        var source = sourceView.Volume; source.Inventory(true, token, telemetry, "verify-source");
        if (source.RecoveryReport != state.Plan.SourceRecovery) throw new InvalidDataException("Source recovery evidence changed.");
        var target = new UfsVolume(targetDisk, request.Destination.PartitionSector); target.Inventory(true, token, telemetry, "verify-destination");
        byte[] expectedSuper = Convert.FromBase64String(state.OriginalSuperblock), actualSuper = (byte[])target.Super.Clone();
        Array.Clear(expectedSuper, 0x3F0, 40); Array.Clear(actualSuper, 0x3F0, 40);
        if (!expectedSuper.AsSpan().SequenceEqual(actualSuper)) throw new InvalidDataException("Destination superblock settings changed.");
        var bundled = ReadBundle("xRegistry.sys", state.RegistrySha256); var targetBundle = ReadBundle("target-xRegistry.sys", state.TargetRegistrySha256);
        var registry = Registries(request, source);
        if (Hash(registry.Source) != state.RegistrySha256 || Hash(registry.Target) != state.TargetRegistrySha256 || registry.Restore != state.Plan.ConsoleStateRestoreRequired)
            throw new InvalidDataException("Console-state snapshots/readiness changed.");
        if (DirectLayout.TreeDigest(source) != state.SourceDigest) throw new InvalidDataException("Source tree changed since migration.");
        if (DirectLayout.TreeDigest(target) != state.DestinationDigest) throw new InvalidDataException("Destination tree digest mismatch.");
        ImageMigrator.Compare(source, target, state.Mapping.ToDictionary(p => p.Source, p => p.Destination), token);
        if (DirectLayout.HashRanges(targetRaw, state.Outside, token, telemetry, "verify-outside") != state.OutsideDigest) throw new InvalidDataException("Sector outside selected destination volume changed.");
        return state;
        byte[]? ReadBundle(string name, string? hash)
        {
            if (hash == null) return null;
            byte[] bytes = File.ReadAllBytes(RegularImage.CheckedPath(Path.Combine(journalDirectory, name)));
            if (Hash(bytes) != hash) throw new InvalidDataException("Console-state bundle digest mismatch."); return bytes;
        }
    }
    private static string? Hash(byte[]? bytes) => bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
}
