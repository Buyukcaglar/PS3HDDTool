namespace PS3HddTool.Core.Migration;

// Scoped to one inode. Failed/cancelled copies discard these buffers; disposal
// must never publish writes during rollback. The caller explicitly completes them.
internal sealed class TransferDataBuffer(UfsVolume volume, CancellationToken token)
{
    private readonly byte[] _buffer = new byte[4 * 1024 * 1024];
    private long _start;
    private int _length;
    internal void Write(long fragment, ReadOnlySpan<byte> data)
    {
        long offset = checked(fragment * volume.Fragment);
        if (_length > 0 && (offset != _start + _length || data.Length > _buffer.Length - _length)) Flush();
        if (data.Length > _buffer.Length) { token.ThrowIfCancellationRequested(); volume.Write(fragment, data.ToArray()); return; }
        if (_length == 0) _start = offset;
        data.CopyTo(_buffer.AsSpan(_length)); _length += data.Length;
    }
    internal void Flush()
    {
        if (_length == 0) return;
        token.ThrowIfCancellationRequested();
        RegularImage.Write(volume.Disk, checked(volume.Offset + _start), _buffer.AsSpan(0, _length).ToArray());
        _length = 0;
    }
}

internal sealed class TransferPointerCache(UfsVolume volume, CancellationToken token)
{
    private const int MaximumPages = 16;
    private readonly Dictionary<long, byte[]> _pages = new();
    private readonly Queue<long> _order = new();
    internal byte[] Get(long fragment, bool fresh = false)
    {
        if (_pages.TryGetValue(fragment, out var page)) return page;
        if (_pages.Count == MaximumPages) FlushOne();
        page = fresh ? new byte[volume.Block] : volume.Read(fragment, volume.Block);
        _pages.Add(fragment, page); _order.Enqueue(fragment); return page;
    }
    private void FlushOne()
    {
        token.ThrowIfCancellationRequested();
        long fragment = _order.Dequeue();
        volume.Write(fragment, _pages[fragment]); _pages.Remove(fragment);
    }
    internal void Flush() { while (_order.Count != 0) FlushOne(); }
}
