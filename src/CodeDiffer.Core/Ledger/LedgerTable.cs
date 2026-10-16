using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;
using System.Text.Unicode;

namespace CodeDiffer.Core.Ledger;

/// <summary>
/// A ledger base held as the file's own bytes, with an index of row numbers by path hash; an entry is built when it
/// is looked up. A dictionary of the same entries held a path string and two hex strings each, about three times
/// the bytes. The journal's changes sit on top, by path (a null value is a removal). Read-only once built, so
/// lookups may run in parallel.
/// </summary>
internal sealed class LedgerTable : IReadOnlyDictionary<string, LedgerEntry>
{
    private readonly byte[] _data;
    private readonly int _version;
    private readonly long[] _off;
    private readonly int _rows;
    private readonly int[] _slots; // row + 1 by path hash (open addressing; 0 = empty)
    private readonly bool[] _shadowed; // a row a later row of the same path replaces, as a dictionary's last write would
    private readonly Dictionary<string, LedgerEntry?> _overlay = new(StringComparer.Ordinal);
    private int _count;

    private LedgerTable(byte[] data, int version, long[] off, int rows, int[] slots, bool[] shadowed, int count)
    {
        _data = data;
        _version = version;
        _off = off;
        _rows = rows;
        _slots = slots;
        _shadowed = shadowed;
        _count = count;
    }

    /// <summary>The table over a base whose section bounds are checked; null if a path's offsets are out of range
    /// (corrupt). <paramref name="exact"/> is false when a path is not valid UTF-8: decoded, two such paths could
    /// become one string, so the caller builds a dictionary instead.</summary>
    public static LedgerTable? Build(byte[] data, int version, long[] off, int rows, out bool exact)
    {
        exact = true;
        var s = data.AsSpan();
        long blobOff = off[1], blobLen = off[2] - off[1];
        int cap = 1;
        while (cap < (long)rows * 2) cap <<= 1;
        var slots = new int[cap];
        var shadowed = new bool[rows];
        int count = 0;
        for (int i = 0; i < rows; i++)
        {
            long o0 = I64(s, off[0] + (long)i * 8), o1 = I64(s, off[0] + (long)(i + 1) * 8);
            if (o0 < 0 || o1 < o0 || o1 > blobLen) return null; // never blobOff + o1: it can overflow
            var path = s.Slice((int)(blobOff + o0), (int)(o1 - o0));
            if (!Utf8.IsValid(path))
            {
                exact = false;
                return null;
            }
            int at = (int)XxHash3.HashToUInt64(path) & (cap - 1);
            while (slots[at] != 0 && !path.SequenceEqual(PathOf(s, off, slots[at] - 1))) at = (at + 1) & (cap - 1);
            if (slots[at] != 0) shadowed[slots[at] - 1] = true;
            else count++;
            slots[at] = i + 1;
        }
        return new LedgerTable(data, version, off, rows, slots, shadowed, count);
    }

    public int Count => _count;

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out LedgerEntry value)
    {
        if (_overlay.TryGetValue(key, out var o))
        {
            value = o.GetValueOrDefault();
            return o.HasValue;
        }
        int row = Row(key);
        value = row < 0 ? default : Entry(row);
        return row >= 0;
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public LedgerEntry this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);

    /// <summary>A journal upsert.</summary>
    internal void Set(string key, LedgerEntry e)
    {
        if (!ContainsKey(key)) _count++;
        _overlay[key] = e;
    }

    /// <summary>A journal removal.</summary>
    internal void Remove(string key)
    {
        if (!ContainsKey(key)) return;
        _count--;
        if (Row(key) >= 0) _overlay[key] = null;
        else _overlay.Remove(key);
    }

    public IEnumerator<KeyValuePair<string, LedgerEntry>> GetEnumerator()
    {
        for (int i = 0; i < _rows; i++)
        {
            if (_shadowed[i]) continue;
            var key = Encoding.UTF8.GetString(PathOf(_data, _off, i));
            if (!_overlay.ContainsKey(key)) yield return new(key, Entry(i));
        }
        foreach (var (k, v) in _overlay)
            if (v.HasValue) yield return new(k, v.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IEnumerable<string> Keys => this.Select(e => e.Key);
    public IEnumerable<LedgerEntry> Values => this.Select(e => e.Value);

    private int Row(string key)
    {
        int max = Encoding.UTF8.GetMaxByteCount(key.Length);
        byte[]? rented = max > 1024 ? ArrayPool<byte>.Shared.Rent(max) : null;
        try
        {
            Span<byte> buf = rented ?? stackalloc byte[1024];
            // A lone surrogate has no UTF-8 form: no path read off disk decodes to it.
            if (Utf8.FromUtf16(key, buf, out _, out int n, replaceInvalidSequences: false) != OperationStatus.Done) return -1;
            var path = buf[..n];
            int mask = _slots.Length - 1, at = (int)XxHash3.HashToUInt64(path) & mask;
            for (; _slots[at] != 0; at = (at + 1) & mask)
                if (path.SequenceEqual(PathOf(_data, _off, _slots[at] - 1))) return _slots[at] - 1;
            return -1;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private LedgerEntry Entry(int i)
    {
        var s = _data.AsSpan();
        var xx = LedgerFormat.HexOrAbsent(s.Slice((int)(_off[4] + (long)i * LedgerFormat.XxBytes), LedgerFormat.XxBytes));
        return _version == 1
            ? new LedgerEntry(I64(s, _off[2] + (long)i * 8), I64(s, _off[3] + (long)i * 8), xx)
            : new LedgerEntry(I64(s, _off[2] + (long)i * 8), I64(s, _off[3] + (long)i * 8), xx,
                I64(s, _off[5] + (long)i * 8), I64(s, _off[6] + (long)i * 8), I64(s, _off[7] + (long)i * 8),
                LedgerFormat.HexOrAbsent(s.Slice((int)(_off[8] + (long)i * LedgerFormat.ShaBytes), LedgerFormat.ShaBytes)));
    }

    private static ReadOnlySpan<byte> PathOf(ReadOnlySpan<byte> s, long[] off, int i)
    {
        long o0 = I64(s, off[0] + (long)i * 8), o1 = I64(s, off[0] + (long)(i + 1) * 8);
        return s.Slice((int)(off[1] + o0), (int)(o1 - o0));
    }

    private static long I64(ReadOnlySpan<byte> s, long at) => BinaryPrimitives.ReadInt64LittleEndian(s[(int)at..]);
}
