using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

public sealed record SourceRecoveryReport(string Directory, int OrphanFiles, long MissingInode,
    long DirectoryFragment, string EvidenceSha256, bool GeneratedContainerAttributes = true);
internal sealed record RecoveryEvidence(long Inode, long Fragment, byte[] Directory, byte[] MissingRecord,
    byte[] RootRecord, long[] Files);

internal sealed partial class UfsVolume
{
    private bool _qualifyingRecovery;
    private long _missingRecoveryInode = -1, _recoveryFragment = -1;
    private readonly List<long> _recoveryFiles = new();
    private RecoveryEvidence? _recoveryEvidence;
    private long _preservedRecoveryInode = -1;
    internal DiskRange? PreservedRecoveryData { get; private set; }
    internal int PreservedOrphanFiles => _preservedRecoveryInode < 0 ? 0 : _recoveryFiles.Count;
    // Called only for a fully qualified original target, with a preservation
    // archive required by its caller. This does not mutate any disk or bitmap.
    internal void TrackPreservedRecovery(RecoveryEvidence evidence)
    {
        if (!ReferenceEquals(evidence, _recoveryEvidence)) throw new InvalidOperationException("Recovery certificate mismatch.");
        long f = evidence.Fragment;
        if (WasFree(f) || Bit(_owned[f/Fpg], 0, (int)(f%Fpg))) throw new InvalidDataException("Recovery ownership changed.");
        Set(_owned[f/Fpg], 0, (int)(f%Fpg), true);
        _preservedRecoveryInode=evidence.Inode;
        PreservedRecoveryData=new(checked(Offset+f*Fragment),Fragment);
    }
    private void RetirePreservedRecoveryInode()
    {
        if (_preservedRecoveryInode < 0) return;
        long ino=_preservedRecoveryInode;
        Group g=Groups[ino/Ipg]; Set(g.Raw,g.InodeMap,(int)(ino%Ipg),false);
        RegularImage.Write(Disk,InodeOffset(ino),new byte[256]);
        _preservedRecoveryInode=-1;
    }

    internal RecoveryEvidence QualifyDetachedRecovery(CancellationToken token, Action<MigrationProgress>? telemetry)
    {
        if (Nodes.Count != 0) throw new InvalidOperationException("Recovery qualification requires a fresh inventory.");
        _qualifyingRecovery = true;
        try
        {
            Inventory(false, token, telemetry, "qualify-source-recovery");
            return _recoveryEvidence ?? throw new InvalidDataException("No qualifying detached recovery directory.");
        }
        finally { _qualifyingRecovery = false; }
    }
    private void LoadRecoveryOrphans(CancellationToken token)
    {
        if (Fragment != 4096) throw new NotSupportedException("Detached recovery currently requires 4KiB fragments.");
        if (Nodes[2].Entries.Any(e => e.Name.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(MigrationSource.Container))))
            throw new InvalidDataException("Recovery container name already exists; refusing a collision.");
        foreach (Group g in Groups)
            for (int i = 0; i < Ipg; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                long ino = (long)g.Number * Ipg + i;
                if (ino < 2 || !Bit(g.Raw, g.InodeMap, i) || Nodes.ContainsKey(ino)) continue;
                Node n = Get(ino); // Includes initialized-table, bounds and node-budget checks.
                if (!n.Raw.AsSpan().ContainsAnyExcept((byte)0))
                {
                    Nodes.Remove(ino);
                    if (_missingRecoveryInode != -1) throw new InvalidDataException("Multiple missing orphan inode records.");
                    _missingRecoveryInode = ino;
                }
                else
                {
                    if (n.Type != 8 || n.Links != 1 || _recoveryFiles.Count >= 128)
                        throw new InvalidDataException("Recovery requires at most 128 regular, singly linked orphan files.");
                    _recoveryFiles.Add(ino); _references.Add(ino, 1);
                }
            }
        if (_missingRecoveryInode < 2 || _recoveryFiles.Count == 0)
            throw new InvalidDataException("Expected one missing inode and regular orphan files.");
    }
    private void RecordRecoveryFragment(long fragment)
    {
        if (_recoveryFragment != -1) throw new InvalidDataException("Additional unaccounted allocation; recovery is ambiguous.");
        _recoveryFragment = fragment;
    }
    private void ValidateRecoveryDirectory()
    {
        if (_recoveryFragment < 1) throw new InvalidDataException("No detached directory fragment.");
        byte[] raw = Read(_recoveryFragment, Fragment);
        var entries = ParseDirectory(raw);
        if (entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != entries.Count ||
            entries.Count != _recoveryFiles.Count + 2 ||
            entries.SingleOrDefault(e => e.Text == ".") is not Entry dot || dot.Inode != _missingRecoveryInode || dot.Type != 4 ||
            entries.SingleOrDefault(e => e.Text == "..") is not Entry parent || parent.Inode != 2 || parent.Type != 4)
            throw new InvalidDataException("Detached directory identity/parent/name mismatch.");
        var files = entries.Where(e => e.Text is not ("." or "..")).ToArray();
        if (!files.Select(e => (long)e.Inode).Order().SequenceEqual(_recoveryFiles.Order()) ||
            files.Any(e => e.Type != 8 || !e.Name.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("#" + e.Inode.ToString("D9", System.Globalization.CultureInfo.InvariantCulture)))))
            throw new InvalidDataException("Detached directory must name every regular orphan exactly once.");
        _recoveryEvidence = new(_missingRecoveryInode, _recoveryFragment, raw, ReadNode(_missingRecoveryInode).Raw,
            (byte[])Nodes[2].Raw.Clone(), _recoveryFiles.Order().ToArray());
        // The following normal summary verification must explain exactly one
        // directory in this inode's CG, with every other counter/map unchanged.
    }

    internal void ViewFree(long fragment, int count)
    {
        for (int j = 0; j < count; j++)
        {
            long f = fragment + j;
            if (Reserved(f) || f <= 0 || f >= Size) throw new InvalidDataException("Invalid view release.");
            Group g = Groups[f / Fpg]; Set(g.Raw, g.FreeMap, (int)(f % Fpg), true);
        }
    }
    internal static byte[] EncodeDirectory(IEnumerable<Entry> entries)
    {
        var sectors = new List<byte[]>(); byte[] sector = new byte[512]; int at = 0, last = -1;
        foreach (Entry e in entries)
        {
            if (e.Name.Length is < 1 or > 255 || e.Name.Contains((byte)0) || e.Name.Contains((byte)'/'))
                throw new InvalidDataException("Invalid generated directory name.");
            int length = (8 + e.Name.Length + 1 + 3) / 4 * 4;
            if (at + length > 512)
            {
                BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(last + 4), (ushort)(512-last));
                sectors.Add(sector); sector = new byte[512]; at = 0;
            }
            BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(at), e.Inode);
            BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(at + 4), (ushort)length);
            sector[at + 6] = e.Type; sector[at + 7] = (byte)e.Name.Length; e.Name.CopyTo(sector, at + 8);
            last = at; at += length;
        }
        if (last < 0) throw new InvalidDataException("Empty generated directory.");
        BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(last + 4), (ushort)(512-last)); sectors.Add(sector);
        return sectors.SelectMany(s => s).ToArray();
    }
    internal void WriteViewPayload(long ino, byte[] raw, byte[] bytes)
    {
        // Only generated evidence files/directories and a direct-only root use this.
        if (bytes.Length > 12 * Block) throw new NotSupportedException("Recovery view metadata exceeds direct-pointer budget.");
        Array.Clear(raw, 0x70, 120); P64(raw, 0x10, bytes.Length);
        long sectors = (long)((I32(raw, 0x5C) + Fragment - 1) / Fragment) * Fragment / 512;
        for (int at = 0; at < bytes.Length; at += Block)
        {
            int length = Math.Min(Block, bytes.Length-at), padded = (length+Fragment-1)/Fragment*Fragment;
            long p = Allocate(padded/Fragment); byte[] data = new byte[padded]; bytes.AsSpan(at, length).CopyTo(data);
            Write(p, data); P64(raw, 0x70 + at/Block*8, p); sectors += padded/512;
        }
        P64(raw, 0x18, sectors); RegularImage.Write(Disk, InodeOffset(ino), raw);
    }
}

// Sparse, bounded copy-on-write metadata overlay. There is deliberately no path
// to source.Write*, Flush or Dispose. It owns only its memory, never the base handle.
internal sealed class RecoveryOverlayDisk(IDiskSource source, long first, long end) : IDiskSource, IDurableDisk
{
    private readonly Dictionary<long, byte[]> _sectors = new();
    internal const int MaxBytes = 128 * 1024 * 1024;
    internal void Freeze() => CanWrite = false;
    public bool CanWrite { get; private set; } = true;
    public long TotalSize => source.TotalSize;
    public int SectorSize => 512;
    public long SectorCount => TotalSize/512;
    public string Description => "Ephemeral validated recovery view";
    public byte[] ReadSectors(long sector, int count) => ReadBytes(checked(sector*512), checked(count*512));
    public byte[] ReadBytes(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > TotalSize-count) throw new ArgumentOutOfRangeException(nameof(offset));
        byte[] result = new byte[count];
        for (int at = 0; at < count;)
        {
            long p = (offset+at)/512*512; int skip = (int)((offset+at)%512), take = Math.Min(512-skip, count-at);
            if (_sectors.TryGetValue(p, out var saved))
            { saved.AsSpan(skip, take).CopyTo(result.AsSpan(at)); at += take; }
            else
            {
                // Coalesce unchanged payload reads; a recovery view must not turn
                // every normal 16KiB data read into 32 physical I/O requests.
                int run = take;
                while (at+run < count && !_sectors.ContainsKey((offset+at+run)/512*512))
                    run += Math.Min(512, count-at-run);
                source.ReadBytes(offset+at, run).CopyTo(result, at); at += run;
            }
        }
        return result;
    }
    public void WriteSectors(long sector, byte[] data) => WriteBytes(checked(sector*512), data);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, data.AsSpan());
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (!CanWrite) throw new InvalidOperationException("Recovery view is frozen.");
        if (offset < first || offset%512 != 0 || data.Length%512 != 0 || offset > end-data.Length)
            throw new InvalidDataException("Recovery overlay write outside its volume/alignment.");
        int added = 0;
        for (int at = 0; at < data.Length; at += 512) if (!_sectors.ContainsKey(offset+at)) added++;
        if (_sectors.Count + added > MaxBytes/512) throw new IOException("Recovery overlay exceeds 128MiB metadata budget.");
        for (int at = 0; at < data.Length; at += 512) _sectors[offset+at] = data.Slice(at, 512).ToArray();
    }
    public void Flush() { }
    public void Dispose() { foreach (var b in _sectors.Values) CryptographicOperations.ZeroMemory(b); _sectors.Clear(); }
}

internal sealed class MigrationSource : IDisposable
{
    internal const string Container = ".ps3hddtool-recovery";
    internal UfsVolume Volume { get; }
    private readonly RecoveryOverlayDisk? _overlay;
    private MigrationSource(UfsVolume volume, RecoveryOverlayDisk? overlay = null) { Volume = volume; _overlay = overlay; }
    internal static void ValidateMode(string? mode)
    { if (mode is not (null or "none" or "detached-directory")) throw new ArgumentException("SourceRecovery must be none or detached-directory."); }
    internal static MigrationSource Open(IDiskSource disk, long sector, string? mode, CancellationToken token,
        Action<MigrationProgress>? telemetry)
    {
        ValidateMode(mode); var original = new UfsVolume(disk, sector);
        if (mode is null or "none") return new(original);
        RecoveryEvidence evidence = original.QualifyDetachedRecovery(token, telemetry);
        if (original.Nodes[2].Size > 12L*original.Block) throw new NotSupportedException("Recovery requires a direct-only root directory.");
        byte[] originalRootDirectory = Enumerable.Range(0, checked((int)((original.Nodes[2].Size+original.Block-1)/original.Block)))
            .SelectMany(i => original.LogicalBytes(original.Nodes[2], i)).ToArray();
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new {
            formatVersion=1, sourceModified=false, originalDirectoryAttributesKnown=false,
            generatedContainer=new { name=Container, mode="0700", uid=0, gid=0, timestamps=0 },
            missingInode=evidence.Inode, directoryFragment=evidence.Fragment,
            missingInodeRecordHex=Convert.ToHexString(evidence.MissingRecord), originalRootRecordHex=Convert.ToHexString(evidence.RootRecord),
            directorySha256=Convert.ToHexString(SHA256.HashData(evidence.Directory)),
            files=evidence.Files.Select(ino => new { sourceInode=ino, name="#"+ino.ToString("D9", System.Globalization.CultureInfo.InvariantCulture),
                originalInodeRecordHex=Convert.ToHexString(original.Nodes[ino].Raw) }).ToArray()
        }, ImageMigrator.Json);
        var report = new SourceRecoveryReport(Container, evidence.Files.Length, evidence.Inode, evidence.Fragment,
            Convert.ToHexString(SHA256.HashData(manifest)));
        MigrationTelemetry.Send(telemetry, new("source-recovery-qualified", $"Validated detached directory and {evidence.Files.Length} orphan files; source remains read-only"));
        var overlay = new RecoveryOverlayDisk(disk, original.Offset, checked(original.Offset+original.Size*original.Fragment));
        try
        {
            var view = new UfsVolume(overlay, sector);
            Node root = original.Nodes[2];
            if (root.Size > 12L*original.Block) throw new NotSupportedException("Recovery requires a direct-only root directory.");
            foreach (var b in original.NonzeroBlocks(root)) view.ViewFree(b.Fragment, original.DataLength(root, b.Logical)/original.Fragment);
            view.ViewFree(evidence.Fragment, 1);
            Entry Named(long ino, byte type, string name) => new(checked((uint)ino), type, Encoding.ASCII.GetBytes(name));
            byte[] Generated(int type, int links)
            {
                byte[] b = new byte[256]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)((type<<12)|(type==4 ? 0x1C0 : 0x180)));
                BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), checked((short)links)); UfsVolume.P32(b, 12, view.Block); return b;
            }
            var entries = new List<Entry> { Named(evidence.Inode, 4, "."), Named(2, 4, "..") };
            entries.AddRange(evidence.Files.Select(ino => Named(ino, 8, "#"+ino.ToString("D9", System.Globalization.CultureInfo.InvariantCulture))));
            foreach (var file in new[] { ("original-directory.bin", evidence.Directory), ("original-inode.bin", evidence.MissingRecord),
                ("original-root-directory.bin", originalRootDirectory), ("recovery.json", manifest) })
            {
                token.ThrowIfCancellationRequested(); long ino = view.AllocateInode();
                view.WriteViewPayload(ino, Generated(8, 1), file.Item2); entries.Add(Named(ino, 8, file.Item1));
            }
            view.WriteViewPayload(evidence.Inode, Generated(4, 2), UfsVolume.EncodeDirectory(entries));
            byte[] rootRaw = (byte[])root.Raw.Clone();
            BinaryPrimitives.WriteInt16BigEndian(rootRaw.AsSpan(2), checked((short)(root.Links+1)));
            view.WriteViewPayload(2, rootRaw, UfsVolume.EncodeDirectory(root.Entries.Append(Named(evidence.Inode, 4, Container))));
            view.RecalculateAndFlush(token: token); overlay.Freeze();
            new UfsVolume(overlay, sector).Inventory(false, token, telemetry, "validate-source-recovery-view");
            MigrationTelemetry.Send(telemetry, new("source-recovery-view", "Recovery view passed strict inventory; original metadata evidence will be copied"));
            return new(new UfsVolume(overlay, sector) { RecoveryReport=report }, overlay);
        }
        catch { overlay.Dispose(); throw; }
    }
    public void Dispose() => _overlay?.Dispose();
}
