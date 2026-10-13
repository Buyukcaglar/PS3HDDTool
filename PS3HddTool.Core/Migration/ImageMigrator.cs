using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PS3HddTool.Core.Migration;

public sealed record MigrationRequest(ImageSpec Source, ImageSpec DestinationTemplate, string OutputDirectory,
    string? SourceRegistry = null, string? TargetRegistry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRecovery = null);
public sealed record InodeMapping(long Source, long Destination);
public sealed record MigrationManifest(int FormatVersion, MigrationRequest Request, int Nodes,
    List<InodeMapping> Mapping, string ImageSha256, string? RegistrySha256,
    bool ConsoleStateRestoreRequired, bool ConsoleRuntimeValidated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceRecoveryReport? SourceRecovery = null);

/// <summary>Transactional offline UFS2 tree migration. Never opens a physical device.</summary>
public static class ImageMigrator
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // Progress is synchronous; throwing (including cancellation) prevents publication.
    public static MigrationManifest Prepare(MigrationRequest request, Action<string>? progress = null,
        CancellationToken token = default, Action<MigrationProgress>? telemetry = null)
    {
        token.ThrowIfCancellationRequested();
        MigrationSource.ValidateMode(request.SourceRecovery);
        string output = RegularImage.CheckedPath(request.OutputDirectory, false);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output must not exist; existing packages are never overwritten.");
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output needs a parent directory.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Create the output parent directory first.");
        request = request with {
            Source = request.Source with { Path = RegularImage.CheckedPath(request.Source.Path) },
            DestinationTemplate = request.DestinationTemplate with { Path = RegularImage.CheckedPath(request.DestinationTemplate.Path) },
            OutputDirectory = output,
            SourceRegistry = request.SourceRegistry == null ? null : RegularImage.CheckedPath(request.SourceRegistry),
            TargetRegistry = request.TargetRegistry == null ? null : RegularImage.CheckedPath(request.TargetRegistry)
        };
        if (request.Source.Path.Equals(request.DestinationTemplate.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and destination template must be different files.");
        RequireSameEncryption(request);
        using var sourceDisk = RegularImage.Open(request.Source);
        using var templateDisk = RegularImage.Open(request.DestinationTemplate);
        using var sourceView = MigrationSource.Open(sourceDisk, request.Source.PartitionSector, request.SourceRecovery, token, telemetry);
        var source = sourceView.Volume;
        var template = new UfsVolume(templateDisk, request.DestinationTemplate.PartitionSector);
        if (source.Block != template.Block || source.Fragment != template.Fragment)
            throw new NotSupportedException("Source and destination must have identical block/fragment sizes.");
        Step("Inventory source and template"); source.Inventory(false, token, telemetry, "source"); template.Inventory(false, token, telemetry, "template");
        byte[]? registry = request.SourceRegistry == null ? null : File.ReadAllBytes(request.SourceRegistry);
        ValidateUsers(source, registry);
        byte[]? targetRegistry = request.TargetRegistry == null ? null : File.ReadAllBytes(request.TargetRegistry);
        if (targetRegistry != null) _ = ConsoleState.ReadUsers(targetRegistry);
        bool restore = registry != null && (targetRegistry == null || !registry.AsSpan().SequenceEqual(targetRegistry));
        long available = checked(UfsVolume.I64(template.Super, 0x3F8) * template.Fpb + UfsVolume.I64(template.Super, 0x408) +
            template.Nodes.Values.Sum(n => n.Sectors) * 512 / template.Fragment);
        long needed = checked(source.Nodes.Values.Sum(n => n.Sectors) * 512 / source.Fragment);
        if (source.Nodes.Count > (long)template.Ncg * template.Ipg - 2 || needed > available)
            throw new IOException("Destination template has insufficient inode/data capacity.");
        Step("Hash source files");
        var sourceHashes = new ProgressMeter(telemetry, "source-hash", "Hash logical file contents", source.Nodes.Values.Where(n => n.Type is 8 or 10).Sum(n => n.Size));
        foreach (Node n in source.Nodes.Values.Where(n => n.Type is 8 or 10)) n.Hash = source.Hash(n, token, sourceHashes);
        sourceHashes.Add(0, force: true);
        string temp = Path.Combine(parent, ".ps3-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool published = false;
        try
        {
            string image = Path.Combine(temp, "disk.img");
            Step("Copy destination template");
            using (var input = new FileStream(request.DestinationTemplate.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = new FileStream(image, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024]; long remaining = input.Length;
                var meter = new ProgressMeter(telemetry, "copy-template", "Copy complete template image", remaining); meter.Add(0, force: true);
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int n = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (n == 0) throw new EndOfStreamException("Template truncated during copy.");
                    copy.Write(buffer, 0, n); remaining -= n;
                    meter.Add(n);
                }
                copy.Flush(true);
                meter.Add(0, force: true);
            }
            var workingSpec = request.DestinationTemplate with { Path = image };
            var mapping = new Dictionary<long, long>();
            using (var workingDisk = RegularImage.Open(workingSpec, true))
            {
                var destination = new UfsVolume(workingDisk, workingSpec.PartitionSector);
                destination.Inventory(false, token, telemetry, "working-template"); destination.ReleaseOldTree();
                mapping = TreeTransfer.Copy(source, destination, progress, token, telemetry);
                Step("Rebuild allocation summaries"); destination.RecalculateAndFlush();
            }
            Step("Verify copied tree and allocation maps");
            using (var resultDisk = RegularImage.Open(workingSpec))
            {
                var result = new UfsVolume(resultDisk, workingSpec.PartitionSector); result.Inventory(true, token, telemetry, "verify-destination");
                CompareSuperblock(template, result);
                Compare(source, result, mapping, token);
            }
            // No partition table, other partition, or sector outside the UFS volume may change.
            Step("Verify other partitions");
            CompareOutside(request.DestinationTemplate.Path, image, template.Offset,
                checked(template.Offset + template.Size * template.Fragment), token);
            if (registry != null) DurableWrite(Path.Combine(temp, "xRegistry.sys"), registry);
            Step("Hash completed image"); string hash = HashFile(image, token, telemetry);
            var manifest = new MigrationManifest(1, request, source.Nodes.Count,
                mapping.Select(p => new InodeMapping(p.Key, p.Value)).OrderBy(p => p.Source).ToList(), hash,
                registry == null ? null : Convert.ToHexString(SHA256.HashData(registry)), restore, SourceRecovery: source.RecoveryReport);
            DurableWrite(Path.Combine(temp, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            Step("Publish verified package");
            Directory.Move(temp, output); published = true;
            return manifest;
        }
        finally
        {
            // Only this invocation's checked, uniquely named staging directory is removed.
            if (!published && Directory.Exists(temp))
            {
                RegularImage.CheckedPath(temp, false);
                foreach (string file in Directory.GetFiles(temp)) { RegularImage.CheckedPath(file); File.Delete(file); }
                Directory.Delete(temp, false);
            }
        }
        void Step(string stage) { token.ThrowIfCancellationRequested(); MigrationTelemetry.Send(telemetry, new(stage, stage)); progress?.Invoke(stage); token.ThrowIfCancellationRequested(); }
    }

    public static MigrationManifest Verify(string package, CancellationToken token = default, Action<MigrationProgress>? telemetry = null)
    {
        token.ThrowIfCancellationRequested();
        package = RegularImage.CheckedPath(package, false);
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllBytes(RegularImage.CheckedPath(Path.Combine(package, "manifest.json"))), Json)
            ?? throw new InvalidDataException("Missing manifest.");
        if (manifest.FormatVersion != 1 || manifest.ConsoleRuntimeValidated) throw new InvalidDataException("Unsupported manifest.");
        string image = RegularImage.CheckedPath(Path.Combine(package, "disk.img"));
        RequireSameEncryption(manifest.Request);
        using var input = RegularImage.Open(manifest.Request.Source);
        using var output = RegularImage.Open(manifest.Request.DestinationTemplate with { Path = image });
        using var templateDisk = RegularImage.Open(manifest.Request.DestinationTemplate);
        if (HashFile(image, token, telemetry) != manifest.ImageSha256) throw new InvalidDataException("Output image digest mismatch.");
        byte[]? registry = manifest.RegistrySha256 == null ? null : File.ReadAllBytes(RegularImage.CheckedPath(Path.Combine(package, "xRegistry.sys")));
        if (registry != null && Convert.ToHexString(SHA256.HashData(registry)) != manifest.RegistrySha256)
            throw new InvalidDataException("Registry snapshot digest mismatch.");
        byte[]? originalRegistry = manifest.Request.SourceRegistry == null ? null :
            File.ReadAllBytes(RegularImage.CheckedPath(manifest.Request.SourceRegistry));
        if ((registry == null) != (originalRegistry == null) ||
            (registry != null && !registry.AsSpan().SequenceEqual(originalRegistry)))
            throw new InvalidDataException("Bundled registry differs from the source snapshot.");
        byte[]? targetRegistry = manifest.Request.TargetRegistry == null ? null :
            File.ReadAllBytes(RegularImage.CheckedPath(manifest.Request.TargetRegistry));
        if (targetRegistry != null) _ = ConsoleState.ReadUsers(targetRegistry);
        bool restore = registry != null && (targetRegistry == null || !registry.AsSpan().SequenceEqual(targetRegistry));
        if (manifest.ConsoleStateRestoreRequired != restore) throw new InvalidDataException("Console-state readiness does not match supplied snapshots.");
        using var sourceView = MigrationSource.Open(input, manifest.Request.Source.PartitionSector, manifest.Request.SourceRecovery, token, telemetry);
        var source = sourceView.Volume;
        if (source.RecoveryReport != manifest.SourceRecovery) throw new InvalidDataException("Source recovery evidence changed.");
        var result = new UfsVolume(output, manifest.Request.DestinationTemplate.PartitionSector);
        var template = new UfsVolume(templateDisk, manifest.Request.DestinationTemplate.PartitionSector);
        if (source.Block != result.Block || source.Fragment != result.Fragment)
            throw new InvalidDataException("Verification geometry mismatch.");
        CompareSuperblock(template, result);
        source.Inventory(true, token, telemetry, "verify-source"); result.Inventory(true, token, telemetry, "verify-destination"); ValidateUsers(source, registry);
        if (source.Nodes.Count != manifest.Nodes) throw new InvalidDataException("Manifest node count mismatch.");
        Compare(source, result, manifest.Mapping.ToDictionary(p => p.Source, p => p.Destination), token);
        CompareOutside(manifest.Request.DestinationTemplate.Path, image, template.Offset,
            checked(template.Offset + template.Size * template.Fragment), token);
        return manifest;
    }

    internal static void RequireSameEncryption(MigrationRequest request)
    {
        RegularImage.ValidateKeyKind(request.Source);RegularImage.ValidateKeyKind(request.DestinationTemplate);
        if (request.Source.Crypto != request.DestinationTemplate.Crypto)
            throw new NotSupportedException("Only same-console migration with matching encryption is supported.");
        if (request.Source.Crypto == "plain") return; // Plain images provide no console identity proof.
        if (request.Source.KeyFile == null || request.DestinationTemplate.KeyFile == null)
            throw new ArgumentException("Encrypted images require key files.");
        byte[] a = RegularImage.ReadKey(request.Source);
        byte[]? b = null;
        try { b=RegularImage.ReadKey(request.DestinationTemplate);if (!CryptographicOperations.FixedTimeEquals(a, b)) throw new NotSupportedException("Source and target encryption keys differ; cross-console migration is unsupported."); }
        finally { CryptographicOperations.ZeroMemory(a);if(b!=null)CryptographicOperations.ZeroMemory(b); }
    }

    internal static void CompareSuperblock(UfsVolume template, UfsVolume result)
    {
        byte[] a = (byte[])template.Super.Clone(), b = (byte[])result.Super.Clone();
        Array.Clear(a, 0x3F0, 40); Array.Clear(b, 0x3F0, 40);
        if (!a.AsSpan().SequenceEqual(b)) throw new InvalidDataException("Destination superblock geometry/settings changed.");
    }

    internal static void ValidateUsers(UfsVolume source, byte[]? registry)
    {
        var home = source.Nodes[2].Entries.SingleOrDefault(e => e.Text == "home");
        var users = registry == null ? Array.Empty<UserIdentity>() : ConsoleState.ReadUsers(registry).ToArray();
        if (home == null) return;
        if (source.Nodes[home.Inode].Type != 4) throw new InvalidDataException("home is not a directory.");
        foreach (var entry in source.Nodes[home.Inode].Entries.Where(e => e.Text is not ("." or "..")))
        {
            if (source.Nodes[entry.Inode].Type != 4 || entry.Text.Length != 8 || !entry.Text.All(char.IsAsciiDigit))
                throw new InvalidDataException("Unsupported home entry; expected eight-digit user directories.");
            if (!users.Any(u => u.UserNumber == entry.Text))
                throw new InvalidDataException("A complete source xRegistry.sys snapshot matching every HDD user is required.");
        }
    }

    internal static void RewriteDirectory(byte[] b, int length, Dictionary<long, long> mapping)
    {
        for (int p = 0; p < length;)
        {
            uint old = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            if (old != 0) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), checked((uint)mapping[old]));
            p += BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 4));
        }
    }
    internal static void Compare(UfsVolume source, UfsVolume result, Dictionary<long, long> mapping, CancellationToken token)
    {
        if (source.Nodes.Count != result.Nodes.Count || mapping.Count != source.Nodes.Count ||
            mapping.Values.Distinct().Count() != mapping.Count || mapping.GetValueOrDefault(2) != 2)
            throw new InvalidDataException("Tree cardinality/root/hard-link mapping mismatch.");
        foreach (Node n in source.Nodes.Values)
        {
            token.ThrowIfCancellationRequested(); Node d = result.Nodes[mapping[n.Number]];
            byte[] a = Metadata(n), b = Metadata(d);
            if (!a.AsSpan().SequenceEqual(b) || n.Hash != d.Hash) throw new InvalidDataException($"Inode {n.Number} metadata/content mismatch.");
            if (n.Type == 4)
            {
                var expected = n.Entries.Select(e => (e.Key, Inode: mapping[e.Inode], e.Type)).OrderBy(e => e.Key, StringComparer.Ordinal);
                var actual = d.Entries.Select(e => (e.Key, Inode: (long)e.Inode, e.Type)).OrderBy(e => e.Key, StringComparer.Ordinal);
                if (!expected.SequenceEqual(actual)) throw new InvalidDataException("Directory topology/name mismatch.");
            }
            int ea = UfsVolume.I32(n.Raw, 0x5C);
            for (int i = 0; i * source.Block < ea; i++)
            {
                int count = Math.Min(source.Block, ea - i * source.Block);
                if (!source.Read(UfsVolume.I64(n.Raw, 0x60 + i * 8), count).AsSpan().SequenceEqual(
                    result.Read(UfsVolume.I64(d.Raw, 0x60 + i * 8), count))) throw new InvalidDataException("Extended attribute mismatch.");
            }
            // Preserve the location of holes, not just their zero-byte contents.
            if (!source.NonzeroBlocks(n).Select(x => x.Logical).SequenceEqual(result.NonzeroBlocks(d).Select(x => x.Logical)))
                throw new InvalidDataException("Sparse extent topology mismatch.");
        }
    }
    private static byte[] Metadata(Node n)
    {
        byte[] b = (byte[])n.Raw.Clone(); Array.Clear(b, 0x18, 8); Array.Clear(b, 0x60, 16);
        if (!n.Inline && n.Type is 4 or 8 or 10) Array.Clear(b, 0x70, 120);
        return b;
    }
    private static void CompareOutside(string template, string image, long first, long end, CancellationToken token)
    {
        using var a = File.OpenRead(template); using var b = File.OpenRead(image);
        if (a.Length != b.Length) throw new InvalidDataException("Output image length changed.");
        byte[] x = new byte[1024 * 1024], y = new byte[x.Length];
        foreach (var range in new[] { (Start: 0L, End: first), (Start: end, End: a.Length) })
            for (long at = range.Start; at < range.End;)
            {
                token.ThrowIfCancellationRequested(); int length = (int)Math.Min(x.Length, range.End - at);
                a.Position = b.Position = at; a.ReadExactly(x.AsSpan(0, length)); b.ReadExactly(y.AsSpan(0, length));
                if (!x.AsSpan(0, length).SequenceEqual(y.AsSpan(0, length))) throw new InvalidDataException("Sector outside target UFS volume changed.");
                at += length;
            }
    }
    private static string HashFile(string path, CancellationToken token, Action<MigrationProgress>? telemetry = null)
    {
        using var f = File.OpenRead(RegularImage.CheckedPath(path)); using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var meter = new ProgressMeter(telemetry, "image-hash", "Hash complete output image", f.Length); meter.Add(0, force: true);
        byte[] b = new byte[1024 * 1024]; int n;
        while ((n = f.Read(b)) > 0) { token.ThrowIfCancellationRequested(); h.AppendData(b, 0, n); meter.Add(n); }
        meter.Add(0, force: true);
        return Convert.ToHexString(h.GetHashAndReset());
    }
    private static void DurableWrite(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
}
