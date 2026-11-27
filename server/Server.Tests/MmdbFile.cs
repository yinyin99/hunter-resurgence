using System.Net;
using System.Text;

namespace Resurgence.Server.Tests;

/// <summary>Builds a tiny MMDB file (format 2, an IPv6 tree with the IPv4 addresses under the 96 zero bits, as the IP databases do) from invented networks: each has a place and a city name.
/// Repeated map keys are written as pointers, as the real writers do; <c>padding</c> bytes of text before the first record push the keys' offsets past the first pointer size.</summary>
public static class MmdbFile
{
    public sealed record Net(string Cidr, double Lat, double Lon, string City = "Test City", bool Floats = false, int Fields = 0, int Huge = 0);

    // set by Write for the maps and pointers it builds (tests build one file at a time per thread)
    [ThreadStatic] static bool Wide, Ipv4Only;

    sealed class Node { public object?[] Child = [null, null]; }  // a Node, or an int: the data offset of a leaf

    /// <param name="wide">Every pointer in the 4 byte form, whatever offset it leads to.</param>
    /// <param name="ipv4Only">An IPv4 tree (ip_version 4) with IPv4 networks only.</param>
    /// <param name="corruptData">The data section filled with bytes that are no value: the metadata and the tree stay intact, so the file opens and its lookups fail.</param>
    public static string Write(string folder, IEnumerable<Net> nets, int recordBits = 24, int padding = 0, string name = "test.mmdb", bool wide = false, bool ipv4Only = false, bool corruptData = false)
    {
        Wide = wide;
        Ipv4Only = ipv4Only;
        var data = new MemoryStream();
        var keys = new Dictionary<string, int>();
        var root = new Node();
        if (padding > 0) Text(new string('x', padding))(data);  // a value no address leads to: later records sit that far into the data
        foreach (var net in nets)
        {
            var offset = (int)data.Position;
            Record(data, keys, net);
            var (address, length) = Parse(net.Cidr);
            Node at = root;
            for (var i = 0; i < length; i++)
            {
                var bit = address[i >> 3] >> (7 - (i & 7)) & 1;
                if (i == length - 1) at.Child[bit] = offset;
                else
                {
                    if (at.Child[bit] is not Node next) { next = new Node(); at.Child[bit] = next; }
                    at = next;
                }
            }
        }
        // number the nodes in a walk from the root; a leaf is the node count plus 16 plus its data offset, an empty side the node count
        var order = new List<Node>();
        void Walk(Node n) { order.Add(n); foreach (var c in n.Child) if (c is Node child) Walk(child); }
        Walk(root);
        var number = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        uint Value(object? c) => c switch { Node n => (uint)number[n], int leaf => (uint)(order.Count + 16 + leaf), _ => (uint)order.Count };
        var tree = new MemoryStream();
        foreach (var n in order)
        {
            uint l = Value(n.Child[0]), r = Value(n.Child[1]);
            switch (recordBits)
            {
                case 24: tree.Write([(byte)(l >> 16), (byte)(l >> 8), (byte)l, (byte)(r >> 16), (byte)(r >> 8), (byte)r]); break;
                case 28: tree.Write([(byte)(l >> 16), (byte)(l >> 8), (byte)l, (byte)((l >> 24) << 4 | r >> 24), (byte)(r >> 16), (byte)(r >> 8), (byte)r]); break;
                default: tree.Write([(byte)(l >> 24), (byte)(l >> 16), (byte)(l >> 8), (byte)l, (byte)(r >> 24), (byte)(r >> 16), (byte)(r >> 8), (byte)r]); break;
            }
        }
        var meta = new MemoryStream();
        Map(meta, null, ("binary_format_major_version", UInt(5, 2)), ("binary_format_minor_version", UInt(5, 0)), ("node_count", UInt(6, (uint)order.Count)),
            ("record_size", UInt(5, (uint)recordBits)), ("ip_version", UInt(5, ipv4Only ? 4u : 6u)), ("database_type", Text("Test-City")), ("build_epoch", UInt(9, 1)));
        var file = new MemoryStream();
        file.Write(tree.ToArray());
        file.Write(new byte[16]);
        file.Write(corruptData ? Enumerable.Repeat((byte)255, (int)data.Length).ToArray() : data.ToArray());
        file.Write([171, 205, 239, .. "MaxMind.com"u8]);
        file.Write(meta.ToArray());
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    static (byte[] Address, int Length) Parse(string cidr)
    {
        var (text, prefix) = (cidr[..cidr.IndexOf('/')], int.Parse(cidr[(cidr.IndexOf('/') + 1)..]));
        var ip = IPAddress.Parse(text);
        if (Ipv4Only) return ([.. ip.GetAddressBytes()], prefix);
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? ([.. new byte[12], .. ip.GetAddressBytes()], 96 + prefix) : (ip.GetAddressBytes(), prefix);
    }

    static void Record(MemoryStream s, Dictionary<string, int> keys, Net net)
    {
        var fields = new List<(string, Action<MemoryStream>)>();
        if (net.Huge > 0) fields.Add(("note", Text(new string('x', net.Huge))));
        for (var i = 0; i < net.Fields; i++) fields.Add(("f", Text("x")));  // the same key again: a pointer each
        fields.Add(("city", m => Map(m, keys, ("names", n => Map(n, keys, ("en", Text(net.City)))))));
        fields.Add(("location", m => Map(m, keys, net.Floats
            ? [("latitude", Float((float)net.Lat)), ("longitude", Float((float)net.Lon))]
            : [("latitude", Double(net.Lat)), ("longitude", Double(net.Lon))])));
        Map(s, keys, [.. fields]);
    }

    static void Control(MemoryStream s, int type, int size)
    {
        var (b, extra) = size < 29 ? (size, Array.Empty<int>()) : size < 285 ? (29, new[] { size - 29 }) : size < 65821 ? (30, new[] { size - 285 >> 8, size - 285 & 255 }) : (31, new[] { size - 65821 >> 16, size - 65821 >> 8 & 255, size - 65821 & 255 });
        if (type < 8) s.WriteByte((byte)(type << 5 | b));
        else { s.WriteByte((byte)b); s.WriteByte((byte)(type - 7)); }
        foreach (var e in extra) s.WriteByte((byte)e);
    }

    static Action<MemoryStream> Text(string text) => s => { var b = Encoding.UTF8.GetBytes(text); Control(s, 2, b.Length); s.Write(b); };
    static Action<MemoryStream> Double(double d) => s => { Control(s, 3, 8); s.Write(BitConverter.GetBytes(d).Reverse().ToArray()); };
    static Action<MemoryStream> Float(float f) => s => { Control(s, 15, 4); s.Write(BitConverter.GetBytes(f).Reverse().ToArray()); };
    static Action<MemoryStream> UInt(int type, ulong n) => s => { var b = BitConverter.GetBytes(n).Reverse().SkipWhile(x => x == 0).ToArray(); Control(s, type, b.Length); s.Write(b); };

    /// <summary>A map; a key already written is a pointer to it (keys is null in the metadata, which has none).</summary>
    static void Map(MemoryStream s, Dictionary<string, int>? keys, params (string Key, Action<MemoryStream> Value)[] fields)
    {
        Control(s, 7, fields.Length);
        foreach (var (key, value) in fields)
        {
            if (keys is not null && keys.TryGetValue(key, out var at)) Pointer(s, at);
            else
            {
                keys?.Add(key, (int)s.Position);
                Text(key)(s);
            }
            value(s);
        }
    }

    /// <summary>A pointer to a data offset, of the smallest size that holds it.</summary>
    static void Pointer(MemoryStream s, int at)
    {
        if (Wide) s.Write([63, (byte)(at >> 24),  // the 3 low bits of the control byte are unused in this form: set them, a reader must ignore them
             (byte)(at >> 16), (byte)(at >> 8), (byte)at]);
        else if (at < 2048) s.Write([(byte)(32 | at >> 8), (byte)at]);
        else if (at < 2048 + 524288) { var v = at - 2048; s.Write([(byte)(40 | v >> 16), (byte)(v >> 8), (byte)v]); }
        else { var v = at - 526336; s.Write([(byte)(48 | v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]); }
    }
}
