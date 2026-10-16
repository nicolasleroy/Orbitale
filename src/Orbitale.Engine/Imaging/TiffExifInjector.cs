using System.Buffers.Binary;

namespace Orbitale.Engine.Imaging;

/// <summary>
/// Magick.NET's TIFF encoder silently drops the EXIF profile, so EXIF is merged into IFD0 of the
/// written classic-TIFF file by appending data at the end of the file and rewriting IFD0 there.
/// Tags already present in the TIFF win over EXIF IFD0 tags.
/// </summary>
internal static class TiffExifInjector
{
    const ushort ExifIfdPointer = 0x8769, GpsIfdPointer = 0x8825, InteropIfdPointer = 0xA005;
    const int MaxIfdDepth = 3;   // IFD0 → Exif/GPS IFD → Interop IFD
    static readonly HashSet<ushort> SkippedIfd0Tags = [273, 279, 330, 513, 514, 34675];

    sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Payload);

    static int TypeSize(ushort t) => t switch
    {
        1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0,
    };

    // Swap unit inside one element: rationals are two 32-bit words.
    static int SwapUnit(ushort t) => t switch { 5 or 10 => 4, _ => TypeSize(t) };

    public static void Inject(string tiffPath, byte[] exif)
    {
        int start = exif.Length >= 6 && exif[0] == 'E' && exif[1] == 'x' && exif[2] == 'i' && exif[3] == 'f' ? 6 : 0;
        if (exif.Length - start < 8) return;
        var blob = exif.AsSpan(start).ToArray();
        bool srcLE = blob[0] == 'I' && blob[1] == 'I';
        if (!srcLE && !(blob[0] == 'M' && blob[1] == 'M')) return;

        using var fs = new FileStream(tiffPath, FileMode.Open, FileAccess.ReadWrite);
        var head = new byte[8];
        if (fs.Read(head, 0, 8) != 8) return;
        bool dstLE = head[0] == 'I' && head[1] == 'I';
        if (!dstLE && !(head[0] == 'M' && head[1] == 'M')) return;
        if (Rd16(head, 2, dstLE) != 42) return; // classic TIFF only

        uint ifd0 = Rd32(head, 4, dstLE);
        fs.Position = ifd0;
        var cb = new byte[2]; fs.ReadExactly(cb);
        int n = Rd16(cb, 0, dstLE);
        var raw = new byte[n * 12 + 4]; fs.ReadExactly(raw);
        var entries = new List<byte[]>();
        var present = new HashSet<ushort>();
        for (int i = 0; i < n; i++)
        {
            var e = raw.AsSpan(i * 12, 12).ToArray();
            entries.Add(e); present.Add((ushort)Rd16(e, 0, dstLE));
        }
        var nextPtr = raw.AsSpan(n * 12, 4).ToArray();

        long Append(byte[] data)
        {
            long pos = fs.Length;
            if ((pos & 1) != 0) { fs.Position = pos; fs.WriteByte(0); pos++; }
            fs.Position = pos; fs.Write(data);
            if (pos + data.Length > uint.MaxValue) throw new IOException("TIFF too large for EXIF injection.");
            return pos;
        }

        byte[] ToRaw(Entry e, uint offsetOrValue, bool inlineData)
        {
            var r = new byte[12];
            Wr16(r, 0, e.Tag, dstLE); Wr16(r, 2, e.Type, dstLE); Wr32(r, 4, e.Count, dstLE);
            if (inlineData) e.Payload.CopyTo(r, 8); else Wr32(r, 8, offsetOrValue, dstLE);
            return r;
        }

        byte[] Build(Entry e)
        {
            if (e.Payload.Length <= 4) return ToRaw(e, 0, true);
            return ToRaw(e, (uint)Append(e.Payload), false);
        }

        List<Entry>? ReadIfd(uint off)
        {
            if (off + 2 > blob.Length) return null;
            int cnt = Rd16(blob, (int)off, srcLE);
            if (off + 2 + cnt * 12L > blob.Length) return null;
            var list = new List<Entry>();
            for (int i = 0; i < cnt; i++)
            {
                int p = (int)off + 2 + i * 12;
                ushort tag = (ushort)Rd16(blob, p, srcLE), type = (ushort)Rd16(blob, p + 2, srcLE);
                uint count = Rd32(blob, p + 4, srcLE);
                int es = TypeSize(type);
                if (es == 0) continue;
                long len = (long)es * count;
                if (len > int.MaxValue) continue;
                int dataPos;
                if (len <= 4) dataPos = p + 8;
                else
                {
                    uint o = Rd32(blob, p + 8, srcLE);
                    if (o + len > blob.Length) continue;
                    dataPos = (int)o;
                }
                var payload = blob.AsSpan(dataPos, (int)len).ToArray();
                if (srcLE != dstLE)
                {
                    int u = SwapUnit(type);
                    if (u > 1) for (int k = 0; k + u <= payload.Length; k += u) Array.Reverse(payload, k, u);
                }
                if (payload.Length < 4) payload = [.. payload, .. new byte[4 - payload.Length]];
                list.Add(new Entry(tag, type, count, payload));
            }
            return list;
        }

        // Produces final 12-byte entries (children appended first so offsets are known). Malformed EXIF must never loop:
        // a sub-IFD already visited or nested deeper than IFD0 → Exif → Interop is dropped.
        var visited = new HashSet<uint>();
        List<byte[]> Materialize(List<Entry> list, Func<ushort, bool> keep, int depth)
        {
            var res = new List<(ushort tag, byte[] raw)>();
            foreach (var e in list)
            {
                if (!keep(e.Tag)) continue;
                if (e.Tag is ExifIfdPointer or GpsIfdPointer or InteropIfdPointer)
                {
                    uint childOff = Rd32(e.Payload, 0, dstLE); // already swapped to dst order
                    // payload was swapped to dst order above; childOff is in blob coordinates
                    if (depth >= MaxIfdDepth || !visited.Add(childOff)) continue;
                    var child = ReadIfd(childOff);
                    if (child is null) continue;
                    var childEntries = Materialize(child, _ => true, depth + 1);
                    long pos = WriteIfd(childEntries);
                    var ptr = new byte[4]; Wr32(ptr, 0, (uint)pos, dstLE);
                    res.Add((e.Tag, ToRaw(new Entry(e.Tag, 4, 1, ptr), 0, true)));
                }
                else res.Add((e.Tag, Build(e)));
            }
            return res.OrderBy(x => x.tag).Select(x => x.raw).ToList();
        }

        long WriteIfd(List<byte[]> raws)
        {
            var buf = new byte[2 + raws.Count * 12 + 4];
            Wr16(buf, 0, (ushort)raws.Count, dstLE);
            for (int i = 0; i < raws.Count; i++) raws[i].CopyTo(buf, 2 + i * 12);
            return Append(buf);
        }

        uint exifIfd0Offset = Rd32(blob, 4, srcLE);
        var exifIfd0 = ReadIfd(exifIfd0Offset);
        if (exifIfd0 is null) return;
        visited.Add(exifIfd0Offset);
        var added = Materialize(exifIfd0, t => !present.Contains(t) && !SkippedIfd0Tags.Contains(t), 1);
        if (added.Count == 0) return;

        var merged = entries.Concat(added).OrderBy(e => Rd16(e, 0, dstLE)).ToList();
        var final = new byte[2 + merged.Count * 12 + 4];
        Wr16(final, 0, (ushort)merged.Count, dstLE);
        for (int i = 0; i < merged.Count; i++) merged[i].CopyTo(final, 2 + i * 12);
        nextPtr.CopyTo(final, 2 + merged.Count * 12);
        long newIfd0 = Append(final);
        var ptrBytes = new byte[4]; Wr32(ptrBytes, 0, (uint)newIfd0, dstLE);
        fs.Position = 4; fs.Write(ptrBytes);
    }

    static int Rd16(byte[] b, int o, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    static uint Rd32(byte[] b, int o, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    static void Wr16(byte[] b, int o, ushort v, bool le) { if (le) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v); }
    static void Wr32(byte[] b, int o, uint v, bool le) { if (le) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v); }
}
