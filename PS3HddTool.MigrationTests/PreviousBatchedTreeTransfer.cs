namespace PS3HddTool.Core.Migration;

internal static class PreviousBatchedTreeTransfer
{
    internal static Dictionary<long, long> Copy(UfsVolume source, UfsVolume destination,
        Action<string>? progress, CancellationToken token, Action<MigrationProgress>? telemetry = null)
    {
        var meter = new ProgressMeter(telemetry, "copy-tree", "Copy allocated filesystem tree",
            source.Nodes.Values.Sum(n => n.Sectors) * 512);
        long finished = 0;
        meter.Add(0, nodesDone: 0, nodesTotal: source.Nodes.Count, force: true);
        var mapping = new Dictionary<long, long> { [2] = destination.AllocateInode(true) };
        foreach (long ino in source.Nodes.Keys.Order())
        { token.ThrowIfCancellationRequested(); if (ino != 2) mapping.Add(ino, destination.AllocateInode()); }
        var dataBuffer = new PreviousTransferDataBuffer(destination, token);
        foreach (Node n in source.Nodes.Values.OrderBy(n => n.Number))
        {
            token.ThrowIfCancellationRequested(); progress?.Invoke($"Copy inode {n.Number}"); token.ThrowIfCancellationRequested();
            byte[] raw = (byte[])n.Raw.Clone(); long sectors = 0;
            var pointersCache = new TransferPointerCache(destination, token);
            using var sourceHash = new TransferSourceHash(n, token);
            Array.Clear(raw, 0x60, 16);
            if (!n.Inline && n.Type is 4 or 8 or 10)
            {
                Array.Clear(raw, 0x70, 120);
                foreach (var block in source.NonzeroBlocks(n))
                {
                    token.ThrowIfCancellationRequested();
                    byte[] bytes = source.Read(block.Fragment, source.DataLength(n, block.Logical));
                    sourceHash.Add(checked(block.Logical * source.Block), bytes);
                    if (n.Type == 4) ImageMigrator.RewriteDirectory(bytes, (int)Math.Min(bytes.Length, n.Size - block.Logical * source.Block), mapping);
                    long address = destination.Allocate(bytes.Length / destination.Fragment);
                    dataBuffer.Write(address, bytes); sectors += bytes.Length / 512;
                    long pointers = destination.PutPointer(raw, block.Logical, address, pointersCache); sectors += pointers;
                    meter.Add(bytes.Length + pointers * 512, n.Number, n.Path, finished, source.Nodes.Count);
                }
            }
            sourceHash.Complete(); dataBuffer.Flush(); pointersCache.Flush();
            int ea = UfsVolume.I32(n.Raw, 0x5C);
            for (int i = 0; i * source.Block < ea; i++)
            {
                token.ThrowIfCancellationRequested();
                int length = ((Math.Min(source.Block, ea - i * source.Block) + source.Fragment - 1) / source.Fragment) * source.Fragment;
                byte[] bytes = source.Read(UfsVolume.I64(n.Raw, 0x60 + i * 8), length);
                long address = destination.Allocate(length / destination.Fragment); destination.Write(address, bytes);
                UfsVolume.P64(raw, 0x60 + i * 8, address); sectors += length / 512;
                meter.Add(length, n.Number, n.Path, finished, source.Nodes.Count);
            }
            UfsVolume.P64(raw, 0x18, sectors);
            RegularImage.Write(destination.Disk, destination.InodeOffset(mapping[n.Number]), raw);
            meter.Add(0, n.Number, n.Path, ++finished, source.Nodes.Count, force: finished == source.Nodes.Count);
        }
        return mapping;
    }
}



// Scoped to one inode. Failed/cancelled copies discard these buffers; disposal
// must never publish writes during rollback. The caller explicitly completes them.
internal sealed class PreviousTransferDataBuffer(UfsVolume volume, CancellationToken token)
{
    private readonly byte[] _buffer = new byte[1024 * 1024];
    private long _start;
    private int _length;
    internal void Write(long fragment, byte[] data)
    {
        long offset = checked(fragment * volume.Fragment);
        if (_length > 0 && (offset != _start + _length || data.Length > _buffer.Length - _length)) Flush();
        if (data.Length > _buffer.Length) { token.ThrowIfCancellationRequested(); volume.Write(fragment, data); return; }
        if (_length == 0) _start = offset;
        data.CopyTo(_buffer, _length); _length += data.Length;
    }
    internal void Flush()
    {
        if (_length == 0) return;
        token.ThrowIfCancellationRequested();
        RegularImage.Write(volume.Disk, checked(volume.Offset + _start), _buffer.AsSpan(0, _length).ToArray());
        _length = 0;
    }
}
