using System.Net;
using System.Text;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

/// <summary>The distance dot's pieces without a server: the reader of the IP database file (on a file built here), the places (private addresses, the cache), the kilometres and the three words.
/// The list itself is in <see cref="MultiplayerTests"/>.</summary>
[Collection("ProcessState")]  // one test captures the console
public class HostDistanceTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("distance").FullName;

    public void Dispose() => Fixtures.DeleteDir(tmp);

    static readonly (double Lat, double Lon) Paris = (48.8566, 2.3522), London = (51.5074, -0.1278), NewYork = (40.7128, -74.0060);

    static readonly MmdbFile.Net[] Nets =
    [
        new("203.0.113.0/24", Paris.Lat, Paris.Lon, "Paris"),
        new("198.51.100.0/25", London.Lat, London.Lon),
        new("2001:db8::/32", NewYork.Lat, NewYork.Lon),
        new("192.0.2.0/24", 0, 0),
    ];

    [Theory]
    [InlineData(24, 0)]
    [InlineData(28, 0)]
    [InlineData(32, 0)]
    [InlineData(24, 3000)]  // the keys' offsets pass 2,048: the pointers have the second size
    [InlineData(28, 600000)]  // ... and the third
    public void TestTheReaderFindsTheNetworkOfAnAddressWhateverTheRecordSizeAndPointerSize(int recordBits, int padding)
    {
        using var db = new Mmdb(MmdbFile.Write(tmp, Nets, recordBits, padding));
        Assert.Equal(Paris, db.Find(IPAddress.Parse("203.0.113.77")));
        Assert.Equal(Paris, db.Find(IPAddress.Parse("::ffff:203.0.113.77")));  // an IPv4-mapped address is the IPv4 one
        Assert.Equal(London, db.Find(IPAddress.Parse("198.51.100.127")));
        Assert.Null(db.Find(IPAddress.Parse("198.51.100.128")));  // the other half of the /24: no network
        Assert.Equal(NewYork, db.Find(IPAddress.Parse("2001:db8:1::5")));
        Assert.Null(db.Find(IPAddress.Parse("2001:db9::1")));
        Assert.Null(db.Find(IPAddress.Parse("8.8.8.8")));
        Assert.Equal((0, 0), db.Find(IPAddress.Parse("192.0.2.1")));  // a place on the equator and the meridian is a place
    }

    [Fact]
    public void TestAPlaceStoredAsFloatsIsRead()
    {
        using var db = new Mmdb(MmdbFile.Write(tmp, [new("203.0.113.0/24", 48.5, 2.25, Floats: true)]));
        Assert.Equal((48.5, 2.25), db.Find(IPAddress.Parse("203.0.113.1")));
    }

    [Fact]
    public void TestAFileThatIsNoDatabaseIsRefused()
    {
        var junk = Path.Combine(tmp, "junk.mmdb");
        File.WriteAllBytes(junk, new byte[4000]);
        Assert.Throws<InvalidDataException>(() => new Mmdb(junk));
        File.WriteAllBytes(junk, []);
        Assert.Throws<InvalidDataException>(() => new Mmdb(junk));
    }

    [Fact]
    public void TestOpeningSaysWhyThereAreNoDots()
    {
        Assert.Equal((null, "distance dots: off (no geoip_file)"), Places.Open(null));
        var missing = Path.Combine(tmp, "none.mmdb");
        Assert.Equal((null, $"distance dots: off ({missing} not found)"), Places.Open(missing));
        var junk = Path.Combine(tmp, "junk.mmdb");
        File.WriteAllBytes(junk, new byte[100]);
        var (places, message) = Places.Open(junk);
        Assert.Null(places);
        Assert.StartsWith($"distance dots: off ({junk}: ", message);
        var (open, ok) = Places.Open(MmdbFile.Write(tmp, Nets));
        Assert.NotNull(open);
        Assert.Equal(Paris, open.Locate("203.0.113.5"));
        Assert.StartsWith("distance dots: from ", ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.7.7")]
    [InlineData("100.64.0.1")]  // shared (CGNAT)
    [InlineData("100.127.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.1.2.3")]
    public void TestAPrivateOrUnroutableAddressHasNoPlaceEvenWhenTheDatabaseGivesOne(string? address)
    {
        var places = new Places(_ => Paris);  // a database that has a place for anything
        Assert.Null(places.Locate(address));
        Assert.Equal(Paris, places.Locate("203.0.113.5"));
        Assert.Equal(Paris, places.Locate("100.63.255.255"));  // just outside 100.64.0.0/10
        Assert.Equal(Paris, places.Locate("172.32.0.1"));
    }

    [Fact]
    public void TestAnAddressIsLookedUpOnceAndTheMemoryIsBounded()
    {
        var calls = 0;
        var places = new Places(_ => { calls++; return null; });
        for (var i = 0; i < 5; i++) places.Locate("203.0.113.5");
        Assert.Equal(1, calls);  // a miss is remembered too
        for (var i = 0; i < Places.Cap + 10; i++) places.Locate($"203.{i >> 8 & 255}.{i & 255}.1");
        Assert.True(calls <= Places.Cap + 12);
    }

    [Fact]
    public void TestTheDistanceOfTwoKnownCityPairs()
    {
        Assert.InRange(HostDistance.Km(Paris, London), 340, 348);  // about 344 km
        Assert.InRange(HostDistance.Km(Paris, NewYork), 5830, 5845);  // about 5,837 km
        Assert.Equal(HostDistance.Km(London, NewYork), HostDistance.Km(NewYork, London), 6);
        Assert.Equal(0, HostDistance.Km(Paris, Paris));
        Assert.InRange(HostDistance.Km((0, 0), (0, 180)), 20015, 20016);  // half the circumference of the 6,371 km sphere
    }

    /// <summary>1 degree of latitude on the 6,371 km sphere.</summary>
    const double DegreeKm = 6371 * Math.PI / 180;

    /// <summary>The place that is <paramref name="km"/> north of (0, 0) along the meridian.</summary>
    static (double Lat, double Lon) North(double km) => (km / DegreeKm, 0);

    [Theory]
    [InlineData(0, "short")]
    [InlineData(2999, "short")]
    [InlineData(2999.9, "short")]
    [InlineData(3000.001, "medium")]  // 3,000 km itself is medium: the nudge keeps the floating point's last digits out of the test
    [InlineData(3001, "medium")]
    [InlineData(6999, "medium")]
    [InlineData(6999.9, "medium")]
    [InlineData(7000.001, "long")]
    [InlineData(7001, "long")]
    [InlineData(19000, "long")]
    public void TestTheBucketsAtTheBounds(double km, string bucket) =>
        Assert.Equal(bucket, HostDistance.Bucket((0, 0), North(km)));

    [Fact]
    public void TestTheBoundsAreThreeAndSevenThousandKilometres()
    {
        Assert.Equal((3000, 7000), (HostDistance.Medium, HostDistance.Long));
        Assert.Equal(("short", "medium"), (HostDistance.Bucket((0, 0), (26.97, 0)), HostDistance.Bucket((0, 0), (26.98, 0))));  // 2,998.9 and 3,000.0 km on one meridian
        Assert.Equal(("medium", "long"), (HostDistance.Bucket((0, 0), (62.95, 0)), HostDistance.Bucket((0, 0), (62.96, 0))));  // 6,999.8 and 7,000.9 km
    }

    [Fact]
    public void TestAnUnknownPlaceOnEitherSideIsNoBucket()
    {
        Assert.Null(HostDistance.Bucket(null, Paris));
        Assert.Null(HostDistance.Bucket(Paris, null));
        Assert.Null(HostDistance.Bucket(null, null));
    }

    [Fact]
    public void TestLookingUpPlacesPrintsNothingAndOnlyTheWordRemains()
    {
        var (old, output) = (Console.Out, new StringWriter());
        Console.SetOut(output);
        try
        {
            var places = new Places(_ => NewYork);
            var word = HostDistance.Bucket(places.Locate("203.0.113.5"), places.Locate("198.51.100.7"));
            Assert.Equal("short", word);
            new RequestLog(console: true, null).Write(new Req("GET", "web.test", "/catalog/games", "", _ => null, [], false, "203.0.113.5"), new Reply(200, [], Encoding.ASCII.GetBytes($"[{{\"distance\": \"{word}\"}}]"), "catalog"));
            var text = output.ToString();
            Assert.Equal("catalog 200 GET web.test/catalog/games", text.Trim());  // the request log's line, with no address, place or distance
        }
        finally
        {
            Console.SetOut(old);
        }
    }
}
