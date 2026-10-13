using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.Migration;

internal sealed record Entry(uint Inode, byte Type, byte[] Name)
{
    // UFS names are bytes. Key is authoritative; Text is an injective, terminal-safe
    // diagnostic label, never an encoding used to write a directory entry.
    public string Key => Convert.ToHexString(Name);
    public string Text => string.Concat(Name.Select(b => b is >= 0x20 and <= 0x7E && b != (byte)'%'
        ? ((char)b).ToString() : "%" + b.ToString("X2")));
}
internal sealed class Node(long number, byte[] raw)
{
    public long Number = number;
    public byte[] Raw = raw;
    public ushort Mode => BinaryPrimitives.ReadUInt16BigEndian(Raw);
    public int Type => Mode >> 12;
    public long Size => BinaryPrimitives.ReadInt64BigEndian(Raw.AsSpan(0x10));
    public long Sectors => BinaryPrimitives.ReadInt64BigEndian(Raw.AsSpan(0x18));
    public short Links => BinaryPrimitives.ReadInt16BigEndian(Raw.AsSpan(2));
    // di_blocks can include external attributes even when the link target is inline.
    public bool Inline => Type == 10 && Size < 120;
    public List<Entry> Entries = new();
    public string Hash = "";
    public string? Path;
}

internal sealed class Group(int number, long offset, byte[] raw)
{
    public int Number = number;
    public long Offset = offset;
    public byte[] Raw = raw;
    public int InodeMap => UfsVolume.I32(Raw, 0x5C);
    public int FreeMap => UfsVolume.I32(Raw, 0x60);
    public int Fragments => UfsVolume.I32(Raw, 0x14);
}

// Separate migration writer: no upstream deferred CG cache or asynchronous batches.
// Used inside an unpublished image copy or behind the direct migration journal guard.
internal sealed partial class UfsVolume
{
    public readonly IDiskSource Disk;
    public readonly long Offset, Size;
    public readonly int Block, Fragment, Fpb, Fanout, Ipg, Fpg, Ncg, Iblk, Dblk, Cblk;
    public readonly byte[] Super;
    public readonly Group[] Groups;
    public readonly Dictionary<long, Node> Nodes = new();
    internal SourceRecoveryReport? RecoveryReport;
    private readonly byte[][] _owned;
    private readonly Dictionary<long, int> _references = new();
    private int _allocGroup;
    private static readonly byte[] HashZeros = new byte[1024 * 1024];
    private readonly Dictionary<int, int> _cursors = new();
    public static int I32(byte[] b, int p) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
    public static long I64(byte[] b, int p) => BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(p));
    public static void P32(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(p), v);
    public static void P64(byte[] b, int p, long v) => BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), v);
    internal UfsVolume(IDiskSource disk, long partitionSector, bool allowDirty = false)
    {
        Disk = disk; Offset = checked(partitionSector * 512);
        Super = disk.ReadBytes(checked(Offset + 65536), 8192);
        if (I32(Super, 0x55C) != 0x19540119) throw new InvalidDataException("Expected big-endian PS3 UFS2.");
        Block = I32(Super, 0x30); Fragment = I32(Super, 0x34); Ipg = I32(Super, 0xB8);
        Fpg = I32(Super, 0xBC); Ncg = I32(Super, 0x2C); Iblk = I32(Super, 0x10);
        Dblk = I32(Super, 0x14); Cblk = I32(Super, 0x0C); Size = I64(Super, 0x438);
        bool Power(int x) => x > 0 && (x & (x - 1)) == 0;
        if (!Power(Block) || Block is < 4096 or > 65536 || !Power(Fragment) || Fragment < 512 ||
            Fragment > Block || Block / Fragment > 8 || Ipg <= 2 || Fpg <= 0 || Ncg <= 0 || Ncg > 100000 ||
            Iblk <= Cblk || Dblk < Iblk + ((long)Ipg * 256 + Fragment - 1) / Fragment ||
            Size <= 0 || Size > (disk.TotalSize - Offset) / Fragment || I32(Super, 0x528) != 120 ||
            (Size + Fpg - 1) / Fpg != Ncg || (Super[0xD1] != 1 && !(allowDirty && Super[0xD1] == 0)))
            throw new InvalidDataException("Unsupported, dirty, or out-of-bounds UFS2 geometry.");
        Fpb = Block / Fragment; Fanout = Block / 8;
        int sblk = I32(Super, 8);
        long cs = I64(Super, 0x448); int cssize = I32(Super, 0x9C);
        if (sblk < (65536 + 8192 + Fragment - 1) / Fragment || sblk % Fpb != 0 ||
            Cblk < sblk + (8192 + Fragment - 1) / Fragment || Iblk < Cblk + Fpb ||
            Fpg % Fpb != 0 || Ipg % (Block / 256) != 0 ||
            cs < Dblk || cssize < Ncg * 16L || cssize % Fragment != 0 || cs > Size - cssize / Fragment ||
            (I32(Super, 0x520) & ~0xB2) != 0 || I64(Super, 0x450) != 0 || I32(Super, 0x458) != 0 ||
            Super.AsSpan(0x45C, 80).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Unsupported UFS2 reserved layout, flags, pending frees, or snapshots.");
        int cgSize = I32(Super, 0xA0);
        if (cgSize < 168 || cgSize > Block) throw new InvalidDataException("Invalid cylinder-group size.");
        Groups = new Group[Ncg];
        _owned = new byte[Ncg][];
        for (int i = 0; i < Ncg; i++)
        {
            long at = checked(Offset + ((long)i * Fpg + Cblk) * Fragment);
            byte[] raw = disk.ReadBytes(at, cgSize); var g = new Group(i, at, raw);
            if (I32(raw, 4) != 0x90255 || I32(raw, 12) != i ||
                g.Fragments != Math.Min(Fpg, Size - (long)i * Fpg) ||
                g.InodeMap < 168 || g.FreeMap < g.InodeMap + (Ipg + 7) / 8 ||
                g.FreeMap > raw.Length - (g.Fragments + 7) / 8 || I32(raw, 0x78) < 0 || I32(raw, 0x78) > Ipg)
                throw new InvalidDataException($"Invalid cylinder group {i}.");
            Groups[i] = g;
            if (g.Fragments <= Dblk) throw new InvalidDataException("Cylinder group is too short.");
            _owned[i] = new byte[(g.Fragments + 7) / 8];
        }
    }
    private bool Reserved(long f)
    {
        long cg = f / Fpg, local = f % Fpg;
        return (local < Dblk && (cg == 0 || local >= I32(Super, 8))) ||
            (f >= I64(Super, 0x448) && f < I64(Super, 0x448) + I32(Super, 0x9C) / Fragment);
    }
    private static bool Bit(byte[] b, int start, int n) => (b[start + n / 8] & (1 << (n % 8))) != 0;
    private static void Set(byte[] b, int start, int n, bool value)
    {
        if (value) b[start + n / 8] |= (byte)(1 << (n % 8));
        else b[start + n / 8] &= (byte)~(1 << (n % 8));
    }
    public long InodeOffset(long ino)
    {
        if (ino < 0 || ino >= (long)Ncg * Ipg) throw new InvalidDataException("Inode out of range.");
        return checked(Offset + ((ino / Ipg) * Fpg + Iblk) * Fragment + (ino % Ipg) * 256);
    }
    public Node ReadNode(long ino) => new(ino, Disk.ReadBytes(InodeOffset(ino), 256));
    public byte[] Read(long fragment, int length)
    {
        if (fragment <= 0 || length < 0 || fragment > Size - (length + Fragment - 1L) / Fragment)
            throw new InvalidDataException("Fragment outside partition.");
        return Disk.ReadBytes(checked(Offset + fragment * Fragment), length);
    }
    public void Write(long fragment, byte[] data)
    {
        if (fragment <= 0 || data.Length % Fragment != 0 || fragment > Size - data.Length / Fragment)
            throw new InvalidDataException("Unaligned/out-of-partition write.");
        Disk.WriteBytes(checked(Offset + fragment * Fragment), data);
    }
    private long Capacity(int level)
    {
        long c = 1; for (int i = 0; i < level; i++) c = checked(c * Fanout); return c;
    }
    public long Pointer(Node n, long logical)
    {
        if (logical < 12) return I64(n.Raw, 0x70 + checked((int)logical) * 8);
        long relative = logical - 12;
        for (int level = 1; level <= 3; level++)
        {
            long cap = Capacity(level);
            if (relative < cap)
            {
                long p = I64(n.Raw, 0xC8 + level * 8);
                for (int l = level; l > 0 && p != 0; l--)
                {
                    long child = Capacity(l - 1); long index = relative / child; relative %= child;
                    p = I64(Read(p, Block), checked((int)index * 8));
                }
                return p;
            }
            relative -= cap;
        }
        throw new InvalidDataException("File exceeds triple-indirect capacity.");
    }
    private IEnumerable<(long Logical, long Fragment)> NonzeroIndirect(long address, int level, long first, long count)
    {
        if (address == 0) yield break;
        byte[] pointers = Read(address, Block); long child = Capacity(level - 1);
        for (int i = 0; count > 0; i++)
        {
            long take = Math.Min(count, child), p = I64(pointers, i * 8);
            if (p < 0) throw new InvalidDataException("Negative pointer.");
            if (level == 1) { if (p != 0) yield return (first, p); }
            else foreach (var b in NonzeroIndirect(p, level - 1, first, take)) yield return b;
            first += take; count -= take;
        }
    }
    public IEnumerable<(long Logical, long Fragment)> NonzeroBlocks(Node n)
    {
        if (n.Inline || n.Type is not (4 or 8 or 10)) yield break;
        long count = checked((n.Size + Block - 1) / Block), first = 0;
        for (; first < Math.Min(12, count); first++)
        {
            long p = I64(n.Raw, 0x70 + (int)first * 8);
            if (p < 0) throw new InvalidDataException("Negative pointer.");
            if (p != 0) yield return (first, p);
        }
        long remain = count - first;
        for (int level = 1; level <= 3 && remain > 0; level++)
        {
            long take = Math.Min(remain, Capacity(level));
            foreach (var b in NonzeroIndirect(I64(n.Raw, 0xC8 + level * 8), level, first, take)) yield return b;
            first += take; remain -= take;
        }
        if (remain > 0) throw new InvalidDataException("File exceeds address capacity.");
    }
    public int DataLength(Node n, long logical)
    {
        long left = n.Size - logical * Block;
        // PS3 direct-only file tails and directory tails are fragment allocated.
        return n.Size <= 12L * Block
            ? checked((int)Math.Min(Block, ((left + Fragment - 1) / Fragment) * Fragment)) : Block;
    }
    public IEnumerable<(long Fragment, int Count)> Extents(Node n)
    {
        if (!n.Inline && n.Type is 4 or 8 or 10)
        {
            foreach (var b in NonzeroBlocks(n)) yield return (b.Fragment, DataLength(n, b.Logical) / Fragment);
            long count = (n.Size + Block - 1) / Block, first = 12;
            for (int level = 1; level <= 3 && first < count; level++)
            {
                long take = Math.Min(count - first, Capacity(level));
                foreach (long p in IndirectExtents(I64(n.Raw, 0xC8 + level * 8), level, take)) yield return (p, Fpb);
                first += take;
            }
        }
        int ea = I32(n.Raw, 0x5C);
        if (ea < 0 || ea > 2 * Block) throw new InvalidDataException("Unsupported extended attribute size.");
        for (int i = 0; i * Block < ea; i++)
            yield return (I64(n.Raw, 0x60 + i * 8), (Math.Min(Block, ea - i * Block) + Fragment - 1) / Fragment);
    }
    private IEnumerable<long> IndirectExtents(long p, int level, long count)
    {
        if (p == 0) yield break;
        yield return p;
        if (level == 1) yield break;
        byte[] b = Read(p, Block); long cap = Capacity(level - 1);
        for (int i = 0; count > 0; i++)
        {
            long take = Math.Min(count, cap);
            foreach (long child in IndirectExtents(I64(b, i * 8), level - 1, take)) yield return child;
            count -= take;
        }
    }
    public byte[] LogicalBytes(Node n, long logical)
    {
        int length = checked((int)Math.Min(Block, n.Size - logical * Block));
        if (n.Inline) return n.Raw.AsSpan(0x70, checked((int)n.Size)).ToArray();
        long p = Pointer(n, logical); return p == 0 ? new byte[length] : Read(p, length);
    }
    public string Hash(Node n, CancellationToken token = default, ProgressMeter? meter = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (n.Inline) { hash.AppendData(n.Raw.AsSpan(0x70, checked((int)n.Size))); meter?.Add(n.Size, n.Number, n.Path); }
        else if (n.Type is 8 or 10)
        {
            // Walk each indirect page once. Merge only physically AND logically
            // contiguous payload blocks; holes still contribute their exact zeros.
            const int maximumRead = 1024 * 1024;
            byte[] zeros = HashZeros;
            long position = 0;
            void Hole(long end)
            {
                if (end < position) throw new InvalidDataException("Hash blocks are not in logical order.");
                while (position < end)
                {
                    token.ThrowIfCancellationRequested();
                    int length = (int)Math.Min(zeros.Length, end - position);
                    hash.AppendData(zeros.AsSpan(0, length)); position += length;
                    meter?.Add(length, n.Number, n.Path);
                }
            }
            using var blocks = NonzeroBlocks(n).GetEnumerator();
            bool more = blocks.MoveNext();
            while (more)
            {
                token.ThrowIfCancellationRequested();
                var first = blocks.Current; long logical = first.Logical, fragment = first.Fragment;
                Hole(checked(logical * Block));
                int length = checked((int)Math.Min(Block, n.Size - position));
                more = blocks.MoveNext();
                while (more && length % Block == 0 && length <= maximumRead - Block &&
                    blocks.Current.Logical == logical + length / Block &&
                    blocks.Current.Fragment == fragment + length / Fragment)
                {
                    length += checked((int)Math.Min(Block, n.Size - position - length));
                    more = blocks.MoveNext();
                }
                byte[] bytes = Read(fragment, length); hash.AppendData(bytes); position += length;
                meter?.Add(length, n.Number, n.Path);
            }
            Hole(n.Size);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static List<Entry> ParseDirectory(byte[] b)
    {
        var entries = new List<Entry>();
        for (int p = 0; p < b.Length;)
        {
            if (p > b.Length - 8) throw new InvalidDataException("Truncated directory entry.");
            uint ino = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 4)), nameLength = b[p + 7];
            if (len < 8 || len % 4 != 0 || len > 512 - p % 512 || len > b.Length - p || nameLength > len - 8)
                throw new InvalidDataException("Invalid directory record.");
            if (ino != 0)
            {
                byte[] name = b.AsSpan(p + 8, nameLength).ToArray();
                if (nameLength == 0 || name.Contains((byte)0) || name.Contains((byte)'/'))
                    throw new InvalidDataException("Invalid filesystem name.");
                entries.Add(new(ino, b[p + 6], name));
            }
            p += len;
        }
        return entries;
    }
    public List<Entry> Directory(Node n)
    {
        var entries = new List<Entry>();
        if (n.Size <= 0 || n.Size % 512 != 0 || n.Size > 64L * 1024 * 1024)
            throw new InvalidDataException("Invalid or unsupported directory size.");
        for (long logical = 0; logical * Block < n.Size; logical++)
        {
            byte[] b = LogicalBytes(n, logical);
            entries.AddRange(ParseDirectory(b));
        }
        if (entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidDataException("Duplicate directory name.");
        return entries;
    }
    public void Inventory(bool hashFiles = true, CancellationToken token = default,
        Action<MigrationProgress>? telemetry = null, string phase = "inventory")
    {
        MigrationTelemetry.Send(telemetry, new(phase, "Walk filesystem tree"));
        var walk = new ProgressMeter(telemetry, phase, "Walk filesystem tree", unit: "directories");
        var pending = new Queue<(long Ino, long Parent)>(); pending.Enqueue((2, 2));
        var directories = new HashSet<long>();
        while (pending.TryDequeue(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!directories.Add(item.Ino)) throw new InvalidDataException("Directory cycle or directory hard link.");
            Node dir = Get(item.Ino); if (dir.Number == 2) dir.Path = "";
            if (dir.Type != 4) throw new InvalidDataException("Directory type mismatch.");
            dir.Entries = Directory(dir);
            if (dir.Entries.SingleOrDefault(e => e.Text == ".")?.Inode != item.Ino ||
                dir.Entries.SingleOrDefault(e => e.Text == "..")?.Inode != item.Parent)
                throw new InvalidDataException("Invalid dot/parent directory references.");
            foreach (var entry in dir.Entries)
            {
                _references[entry.Inode] = _references.GetValueOrDefault(entry.Inode) + 1;
                if (entry.Text is "." or "..") continue;
                Node child = Get(entry.Inode);
                if (telemetry != null && child.Path == null)
                {
                    string label = string.IsNullOrEmpty(dir.Path) ? entry.Text : dir.Path + "/" + entry.Text;
                    // Diagnostic labels must not cause quadratic memory growth on deep trees.
                    child.Path = label.Length <= 4096 ? label : label[..4093] + "...";
                }
                int expectedType = child.Type switch { 4 => 4, 8 => 8, 10 => 10, 1 => 1, 2 => 2, 6 => 6, 12 => 12, _ => -1 };
                if (entry.Type != 0 && entry.Type != expectedType) throw new InvalidDataException("Directory/inode type mismatch.");
                if (child.Type == 4) pending.Enqueue((entry.Inode, item.Ino));
            }
            walk.Add(1, dir.Number, dir.Path);
        }
        if (_qualifyingRecovery) LoadRecoveryOrphans(token);
        long? hashTotal = null;
        if (telemetry != null) try { hashTotal = Nodes.Values.Where(n => n.Type is 8 or 10).Sum(n => n.Size); } catch (OverflowException) { }
        var hashMeter = hashFiles && telemetry != null ? new ProgressMeter(telemetry, phase + "-hash", "Hash logical file contents", hashTotal) : null;
        foreach (Node n in Nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (n.Links <= 0 || n.Links != _references.GetValueOrDefault(n.Number) || n.Size < 0 ||
                n.Type is not (1 or 2 or 4 or 6 or 8 or 10 or 12) || (I32(n.Raw, 0x58) & 0x200000) != 0)
                throw new InvalidDataException("Invalid inode links/type, size, or snapshot inode.");
            long sectors = 0;
            foreach (var extent in Extents(n))
            {
                if (extent.Fragment <= 0 || extent.Count <= 0 || extent.Count > Fpb || extent.Fragment > Size - extent.Count ||
                    extent.Fragment / Fpb != (extent.Fragment + extent.Count - 1) / Fpb ||
                    (extent.Count == Fpb && extent.Fragment % Fpb != 0)) throw new InvalidDataException("Invalid extent.");
                for (int i = 0; i < extent.Count; i++)
                {
                    long f = extent.Fragment + i; int cg = checked((int)(f / Fpg)), index = (int)(f % Fpg);
                    if (Reserved(f) || Bit(Groups[cg].Raw, Groups[cg].FreeMap, index) || Bit(_owned[cg], 0, index))
                        throw new InvalidDataException("Free, reserved, or multiply-owned fragment is referenced.");
                    Set(_owned[cg], 0, index, true);
                }
                sectors += (long)extent.Count * Fragment / 512;
            }
            if (n.Type is 4 or 8 or 10 && sectors != n.Sectors) throw new InvalidDataException("Inode block accounting mismatch.");
            if (hashFiles && n.Type is 8 or 10) n.Hash = Hash(n, token, hashMeter);
        }
        hashMeter?.Add(0, force: true);
        var maps = new ProgressMeter(telemetry, phase + "-maps", "Audit allocation ownership", Size, "fragments");
        // Reject allocated orphans instead of silently carrying forward/leaking them.
        foreach (Group g in Groups)
            for (int i = 0; i < Ipg; i++)
                if (Bit(g.Raw, g.InodeMap, i) && (long)g.Number * Ipg + i > 1 &&
                    !Nodes.ContainsKey((long)g.Number * Ipg + i) &&
                    !(_qualifyingRecovery && (long)g.Number * Ipg + i == _missingRecoveryInode))
                    throw new InvalidDataException("Allocated inode is unreachable from root.");
        foreach (Group g in Groups)
        {
            for (int i = 0; i < g.Fragments; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                bool reserved = Reserved((long)g.Number * Fpg + i), free = Bit(g.Raw, g.FreeMap, i);
                if (reserved && free) throw new InvalidDataException("Free reserved fragment.");
                if (!reserved && !free && !Bit(_owned[g.Number], 0, i) && _qualifyingRecovery)
                    RecordRecoveryFragment((long)g.Number * Fpg + i);
                else if (!reserved && !free && !Bit(_owned[g.Number], 0, i))
                    throw new InvalidDataException("Free reserved fragment or unreachable allocation.");
            }
            maps.Add(g.Fragments, force: g.Number == Ncg - 1);
        }
        if (_qualifyingRecovery) ValidateRecoveryDirectory();
        RecalculateAndFlush(verifyOnly: true, token: token);
    }
    private Node Get(long ino)
    {
        if (Nodes.TryGetValue(ino, out var n)) return n;
        if (Nodes.Count >= 2_000_000) throw new InvalidDataException("Inventory exceeds the supported node budget.");
        if (ino < 2 || ino >= (long)Ipg * Ncg || !Bit(Groups[ino / Ipg].Raw, Groups[ino / Ipg].InodeMap, (int)(ino % Ipg)))
            throw new InvalidDataException("Unallocated/out-of-range inode reference.");
        if (ino % Ipg >= I32(Groups[ino / Ipg].Raw, 0x78)) throw new InvalidDataException("Allocated inode is outside the initialized inode table.");
        n = ReadNode(ino); Nodes.Add(ino, n); return n;
    }
    public void ReleaseOldTree(bool releaseData = true)
    {
        if (releaseData) ReleaseRetiredData();
        RetirePreservedRecoveryInode();
        foreach (long ino in Nodes.Keys)
        {
            Group g = Groups[ino / Ipg]; Set(g.Raw, g.InodeMap, (int)(ino % Ipg), false);
            RegularImage.Write(Disk, InodeOffset(ino), new byte[256]);
        }
        Nodes.Clear(); _references.Clear();
    }
    public void ReleaseRetiredData()
    {
        foreach (Group g in Groups)
        {
            for (int i = 0; i < g.Fragments; i++)
                if (Bit(_owned[g.Number], 0, i)) Set(g.Raw, g.FreeMap, i, true);
            Array.Clear(_owned[g.Number]);
        }
    }
    internal bool WasFree(long fragment)
    {
        if (fragment <= 0 || fragment >= Size || Reserved(fragment)) return false;
        Group g = Groups[fragment / Fpg]; return Bit(g.Raw, g.FreeMap, (int)(fragment % Fpg));
    }
    public long AllocateInode(bool root = false)
    {
        for (int pass = 0; pass < Ncg; pass++)
        {
            int cg = (_allocGroup + pass) % Ncg; Group g = Groups[cg];
            for (int i = 0; i < Ipg; i++)
            {
                long ino = (long)cg * Ipg + i;
                if (ino < 2 || (root && ino != 2) || (!root && ino == 2) || Bit(g.Raw, g.InodeMap, i)) continue;
                int initialized = I32(g.Raw, 0x78);
                if (i >= initialized)
                {
                    int end = Math.Min(Ipg, ((i + Block / 256) / (Block / 256)) * (Block / 256));
                    RegularImage.Write(Disk, InodeOffset((long)cg * Ipg + initialized), new byte[(end - initialized) * 256]);
                    P32(g.Raw, 0x78, end);
                }
                Set(g.Raw, g.InodeMap, i, true); _allocGroup = cg; return ino;
            }
        }
        throw new IOException("No free inodes across any cylinder group.");
    }
    public long Allocate(int count)
    {
        for (int pass = 0; pass < Ncg; pass++)
        {
            int cg = (_allocGroup + pass) % Ncg; Group g = Groups[cg];
            int minimum = cg == 0 ? Dblk : 0;
            int start = _cursors.GetValueOrDefault(cg, minimum);
            for (int round = 0; round < 2; round++)
            {
                int end = round == 0 ? g.Fragments : start;
                for (int f = round == 0 ? start : minimum; f <= end - count; f++)
                {
                    if (f / Fpb != (f + count - 1) / Fpb || (count == Fpb && f % Fpb != 0)) continue;
                    bool free = f + (long)cg * Fpg != 0;
                    for (int j = 0; j < count; j++) free &= !Reserved((long)cg * Fpg + f + j) && Bit(g.Raw, g.FreeMap, f + j);
                    if (!free) continue;
                    for (int j = 0; j < count; j++) Set(g.Raw, g.FreeMap, f + j, false);
                    _cursors[cg] = f + count; _allocGroup = cg; return (long)cg * Fpg + f;
                }
            }
        }
        throw new IOException("No free extent across any cylinder group.");
    }
    public long PutPointer(byte[] raw, long logical, long data, TransferPointerCache? cache = null)
    {
        if (logical < 12) { P64(raw, 0x70 + (int)logical * 8, data); return 0; }
        long relative = logical - 12, added = 0;
        for (int level = 1; level <= 3; level++)
        {
            long cap = Capacity(level); if (relative >= cap) { relative -= cap; continue; }
            int rootOffset = 0xC8 + level * 8; long p = I64(raw, rootOffset);
            if (p == 0) { p = Allocate(Fpb); if (cache == null) Write(p, new byte[Block]); else cache.Get(p, fresh: true); P64(raw, rootOffset, p); added++; }
            for (int l = level; l > 0; l--)
            {
                long childCap = Capacity(l - 1); int index = checked((int)(relative / childCap)); relative %= childCap;
                byte[] pointers = cache == null ? Read(p, Block) : cache.Get(p);
                if (l == 1) { P64(pointers, index * 8, data); if (cache == null) Write(p, pointers); break; }
                long child = I64(pointers, index * 8);
                if (child == 0)
                {
                    child = Allocate(Fpb); if (cache == null) Write(child, new byte[Block]); else cache.Get(child, fresh: true);
                    // Adding a page can evict its parent; reacquire before changing it.
                    if (cache != null) pointers = cache.Get(p);
                    P64(pointers, index * 8, child); if (cache == null) Write(p, pointers); added++;
                }
                p = child;
            }
            return added * Block / 512;
        }
        throw new InvalidDataException("File exceeds triple-indirect capacity.");
    }
    public void RecalculateAndFlush(bool verifyOnly = false, CancellationToken token = default)
    {
        long dirs = 0, blocks = 0, inodes = 0, fragments = 0, clusters = 0;
        long cs = I64(Super, 0x448); int cssize = I32(Super, 0x9C);
        if (cs <= 0 || cssize < Ncg * 16L || cssize % Fragment != 0) throw new InvalidDataException("Invalid summary table.");
        byte[] oldSummary = Read(cs, cssize), summary = (byte[])oldSummary.Clone();
        foreach (Group g in Groups)
        {
            token.ThrowIfCancellationRequested();
            byte[] r = (byte[])g.Raw.Clone();
            int ndir = 0, nb = 0, ni = 0, nf = 0;
            for (int i = 0; i < Ipg; i++)
            {
                if (!Bit(g.Raw, g.InodeMap, i)) ni++;
                else if (((long)g.Number * Ipg + i) >= 2 && ReadNode((long)g.Number * Ipg + i).Type == 4) ndir++;
            }
            // Only the independently validated detached-directory certificate can
            // explain this one directory counter. No summary/free-map relaxation.
            if (verifyOnly && _qualifyingRecovery && _recoveryEvidence != null && _missingRecoveryInode / Ipg == g.Number) ndir++;
            int co = I32(g.Raw, 0x6C), nc = I32(g.Raw, 0x70), so = I32(g.Raw, 0x68);
            int max = I32(Super, 0x524);
            if (co != 0 && (co < 168 || nc < 0 || nc > g.Fragments / Fpb || co > g.Raw.Length - (nc + 7) / 8 ||
                max <= 0 || max > 64 || so + 4 < g.FreeMap + (g.Fragments + 7) / 8 || so > co - (max + 1) * 4)) throw new InvalidDataException("Invalid cluster maps.");
            if ((max > 0) != (co > 0)) throw new InvalidDataException("Missing cluster map.");
            // Summary index zero may overlap the final four bytes of the fragment bitmap.
            if (co != 0) { Array.Clear(r, co, (nc + 7) / 8); Array.Clear(r, so + 4, max * 4); }
            Array.Clear(r, 0x34, 32); int run = 0;
            void EndRun() { if (run > 0 && co != 0) { int p = so + Math.Min(run, max) * 4; P32(r, p, I32(r, p) + 1); } run = 0; }
            for (int b = 0; b < g.Fragments; b += Fpb)
            {
                int free = 0, fragmentRun = 0;
                void EndFragment() { if (fragmentRun > 0 && fragmentRun < Fpb) { int p = 0x34 + fragmentRun * 4; P32(r, p, I32(r, p) + 1); } fragmentRun = 0; }
                for (int j = 0; j < Math.Min(Fpb, g.Fragments - b); j++)
                {
                    if (Bit(g.Raw, g.FreeMap, b + j)) { free++; fragmentRun++; } else EndFragment();
                }
                EndFragment();
                if (free == Fpb) { nb++; if (co != 0 && b / Fpb < nc) { Set(r, co, b / Fpb, true); run++; } }
                else { nf += free; EndRun(); }
            }
            EndRun();
            P32(r, 0x18, ndir); P32(r, 0x1C, nb); P32(r, 0x20, ni); P32(r, 0x24, nf);
            P32(summary, g.Number * 16, ndir); P32(summary, g.Number * 16 + 4, nb);
            P32(summary, g.Number * 16 + 8, ni); P32(summary, g.Number * 16 + 12, nf);
            if (verifyOnly && !r.AsSpan().SequenceEqual(g.Raw)) throw new InvalidDataException("Cylinder-group summaries do not match bitmaps.");
            if (!verifyOnly) { r.CopyTo(g.Raw, 0); Disk.WriteBytes(g.Offset, r); }
            dirs += ndir; blocks += nb; inodes += ni; fragments += nf;
        }
        byte[] updated = (byte[])Super.Clone();
        P64(updated, 0x3F0, dirs); P64(updated, 0x3F8, blocks); P64(updated, 0x400, inodes);
        P64(updated, 0x408, fragments); P64(updated, 0x410, clusters); // FreeBSD 5 leaves cs_numclusters unused (zero).
        if (verifyOnly)
        {
            if (!summary.AsSpan().SequenceEqual(oldSummary) || !updated.AsSpan().SequenceEqual(Super))
                throw new InvalidDataException("Filesystem summary table/totals do not match bitmaps.");
        }
        else { Write(cs, summary); updated.CopyTo(Super, 0); Disk.WriteBytes(Offset + 65536, Super); RegularImage.Flush(Disk); }
    }
}
