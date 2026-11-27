// A reader of the MMDB format (a binary search tree over IP address bits, then a data section) for the one thing the server asks of it: the latitude and longitude a city-level
// IP database such as DB-IP's gives an address. Reads the file on demand and holds it open; a lookup is safe from any thread.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Resurgence.Server;

public sealed class Mmdb : IDisposable
{
    static readonly byte[] Marker = [171, 205, 239, .. "MaxMind.com"u8];
    const int MaxMetadata = 128 * 1024, MaxValue = 1 << 20, Depth = 16;

    readonly SafeFileHandle file;
    readonly long length;
    readonly uint nodeCount;
    readonly int recordBits, nodeBytes;
    readonly long treeBytes;
    readonly bool ipv6;
    readonly uint ipv4Start;
    long dataBase;  // what a pointer counts from: the metadata's start while it is read, then the data section

    /// <summary>Opens the file; InvalidDataException when it is no MMDB file of a version and record size this reader knows.</summary>
    public Mmdb(string path)
    {
        file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            length = RandomAccess.GetLength(file);
            var tail = (int)Math.Min(length, MaxMetadata);
            var end = Read(length - tail, tail);
            var at = end.AsSpan().LastIndexOf(Marker);
            if (at < 0) throw new InvalidDataException("not an MMDB file: no metadata");
            dataBase = length - tail + at + Marker.Length;
            var meta = Decode(dataBase, 0, out _) as Dictionary<string, object?> ?? throw new InvalidDataException("not an MMDB file: no metadata");
            if (Number(meta, "binary_format_major_version") != 2) throw new InvalidDataException("not an MMDB file of format 2");
            nodeCount = (uint)Number(meta, "node_count");
            recordBits = (int)Number(meta, "record_size");
            ipv6 = Number(meta, "ip_version") switch { 4 => false, 6 => true, _ => throw new InvalidDataException("MMDB: ip_version is not 4 or 6") };
            if (recordBits is not (24 or 28 or 32)) throw new InvalidDataException("MMDB: record_size is not 24, 28 or 32");
            nodeBytes = recordBits / 4;
            treeBytes = (long)nodeCount * nodeBytes;
            if (treeBytes + 16 > length) throw new InvalidDataException("MMDB: the search tree is longer than the file");
            dataBase = treeBytes + 16;
            // an IPv4 address sits under the 96 zero bits of an IPv6 tree
            var node = 0u;
            for (var i = 0; ipv6 && i < 96 && node < nodeCount; i++) node = Record(node, 0);
            ipv4Start = node;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    static long Number(Dictionary<string, object?> meta, string key) =>
        meta.TryGetValue(key, out var v) && v is long n ? n : throw new InvalidDataException($"MMDB: no {key}");

    public void Dispose() => file.Dispose();

    byte[] Read(long offset, int count)
    {
        var bytes = new byte[count];
        if (offset < 0 || offset + count > length) throw new InvalidDataException("MMDB: a read past the end of the file");
        for (var done = 0; done < count;)
        {
            var n = RandomAccess.Read(file, bytes.AsSpan(done), offset + done);
            if (n == 0) throw new InvalidDataException("MMDB: the file ended early");
            done += n;
        }
        return bytes;
    }

    /// <summary>The left (bit 0) or right (bit 1) record of a node.</summary>
    uint Record(uint node, int bit)
    {
        var b = Read(node * (long)nodeBytes, nodeBytes);
        return recordBits switch
        {
            24 => bit == 0 ? (uint)(b[0] << 16 | b[1] << 8 | b[2]) : (uint)(b[3] << 16 | b[4] << 8 | b[5]),
            28 => bit == 0 ? (uint)((b[3] & 240) << 20 | b[0] << 16 | b[1] << 8 | b[2]) : (uint)((b[3] & 15) << 24 | b[4] << 16 | b[5] << 8 | b[6]),
            _ => bit == 0 ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : (uint)(b[4] << 24 | b[5] << 16 | b[6] << 8 | b[7]),
        };
    }

    /// <summary>The place the file gives the address (its record's `location`), null when the file has none for it. An IPv4 address in an IPv4-only or IPv6 file, an IPv6 one in an IPv6 file only.</summary>
    public (double Lat, double Lon)? Find(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bits = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !ipv6) return null;
        var node = address.AddressFamily == AddressFamily.InterNetwork && ipv6 ? ipv4Start : 0u;
        for (var i = 0; i < bits.Length * 8 && node < nodeCount; i++)
            node = Record(node, bits[i >> 3] >> (7 - (i & 7)) & 1);
        if (node <= nodeCount) return null;  // equal: the tree says "no data here"
        var data = treeBytes + (node - nodeCount);  // a record's value counts from the tree's end plus 16, and the data section starts 16 bytes after the tree
        if (Decode(data, 0, out _) is not Dictionary<string, object?> record || record.GetValueOrDefault("location") is not Dictionary<string, object?> where) return null;
        return where.GetValueOrDefault("latitude") is double lat && where.GetValueOrDefault("longitude") is double lon ? (lat, lon) : null;
    }

    /// <summary>The value at a file offset of the data section: text, a whole number as long, a float or double as double, a map, a list, a bool or bytes; `next` is where the following value starts.</summary>
    object? Decode(long offset, int depth, out long next)
    {
        if (depth > Depth) throw new InvalidDataException("MMDB: data nested too deep");
        var ctrl = Read(offset++, 1)[0];
        var type = ctrl >> 5;
        if (type == 1)  // a pointer, relative to the data section (16 bytes after the tree)
        {
            var width = (ctrl >> 3 & 3) + 1;
            var raw = Read(offset, width);
            offset += width;
            var target = (long)BigEndian(raw) + (width == 4 ? 0 : (long)(ctrl & 7) << (8 * width)) + (width switch { 2 => 2048, 3 => 526336, _ => 0 });
            next = offset;
            return Decode(dataBase + target, depth + 1, out _);
        }
        if (type == 0) type = 7 + Read(offset++, 1)[0];
        long size = ctrl & 31;
        if (size >= 29)
        {
            var extra = (int)size - 28;
            size = (long)BigEndian(Read(offset, extra)) + (size == 29 ? 29 : size == 30 ? 285 : 65821);
            offset += extra;
        }
        if (type is 7)
        {
            var map = new Dictionary<string, object?>();
            for (var i = 0; i < size; i++)
            {
                var key = Decode(offset, depth + 1, out offset) as string ?? throw new InvalidDataException("MMDB: a map key is not text");
                map[key] = Decode(offset, depth + 1, out offset);
            }
            next = offset;
            return map;
        }
        if (type is 11)
        {
            var list = new List<object?>();
            for (var i = 0; i < size; i++) list.Add(Decode(offset, depth + 1, out offset));
            next = offset;
            return list;
        }
        if (type is 14) { next = offset; return size != 0; }  // a bool's value is its size
        if (size > MaxValue) throw new InvalidDataException("MMDB: a value is too long");
        var body = Read(offset, (int)size);
        next = offset + size;
        return type switch
        {
            2 => Encoding.UTF8.GetString(body),
            3 when size == 8 => BitConverter.ToDouble([.. body.Reverse()]),
            15 when size == 4 => (double)BitConverter.ToSingle([.. body.Reverse()]),
            5 or 6 or 9 when size <= 8 => (long)BigEndian(body),
            8 when size <= 4 => size == 4 ? (long)(int)BigEndian(body) : (long)BigEndian(body),  // an int32 stored short is a non-negative number
            _ => body,  // bytes, uint128, a cache container and what this reader does not use
        };
    }

    static ulong BigEndian(byte[] bytes)
    {
        ulong n = 0;
        foreach (var b in bytes) n = n << 8 | b;
        return n;
    }
}
