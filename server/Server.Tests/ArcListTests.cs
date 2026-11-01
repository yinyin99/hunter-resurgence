using System.IO.Compression;
using System.Text;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

public class ArcListTests
{
    [Fact]
    public void NameHashIsLookup3OfLowercaseBasename()
    {
        Assert.Equal(0x9ACB_711Fu, ArcList.NameHash("test_file.bin"));
        Assert.Equal(0x9ACB_711Fu, ArcList.NameHash(@"some\dir/TEST_FILE.BIN"));
        Assert.NotEqual(ArcList.NameHash("test_file.bin"), ArcList.NameHash("test_file.bak"));
    }

    [Fact]
    public void ReadEntryInflatesZlibOnly()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var raw = Encoding.ASCII.GetBytes("plain bytes");
            var inflated = Encoding.ASCII.GetBytes("deflated bytes deflated bytes");
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(inflated);
            var packed = z.ToArray();
            Assert.Equal([0x78, 0x01], packed[..2]);
            File.WriteAllBytes(Path.Combine(dir, "pc0.arc"), [.. raw, .. packed]);
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "pc0.tab"))))
            {
                w.Write(0x800u);
                w.Write(ArcList.NameHash("a.txt")); w.Write(0u); w.Write((uint)raw.Length); w.Write(0u);
                w.Write(ArcList.NameHash("b.txt")); w.Write((uint)raw.Length); w.Write((uint)packed.Length); w.Write(0u);
            }
            var idx = ArcList.Index([Path.Combine(dir, "pc0.tab")]);
            Assert.Equal(raw, ArcList.ReadEntry(ArcList.Find("x/a.txt", idx)!));
            Assert.Equal(inflated, ArcList.ReadEntry(ArcList.Find("B.TXT", idx)!));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
