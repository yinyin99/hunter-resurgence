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
    public void TestTheFourBytePointerFormIsReadAndItsUnusedControlBitsAreIgnored()
    {
        using var db = new Mmdb(MmdbFile.Write(tmp, Nets, wide: true));
        Assert.Equal(Paris, db.Find(IPAddress.Parse("203.0.113.77")));
        Assert.Equal(NewYork, db.Find(IPAddress.Parse("2001:db8:1::5")));
    }

    [Fact]
    public void TestAnIpv4OnlyFileGivesNoPlaceToAnIpv6AddressWhoseFirstBitsAreAnIpv4Network()
    {
        using var db = new Mmdb(MmdbFile.Write(tmp, [new("203.0.113.0/24", Paris.Lat, Paris.Lon)], ipv4Only: true));
        Assert.Equal(Paris, db.Find(IPAddress.Parse("203.0.113.77")));
        Assert.Equal(Paris, db.Find(IPAddress.Parse("::ffff:203.0.113.77")));
        Assert.Null(db.Find(IPAddress.Parse("cb00:7100::1")));  // 203.0.113.0 in its first 32 bits
    }

    [Fact]
    public void TestTheBuildDateComesFromTheMetadata()
    {
        using var db = new Mmdb(MmdbFile.Write(tmp, Nets));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), db.Built);
    }

    // records that use their top bits: a reader that swaps the halves of a 28 bit pair, or drops the 32 bit record's high byte, reads another node
    [Fact]
    public void TestTheRecordsOfANodeUseTheirTopBitsAtEveryRecordSize()
    {
        byte[] n24 = [255, 1, 2, 128, 4, 5];
        Assert.Equal((255u << 16) | (1u << 8) | 2, Mmdb.Record(n24, 24, 0));
        Assert.Equal((128u << 16) | (4u << 8) | 5, Mmdb.Record(n24, 24, 1));
        byte[] n28 = [17, 34, 51, 165, 68, 85, 102];  // the middle byte 165: its high nibble 10 is the left record's top bits, its low nibble 5 the right's
        Assert.Equal((10u << 24) | (17u << 16) | (34u << 8) | 51, Mmdb.Record(n28, 28, 0));
        Assert.Equal((5u << 24) | (68u << 16) | (85u << 8) | 102, Mmdb.Record(n28, 28, 1));
        byte[] n32 = [200, 1, 2, 3, 129, 4, 5, 6];
        Assert.Equal((200u << 24) | (1u << 16) | (2u << 8) | 3, Mmdb.Record(n32, 32, 0));
        Assert.Equal((129u << 24) | (4u << 16) | (5u << 8) | 6, Mmdb.Record(n32, 32, 1));
    }

    [Fact]
    public void TestAPointerLeadsWhereEachOfItsFourFormsSays()
    {
        Assert.Equal((5L << 8) | 9, Mmdb.PointerTarget(32 | 5, [9]));  // 1 byte: 3 top bits from the control byte
        Assert.Equal((3L << 16) + 258 + 2048, Mmdb.PointerTarget(32 | 8 | 3, [1, 2]));
        Assert.Equal((2L << 24) + 66051 + 526336, Mmdb.PointerTarget(32 | 16 | 2, [1, 2, 3]));
        Assert.Equal(16909060L, Mmdb.PointerTarget(32 | 24 | 7, [1, 2, 3, 4]));  // 4 bytes: nothing from the control byte, nothing added
    }

    [Fact]
    public void TestARecordThatDecodesToTooManyValuesIsRefusedAndASmallOneIsNot()
    {
        using var db = new Mmdb(MmdbFile.Write(tmp, [new("203.0.113.0/24", Paris.Lat, Paris.Lon, Fields: 300), new("198.51.100.0/24", London.Lat, London.Lon, Fields: 5000)]));
        Assert.Equal(Paris, db.Find(IPAddress.Parse("203.0.113.1")));
        Assert.Contains("too many values", Assert.Throws<InvalidDataException>(() => db.Find(IPAddress.Parse("198.51.100.1"))).Message);
    }

    [Fact]
    public void TestARecordWithHugeStringsIsRefusedBeforeItsBytesAreReadAndQuickly()
    {
        // 3 values only, far under the value budget, but 10 million bytes of text
        using var db = new Mmdb(MmdbFile.Write(tmp, [new("203.0.113.0/24", Paris.Lat, Paris.Lon, Huge: 10_000_000), new("198.51.100.0/24", London.Lat, London.Lon, Huge: 66_000), new("192.0.2.0/24", 0, 0, Huge: 60_000)]));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("too long", Assert.Throws<InvalidDataException>(() => db.Find(IPAddress.Parse("203.0.113.1"))).Message);
        Assert.Contains("too long", Assert.Throws<InvalidDataException>(() => db.Find(IPAddress.Parse("198.51.100.1"))).Message);  // just over 64 KiB of text
        Assert.Equal((0, 0), db.Find(IPAddress.Parse("192.0.2.1")));  // 60 KB of text is within the budget
        Assert.True(clock.ElapsedMilliseconds < 1000);
        var lines = new List<string>();
        var places = new Places(db.Find, log: lines.Add);
        Assert.Null(places.Locate("203.0.113.1"));
        Assert.Single(lines);  // the feature is off
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
        Assert.Equal("distance dots: from test.mmdb (built 1970-01-01)", ok);  // the file's name and build date, no folder
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
    public void TestAnAddressIsLookedUpOnce()
    {
        var calls = 0;
        var places = new Places(_ => { calls++; return null; });
        for (var i = 0; i < 5; i++) places.Locate("203.0.113.5");
        Assert.Equal(1, calls);  // a miss is remembered too
    }

    [Fact]
    public void TestTheMemoryIsBoundedByCount()
    {
        var calls = 0;
        var places = new Places(_ => { calls++; return Paris; });
        string Address(int i) => $"203.0.{i >> 8 & 255}.{i & 255}";
        for (var i = 0; i < Places.Cap; i++) places.Locate(Address(i));
        places.Locate(Address(0));
        Assert.Equal(Places.Cap, calls);  // the cache is full, not over
        places.Locate(Address(Places.Cap));  // one more: the memory is emptied first
        Assert.Equal(Places.Cap + 1, calls);
        places.Locate(Address(0));
        Assert.Equal(Places.Cap + 2, calls);  // the first address is forgotten and looked up again
    }

    [Fact]
    public void TestTheMemoryIsForgottenByTheSweepAloneOnAnIdleServer()
    {
        double now = 5000;
        var places = new Places(_ => Paris, () => now);
        places.Locate("203.0.113.5");
        places.Locate("203.0.113.6");
        Assert.Equal(2, places.Remembered);
        now += Places.Lifetime - 1;
        places.Expire();
        Assert.Equal(2, places.Remembered);
        now += 1;
        places.Expire();  // no lookup in between: only the sweep runs
        Assert.Equal(0, places.Remembered);
    }

    [Fact]
    public void TestTheMemoryIsEmptiedAtOnceWhenTheFeatureTurnsOff()
    {
        var broken = false;
        var lines = new List<string>();
        var places = new Places(_ => broken ? throw new InvalidDataException("damaged") : Paris, log: lines.Add);
        places.Locate("203.0.113.5");
        places.Locate("203.0.113.6");
        Assert.Equal(2, places.Remembered);
        broken = true;
        places.Locate("203.0.113.7");
        Assert.Equal(0, places.Remembered);
        Assert.Null(places.Locate("203.0.113.5"));  // what was remembered is not served either
        Assert.Equal(0, places.Remembered);
    }

    [Fact]
    public void TestTheOffLineIsPrintedOnceHoweverManyLookupsFail()
    {
        var lines = new List<string>();
        var places = new Places(_ => throw new InvalidDataException("damaged"), log: lines.Add);
        Parallel.For(0, 200, i => places.Locate($"203.0.{i >> 8}.{i & 255}"));
        for (var i = 0; i < 50; i++) places.Locate($"198.51.100.{i}");
        Assert.Equal(["distance dots: off (damaged)"], lines);
    }

    [Fact]
    public void TestTheMemoryIsForgottenAnHourAfterItWasLastEmptied()
    {
        double now = 5000;
        var calls = 0;
        var places = new Places(_ => { calls++; return Paris; }, () => now);
        places.Locate("203.0.113.5");
        places.Locate("203.0.113.6");
        now += Places.Lifetime - 1;
        places.Locate("203.0.113.5");
        Assert.Equal(2, calls);  // 59 min 59 s: remembered
        now += 1;
        places.Locate("203.0.113.5");
        Assert.Equal(3, calls);  // an hour: forgotten, looked up again
        places.Locate("203.0.113.6");
        Assert.Equal(4, calls);  // all of it, not only the address asked for
        now += Places.Lifetime - 1;
        places.Locate("203.0.113.5");
        Assert.Equal(4, calls);  // the hour counts from the last time it was emptied
        Assert.True(Places.Lifetime + Places.SweepSeconds <= 3600);  // the sweep runs every SweepSeconds: no place is older than an hour
    }

    [Fact]
    public void TestADamagedFileOpensThenTheFirstFailingLookupTurnsTheFeatureOffWithOneLineAndNoRetry()
    {
        var path = MmdbFile.Write(tmp, Nets, corruptData: true);  // the end (metadata) and the tree are intact: the start accepts it
        var (opened, message) = Places.Open(path);
        Assert.NotNull(opened);
        Assert.StartsWith("distance dots: from ", message);
        var calls = 0;
        var db = new Mmdb(path);
        var lines = new List<string>();
        var places = new Places(ip => { calls++; return db.Find(ip); }, log: lines.Add);
        Assert.Null(places.Locate("203.0.113.5"));
        var line = Assert.Single(lines);
        Assert.StartsWith("distance dots: off (MMDB: ", line);
        Assert.DoesNotContain("203.0.113", line);
        Assert.Null(places.Locate("198.51.100.7"));
        Assert.Null(places.Locate("203.0.113.5"));
        Assert.Equal((1, 1), (calls, lines.Count));  // off for the process's life: the file is not read again, nothing more is said
    }

    [Fact]
    public void TestAnyFailureOfTheLookupTurnsItOffAndNamesNoAddress()
    {
        var lines = new List<string>();
        var places = new Places(_ => throw new IOException("read of /srv/geoip.mmdb failed at 203.0.113.5"), log: lines.Add);
        Assert.Null(places.Locate("203.0.113.5"));
        Assert.Equal(["distance dots: off (IOException)"], lines);  // the type, not a message that could hold a path or an address
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

    [Theory]
    [InlineData(2999.999, "short")]
    [InlineData(3000, "medium")]
    [InlineData(6999.999, "medium")]
    [InlineData(7000, "long")]
    public void TestTheWordOfADistanceAtTheExactBounds(double km, string word) => Assert.Equal(word, HostDistance.Word(km));

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
