using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PS3HddTool.Core.Migration;

static class Oracle
{
    public static void Check(Fixture source, byte[] result, MigrationManifest manifest, bool auditMaps = true)
        => Check(source, (at, count) => result.AsSpan(checked((int)at), count).ToArray(), Fixture.Prefix, manifest, auditMaps: auditMaps);
    public static void Check(Fixture source, Func<long, int, byte[]> readAt, long offset, MigrationManifest manifest, Action<long, int>? onExtent = null, bool auditMaps = true)
    {
        var map = manifest.Mapping.ToDictionary(p => p.Source, p => p.Destination);
        byte[] sb = readAt(offset + 65536, 8192);
        int block = Fixture.R32(sb, 0x30), frag = Fixture.R32(sb, 0x34);
        int ipg = Fixture.R32(sb, 0xB8), fpg = Fixture.R32(sb, 0xBC), iblk = Fixture.R32(sb, 0x10);
        int ncg = Fixture.R32(sb, 0x2C), cblk = Fixture.R32(sb, 12), dblk = Fixture.R32(sb, 20), sblk = Fixture.R32(sb, 8);
        int fpb = block / frag;
        var inodesByGroup = map.Values.GroupBy(i => i / ipg).ToDictionary(g => g.Key, g => g.Select(i => (int)(i % ipg)).ToHashSet());
        var directoriesByGroup = source.Items.Where(n => n.Type == 4).GroupBy(n => map[n.Ino] / ipg).ToDictionary(g => g.Key, g => g.Count());
        long[] totals = new long[4]; var owned = new HashSet<long>();
        byte[] Read(long address, int length) => readAt(checked(offset + address * frag), length);
        byte[] summary = Read(Fixture.R64(sb, 0x448), Fixture.R32(sb, 0x9C));
        long Resolve(byte[] inode, long logical)
        {
            if (logical < 12) return Fixture.R64(inode, 0x70 + (int)logical * 8);
            long n = logical - 12, capacity = block / 8;
            for (int depth = 1; depth <= 3; depth++, capacity *= block / 8)
            {
                if (n >= capacity) { n -= capacity; continue; }
                long pointer = Fixture.R64(inode, 0xC8 + depth * 8);
                for (int level = depth; level > 0 && pointer != 0; level--)
                {
                    long stride = 1; for (int i = 1; i < level; i++) stride *= block / 8;
                    byte[] table = Read(pointer, block); pointer = Fixture.R64(table, (int)(n / stride) * 8); n %= stride;
                }
                return pointer;
            }
            throw new Exception("Oracle logical address overflow.");
        }
        void Own(long at, int fragments)
        {
            onExtent?.Invoke(at, fragments);
            if (at <= 0 || at % fpb + fragments > fpb) throw new Exception("Oracle invalid extent.");
            for (int i = 0; i < fragments; i++) if (!owned.Add(at + i)) throw new Exception("Oracle duplicate fragment.");
        }
        void Indirect(long at, int level, long count)
        {
            if (at == 0) return; Own(at, fpb); if (level == 1) return;
            byte[] table = Read(at, block); long capacity = 1; for (int i = 1; i < level; i++) capacity *= block / 8;
            for (int i = 0; count > 0; i++) { long take = Math.Min(count, capacity); Indirect(Fixture.R64(table, i * 8), level - 1, take); count -= take; }
        }
        foreach (var item in source.Items)
        {
            long ino = map[item.Ino]; long at = checked(offset + (ino / ipg) * fpg * frag + (long)iblk * frag + (ino % ipg) * 256);
            byte[] actual = readAt(at, 256), expected = (byte[])item.Raw.Clone(), masked = (byte[])actual.Clone();
            Array.Clear(expected, 0x18, 8); Array.Clear(masked, 0x18, 8); Array.Clear(expected, 0x60, 16); Array.Clear(masked, 0x60, 16);
            bool inline = item.Type == 10 && item.Data.Count == 0;
            if (!inline && item.Type is 4 or 8 or 10) { Array.Clear(expected, 0x70, 120); Array.Clear(masked, 0x70, 120); }
            Equal(expected, masked, "raw metadata including owner, flags, times, generation and inline target");
            long blocks = (item.Size + block - 1) / block;
            if (!inline && item.Type is 4 or 8 or 10)
            {
                for (long logical = 0; logical < blocks; logical++)
                {
                    long address = Resolve(actual, logical); int count = (int)Math.Min(block, item.Size - logical * block);
                    bool exists = item.Data.TryGetValue(logical, out byte[]? payload);
                    if ((address != 0) != exists) throw new Exception("Oracle sparse topology.");
                    if (address == 0) continue;
                    byte[] bytes = Read(address, count);
                    Own(address, item.Size <= 12L * block ? (count + frag - 1) / frag : fpb);
                    if (item.Type == 4)
                    {
                        payload = (byte[])payload!.Clone();
                        for (int p = 0; p < bytes.Length;)
                        {
                            uint old = BinaryPrimitives.ReadUInt32BigEndian(payload!.AsSpan(p));
                            uint copied = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p));
                            if (copied != (old == 0 ? 0 : map[old])) throw new Exception("Oracle directory inode remap.");
                            bytes.AsSpan(p, 4).CopyTo(payload.AsSpan(p, 4)); // use a clone below, never mutate fixture
                            p += BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p + 4));
                        }
                        // Record bytes other than inode numbers must be identical.
                        byte[] reference = payload;
                        Equal(reference, bytes, "raw directory records and names");
                    }
                    else { byte[] padded = new byte[count]; payload!.CopyTo(padded, 0); Equal(padded, bytes, "payload bytes"); }
                }
                long remaining = Math.Max(0, blocks - 12), capacity = block / 8;
                for (int level = 1; level <= 3 && remaining > 0; level++, capacity *= block / 8)
                { long take = Math.Min(remaining, capacity); Indirect(Fixture.R64(actual, 0xC8 + level * 8), level, take); remaining -= take; }
            }
            for (int i = 0; i * block < item.Ea.Length; i++)
            {
                int count = Math.Min(block, item.Ea.Length - i * block); long address = Fixture.R64(actual, 0x60 + i * 8);
                Equal(item.Ea.AsSpan(i * block, count).ToArray(), Read(address, count), "extended attributes"); Own(address, (count + frag - 1) / frag);
            }
        }
        // A recovery migration generates a container/evidence files. The subset
        // oracle still checks EVERY original unchanged object, name byte, sparse
        // extent and EA independently; strict production inventory audits all maps.
        if (!auditMaps) return;
        for (int c = 0; c < ncg; c++)
        {
            byte[] g = Read((long)c * fpg + cblk, Fixture.R32(sb, 0xA0));
            int groupFragments = Fixture.R32(g, 0x14);
            int imap = Fixture.R32(g, 0x5C), fmap = Fixture.R32(g, 0x60), co = Fixture.R32(g, 0x6C), so = Fixture.R32(g, 0x68);
            var usedInodes = inodesByGroup.GetValueOrDefault(c) ?? new HashSet<int>();
            if (c == 0) { usedInodes.Add(0); usedInodes.Add(1); }
            for (int i = 0; i < ipg; i++) if (Fixture.Get(g, imap, i) != usedInodes.Contains(i)) throw new Exception("Oracle inode bitmap.");
            int[] counts = [directoriesByGroup.GetValueOrDefault(c), 0, ipg - usedInodes.Count, 0];
            int[] frsum = new int[8], clusters = new int[17]; int run = 0;
            for (int f = 0; f < groupFragments; f += fpb)
            {
                int free = 0, frun = 0;
                for (int j = 0; j < Math.Min(fpb, groupFragments - f); j++)
                {
                    long address = (long)c * fpg + f + j;
                    bool reserved = (f + j < dblk && (c == 0 || f + j >= sblk)) ||
                        (address >= Fixture.R64(sb, 0x448) && address < Fixture.R64(sb, 0x448) + Fixture.R32(sb, 0x9C) / frag);
                    bool isFree = Fixture.Get(g, fmap, f + j);
                    if (isFree == (reserved || owned.Contains(address))) throw new Exception("Oracle fragment bitmap/ownership.");
                    if (isFree) { free++; frun++; }
                    else if (frun > 0) { frsum[frun]++; frun = 0; }
                }
                if (frun > 0 && frun < fpb) frsum[frun]++;
                bool whole = free == fpb;
                if (f / fpb < groupFragments / fpb && Fixture.Get(g, co, f / fpb) != whole) throw new Exception("Oracle cluster bitmap.");
                if (whole) { counts[1]++; run++; }
                else { counts[3] += free; if (run > 0) { clusters[Math.Min(run, 16)]++; run = 0; } }
            }
            if (run > 0) clusters[Math.Min(run, 16)]++;
            for (int i = 1; i < fpb; i++) if (Fixture.R32(g, 0x34 + i * 4) != frsum[i]) throw new Exception("Oracle fragment summary.");
            for (int i = 1; i <= 16; i++) if (Fixture.R32(g, so + i * 4) != clusters[i]) throw new Exception("Oracle cluster summary.");
            for (int i = 0; i < 4; i++)
            {
                if (Fixture.R32(g, 0x18 + i * 4) != counts[i] || Fixture.R32(summary, c * 16 + i * 4) != counts[i])
                    throw new Exception("Oracle CG/summary count.");
                totals[i] += counts[i];
            }
        }
        for (int i = 0; i < 4; i++) if (Fixture.R64(sb, 0x3F0 + i * 8) != totals[i]) throw new Exception("Oracle superblock total.");
    }
    public static void Equal(byte[] expected, byte[] actual, string reason)
    { if (!expected.AsSpan().SequenceEqual(actual)) throw new Exception("Oracle mismatch: " + reason); }
}
