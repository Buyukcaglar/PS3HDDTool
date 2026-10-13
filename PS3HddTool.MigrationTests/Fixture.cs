using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

// Deliberately independent fixture author/oracle: never calls the migration allocator,
// parser, directory writer, or summary writer. All data/identities are generated here.
sealed class Fixture
{
    internal sealed class Item(string path, long ino, int type, long size)
    {
        public string Path = path; public long Ino = ino; public int Type = type; public long Size = size;
        public byte[] Raw = new byte[256], Ea = [];
        public SortedDictionary<long, byte[]> Data = new();
        public List<(string Name, Item Target)> Children = new();
    }
    public readonly int Block, Frag, Fpb, Ipg, Fpg, Ncg, Sblk, Cblk, Iblk, Dblk;
    public const int Prefix = 16384;
    public readonly byte[] Image, Super = new byte[8192];
    public readonly List<Item> Items = new();
    internal readonly Dictionary<string, byte[]> RawNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Item> _paths = new(StringComparer.Ordinal);
    private readonly List<byte[]> _groups = new();
    public Item Root => Items[0];
    public Fixture(int groups = 4, int block = 16384, int frag = 4096, int ipg = 64, bool metadataOnly = false)
    {
        Block = block; Frag = frag; Fpb = block / frag; Ipg = ipg; Ncg = groups;
        Sblk = (65536 + 8192 + block - 1) / block * Fpb;
        Cblk = Sblk + (8192 + block - 1) / block * Fpb; Iblk = Cblk + Fpb;
        Dblk = Iblk + ipg * 256 / frag;
        Fpg = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(256, Dblk + 128));
        Image = metadataOnly ? [] : new byte[Prefix + groups * Fpg * frag + 4096];
        if (!metadataOnly) { Image.AsSpan(0, Prefix).Fill(0xA5); Image.AsSpan(Image.Length - 4096).Fill(0x5A); }
        W32(Super, 8, Sblk); W32(Super, 12, Cblk); W32(Super, 16, Iblk); W32(Super, 20, Dblk);
        W32(Super, 0x2C, groups); W32(Super, 0x30, block); W32(Super, 0x34, frag); W32(Super, 0x38, Fpb);
        W32(Super, 0x9C, frag); W32(Super, 0xA0, block); W32(Super, 0xB8, ipg); W32(Super, 0xBC, Fpg);
        Super[0xD1] = 1; W32(Super, 0x524, 16); W32(Super, 0x528, 120); W32(Super, 0x55C, 0x19540119);
        W64(Super, 0x438, (long)groups * Fpg); W64(Super, 0x448, Dblk); W64(Super, 0x3E8, 65536);
        for (int c = 0; !metadataOnly && c < groups; c++)
        {
            byte[] g = new byte[block]; _groups.Add(g);
            W32(g, 4, 0x90255); W32(g, 12, c); W32(g, 20, Fpg); W32(g, 0x74, ipg); W32(g, 0x78, ipg);
            int imap = 168, fmap = imap + (ipg + 7) / 8, sum = (fmap + (Fpg + 7) / 8 - 4 + 3) / 4 * 4;
            W32(g, 0x5C, imap); W32(g, 0x60, fmap); W32(g, 0x68, sum); W32(g, 0x6C, sum + 68); W32(g, 0x70, Fpg / Fpb);
            for (int f = 0; f < Fpg; f++)
                Set(g, fmap, f, !(f < Dblk && (c == 0 || f >= Sblk)) && !(c == 0 && f == Dblk));
            if (c == 0) { Set(g, imap, 0, true); Set(g, imap, 1, true); }
        }
        Add("", 4, 0);
    }
    public Item Add(string path, int type = 8, long size = 0, byte[]? payload = null, bool inline = false)
    {
        long ino = Items.Count + 2; if (ino >= Ncg * Ipg) throw new Exception("Fixture inode capacity.");
        var n = new Item(path, ino, type, size); _paths.Add(path, n); Items.Add(n);
        if (path != "")
        {
            int slash = path.LastIndexOf('/'); string parent = slash < 0 ? "" : path[..slash], name = path[(slash + 1)..];
            _paths[parent].Children.Add((name, n));
        }
        BinaryPrimitives.WriteUInt16BigEndian(n.Raw, (ushort)((type << 12) | 0x180)); // 0600
        W32(n.Raw, 4, 1001); W32(n.Raw, 8, 1002); W32(n.Raw, 12, Block);
        for (int t = 0; t < 4; t++) { W64(n.Raw, 0x20 + t * 8, 1234567890 + t); W32(n.Raw, 0x40 + t * 4, 123456 + t); }
        W32(n.Raw, 0x50, 0x12345678); W32(n.Raw, 0x58, 2); W64(n.Raw, 0xE8, 0xAABBCCDD);
        if (payload != null)
        {
            n.Size = payload.Length;
            if (inline) payload.CopyTo(n.Raw, 0x70);
            else for (int at = 0; at < payload.Length; at += Block)
                n.Data[at / Block] = payload.AsSpan(at, Math.Min(Block, payload.Length - at)).ToArray();
        }
        return n;
    }
    public void Link(string directory, string name, Item target) => _paths[directory].Children.Add((name, target));
    public void FinishMetadata()
    {
        var links = Items.ToDictionary(n => n.Ino, _ => 0);
        foreach (Item n in Items.Where(n => n.Type == 4))
        {
            Item parent = n == Root ? Root : _paths[n.Path.Contains('/') ? n.Path[..n.Path.LastIndexOf('/')] : ""];
            var entries = new[] { (Name: ".", Target: n), (Name: "..", Target: parent) }.Concat(n.Children).ToList();
            foreach (var entry in entries) links[entry.Target.Ino]++;
            byte[] data = DirectoryBytes(entries); n.Size = data.Length;
            for (int at = 0; at < data.Length; at += Block) n.Data[at / Block] = data.AsSpan(at, Math.Min(Block, data.Length - at)).ToArray();
        }
        foreach (Item n in Items)
        {
            BinaryPrimitives.WriteInt16BigEndian(n.Raw.AsSpan(2), checked((short)links[n.Ino])); W64(n.Raw, 0x10, n.Size);
            W32(n.Raw, 0x5C, n.Ea.Length);
        }
    }
    public void Finish(bool lazy = false)
    {
        if (Image.Length == 0) throw new Exception("Metadata-only fixtures require the streaming sparse author.");
        FinishMetadata();
        foreach (Item n in Items)
        {
            long sectors = 0;
            foreach (var (logical, bytes) in n.Data)
            {
                int count = n.Size <= 12L * Block ? Math.Min(Fpb, (int)((n.Size - logical * Block + Frag - 1) / Frag)) : Fpb;
                long at = Allocate(count); bytes.CopyTo(Image, checked(Prefix + (int)at * Frag)); sectors += count * Frag / 512;
                sectors += Pointer(n.Raw, logical, at);
            }
            W32(n.Raw, 0x5C, n.Ea.Length);
            for (int i = 0; i * Block < n.Ea.Length; i++)
            {
                int length = Math.Min(Block, n.Ea.Length - i * Block), count = (length + Frag - 1) / Frag;
                long at = Allocate(count); n.Ea.AsSpan(i * Block, length).CopyTo(Image.AsSpan(Prefix + (int)at * Frag));
                W64(n.Raw, 0x60 + i * 8, at); sectors += count * Frag / 512;
            }
            W64(n.Raw, 0x18, sectors);
            byte[] g = _groups[(int)(n.Ino / Ipg)]; Set(g, R32(g, 0x5C), (int)(n.Ino % Ipg), true);
            n.Raw.CopyTo(Image, checked(Prefix + (int)(n.Ino / Ipg) * Fpg * Frag + Iblk * Frag + (int)(n.Ino % Ipg) * 256));
        }
        long[] total = new long[4];
        for (int c = 0; c < Ncg; c++)
        {
            byte[] g = _groups[c]; int imap = R32(g, 0x5C), fmap = R32(g, 0x60), co = R32(g, 0x6C), so = R32(g, 0x68);
            int[] sums = [Items.Count(n => n.Type == 4 && n.Ino / Ipg == c), 0, 0, 0];
            for (int i = 0; i < Ipg; i++) if (!Get(g, imap, i)) sums[2]++;
            for (int f = 0; f < Fpg; f += Fpb)
            {
                int mask = 0; for (int j = 0; j < Fpb; j++) if (Get(g, fmap, f + j)) mask |= 1 << j;
                if (mask == (1 << Fpb) - 1) { sums[1]++; Set(g, co, f / Fpb, true); }
                else
                {
                    sums[3] += System.Numerics.BitOperations.PopCount((uint)mask);
                    for (int j = 0; j < Fpb;)
                    {
                        if ((mask & (1 << j)) == 0) { j++; continue; }
                        int start = j; while (j < Fpb && (mask & (1 << j)) != 0) j++;
                        int at = 0x34 + (j - start) * 4; W32(g, at, R32(g, at) + 1);
                    }
                }
            }
            for (int b = 0; b < Fpg / Fpb;)
            {
                if (!Get(g, co, b)) { b++; continue; }
                int start = b; while (b < Fpg / Fpb && Get(g, co, b)) b++;
                int at = so + Math.Min(b - start, 16) * 4; W32(g, at, R32(g, at) + 1);
            }
            if (lazy && c > 0 && !Items.Any(n => n.Ino / Ipg == c)) W32(g, 0x78, 0);
            for (int i = 0; i < 4; i++)
            {
                W32(g, 0x18 + i * 4, sums[i]); W32(Image, Prefix + Dblk * Frag + c * 16 + i * 4, sums[i]); total[i] += sums[i];
            }
            g.CopyTo(Image, Prefix + (c * Fpg + Cblk) * Frag);
        }
        for (int i = 0; i < 4; i++) W64(Super, 0x3F0 + i * 8, total[i]);
        Super.CopyTo(Image, Prefix + 65536);
        for (int c = 0; c < Ncg; c++) Super.CopyTo(Image, Prefix + (c * Fpg + Sblk) * Frag);
    }
    private long Allocate(int count)
    {
        for (int c = 0; c < Ncg; c++)
        {
            byte[] g = _groups[c]; int map = R32(g, 0x60);
            for (int f = 0; f <= Fpg - count; f++)
            {
                if ((c == 0 && f == 0) || f / Fpb != (f + count - 1) / Fpb || (count == Fpb && f % Fpb != 0)) continue;
                if (!Enumerable.Range(f, count).All(i => Get(g, map, i))) continue;
                for (int i = 0; i < count; i++) Set(g, map, f + i, false);
                return (long)c * Fpg + f;
            }
        }
        throw new Exception("Fixture data capacity.");
    }
    private long Pointer(byte[] inode, long logical, long data)
    {
        if (logical < 12) { W64(inode, 0x70 + (int)logical * 8, data); return 0; }
        int fan = Block / 8; long remaining = logical - 12, capacity = fan, allocated = 0;
        for (int depth = 1; depth <= 3; depth++, capacity *= fan)
        {
            if (remaining >= capacity) { remaining -= capacity; continue; }
            int root = 0xC8 + depth * 8; long p = R64(inode, root);
            if (p == 0) { p = Allocate(Fpb); W64(inode, root, p); allocated++; }
            for (int level = depth; level > 0; level--)
            {
                long stride = 1; for (int i = 1; i < level; i++) stride *= fan;
                int slot = checked(Prefix + (int)p * Frag + (int)(remaining / stride) * 8); remaining %= stride;
                if (level == 1) W64(Image, slot, data);
                else { long next = R64(Image, slot); if (next == 0) { next = Allocate(Fpb); W64(Image, slot, next); allocated++; } p = next; }
            }
            return allocated * Block / 512;
        }
        throw new Exception("Fixture pointer capacity.");
    }
    private byte[] DirectoryBytes(List<(string Name, Item Target)> entries)
    {
        var sectors = new List<byte[]>(); byte[] sector = new byte[512]; int p = 0, last = -1;
        foreach (var (name, target) in entries)
        {
            byte[] bytes = RawNames.TryGetValue(name, out var rawName) ? rawName : Encoding.UTF8.GetBytes(name); int length = (8 + bytes.Length + 1 + 3) / 4 * 4;
            if (p + length > 512) { BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(last + 4), (ushort)(512 - last)); sectors.Add(sector); sector = new byte[512]; p = 0; }
            W32(sector, p, (int)target.Ino); BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(p + 4), (ushort)length);
            sector[p + 6] = (byte)target.Type; sector[p + 7] = (byte)bytes.Length; bytes.CopyTo(sector, p + 8); last = p; p += length;
        }
        BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(last + 4), (ushort)(512 - last)); sectors.Add(sector);
        return sectors.SelectMany(s => s).ToArray();
    }
    public static byte[] Registry(params (string Number, string Account)[] users)
    {
        byte[] b = new byte[0x40000]; new byte[] { 0xBC, 0xAD, 0xAD, 0xBC }.CopyTo(b, 0);
        int key = 0x100, value = 0x10002;
        foreach (var (number, account) in users)
        {
            byte[] name = Encoding.ASCII.GetBytes($"/setting/user/{number}/npaccount/accountid\0"); name.CopyTo(b, key);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(value), (ushort)(key - 0x15));
            b[value + 5] = 0x11; b[value + 6] = 2; Encoding.ASCII.GetBytes(account).CopyTo(b, value + 7); key += 128; value += 40;
        }
        return b;
    }
    public static byte[] Payload(int length, int seed) { byte[] b = new byte[length]; new Random(seed).NextBytes(b); return b; }
    public static int R32(byte[] b, int p) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
    public static long R64(byte[] b, int p) => BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(p));
    public static void W32(byte[] b, int p, int value) => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(p), value);
    public static void W64(byte[] b, int p, long value) => BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), value);
    public static bool Get(byte[] b, int start, int n) => (b[start + n / 8] & (1 << (n % 8))) != 0;
    public static void Set(byte[] b, int start, int n, bool value) { if (value) b[start + n / 8] |= (byte)(1 << (n % 8)); else b[start + n / 8] &= (byte)~(1 << (n % 8)); }
}
