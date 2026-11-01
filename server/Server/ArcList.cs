// Archive tables (pcN.tab + pcN.arc in the game's archives folder). A .tab is a 4-byte header followed by 16-byte
// little-endian entries: name hash, offset into the .arc, size, flags. The .arc holds the stored bytes; an entry that
// starts with the zlib header 78 01 is deflated. Files are keyed by the lookup3 hash of their lowercase name.
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Resurgence.Server;

public static class ArcList
{
    public record Rec(string Tab, uint Hash, uint Offset, uint Size, uint Flags);

    /// <summary>Bob Jenkins lookup3 hashlittle.</summary>
    public static uint HashLittle(ReadOnlySpan<byte> data, uint initval = 0)
    {
        unchecked
        {
            var n = data.Length;
            uint a, b, c;
            a = b = c = 0xDEAD_BEEF + (uint)n + initval;
            var i = 0;
            while (n > 12)
            {
                a += BitConverter.ToUInt32(data[i..]);
                b += BitConverter.ToUInt32(data[(i + 4)..]);
                c += BitConverter.ToUInt32(data[(i + 8)..]);
                a -= c; a ^= BitOperations.RotateLeft(c, 4); c += b;
                b -= a; b ^= BitOperations.RotateLeft(a, 6); a += c;
                c -= b; c ^= BitOperations.RotateLeft(b, 8); b += a;
                a -= c; a ^= BitOperations.RotateLeft(c, 16); c += b;
                b -= a; b ^= BitOperations.RotateLeft(a, 19); a += c;
                c -= b; c ^= BitOperations.RotateLeft(b, 4); b += a;
                i += 12;
                n -= 12;
            }
            if (n == 0) return c;
            Span<byte> tail = stackalloc byte[12];
            data[i..].CopyTo(tail);
            a += BitConverter.ToUInt32(tail);
            b += BitConverter.ToUInt32(tail[4..]);
            c += BitConverter.ToUInt32(tail[8..]);
            c ^= b; c -= BitOperations.RotateLeft(b, 14);
            a ^= c; a -= BitOperations.RotateLeft(c, 11);
            b ^= a; b -= BitOperations.RotateLeft(a, 25);
            c ^= b; c -= BitOperations.RotateLeft(b, 16);
            a ^= c; a -= BitOperations.RotateLeft(c, 4);
            b ^= a; b -= BitOperations.RotateLeft(a, 14);
            c ^= b; c -= BitOperations.RotateLeft(b, 24);
            return c;
        }
    }

    /// <summary>Key of a file name: lookup3 hash of the lowercase basename.</summary>
    public static uint NameHash(string name)
    {
        var p = name.Replace('\\', '/');
        return HashLittle(Encoding.Latin1.GetBytes(p[(p.LastIndexOf('/') + 1)..].ToLowerInvariant()));
    }

    /// <summary>hash → record over several tables; a later table wins.</summary>
    public static Dictionary<uint, Rec> Index(IEnumerable<string> tabs)
    {
        var idx = new Dictionary<uint, Rec>();
        foreach (var tab in tabs)
        {
            var raw = File.ReadAllBytes(tab);
            for (var k = 0; k < (raw.Length - 4) / 16; k++)
            {
                var o = 4 + 16 * k;
                var h = BitConverter.ToUInt32(raw, o);
                idx[h] = new Rec(tab, h, BitConverter.ToUInt32(raw, o + 4), BitConverter.ToUInt32(raw, o + 8), BitConverter.ToUInt32(raw, o + 12));
            }
        }
        return idx;
    }

    /// <summary>Lookup by basename hash; an entry with flag 1 is keyed by the hash of the lowercase relative path (backslashes) instead.</summary>
    public static Rec? Find(string path, Dictionary<uint, Rec> idx)
    {
        var hit = idx.GetValueOrDefault(NameHash(path));
        if (hit is { Flags: 1 })
            hit = idx.GetValueOrDefault(HashLittle(Encoding.Latin1.GetBytes(path.Replace('/', '\\').ToLowerInvariant())));
        return hit;
    }

    /// <summary>Bytes of one entry from the sibling .arc; zlib-stored (78 01) entries are inflated.</summary>
    public static byte[] ReadEntry(Rec r)
    {
        var b = new byte[r.Size];
        using (var arc = File.OpenRead(Path.ChangeExtension(r.Tab, ".arc")))
        {
            arc.Seek(r.Offset, SeekOrigin.Begin);
            b = b[..arc.ReadAtLeast(b, b.Length, throwOnEndOfStream: false)];
        }
        if (b is not [0x78, 0x01, ..]) return b;
        using var z = new ZLibStream(new MemoryStream(b), CompressionMode.Decompress);
        using var out_ = new MemoryStream();
        z.CopyTo(out_);
        return out_.ToArray();
    }

    static readonly ConcurrentDictionary<string, Dictionary<uint, Rec>> Cache = new();

    /// <summary>Index of install_dir/game/archives/*.tab, in lowercase path order (pc0, pc1, pc10, pc2…).</summary>
    public static Dictionary<uint, Rec> ArchiveIndex(string installDir) => Cache.GetOrAdd(installDir, d =>
    {
        var dir = Path.Combine(d, "game", "archives");
        return Index(Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.tab").OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal)
            : []);
    });
}
