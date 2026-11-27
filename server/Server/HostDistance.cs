// How far a host is from the player who looks at the list (docs/server.md#how-far-a-host-is): the approximate place of two addresses from an offline IP database, the great-circle
// distance between them, and only one of three words leaves the server. No address, country, coordinate or place is written to disk or a log or sent; the places are in memory for at most an hour.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Resurgence.Server;

/// <summary>The approximate place of an address: the database's (<paramref name="find"/>), for a public address only, remembered in memory only: at most <see cref="Cap"/> addresses, the whole memory forgotten once an hour
/// (<see cref="Lifetime"/>, by <paramref name="clock"/>, Unix seconds) and at a restart. A lookup that fails (a damaged file) turns the feature off for the rest of the process's life with one line to <paramref name="log"/>; the list never fails because of it.</summary>
/// <param name="find">The place the database gives an address, null for none.</param>
public sealed class Places(Func<IPAddress, (double Lat, double Lon)?> find, Func<double>? clock = null, Action<string>? log = null)
{
    public const int Cap = 4096;
    /// <summary>Seconds the remembered places live: the whole memory is emptied at the next lookup after this long.</summary>
    public const double Lifetime = 3600;
    readonly Func<double> clock = clock ?? Py.Now;
    readonly Action<string> log = log ?? Console.WriteLine;
    readonly ConcurrentDictionary<string, (double Lat, double Lon)?> known = [];
    double cleared = (clock ?? Py.Now)();
    int off;

    /// <summary>The place of the address text; null for no address, one that does not parse, a private, loopback, link-local or shared (CGNAT) one, one the database does not have, and every address once a lookup has failed.</summary>
    public (double Lat, double Lon)? Locate(string? address)
    {
        if (address is null || off != 0) return null;
        var now = clock();
        if (now - cleared >= Lifetime)
        {
            known.Clear();
            cleared = now;
        }
        if (known.TryGetValue(address, out var seen)) return seen;
        (double Lat, double Lon)? place = null;
        try
        {
            place = IPAddress.TryParse(address, out var ip) && Public(ip) ? find(ip) : null;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref off, 1) == 0) log($"distance dots: off ({(e is InvalidDataException ? e.Message : e.GetType().Name)})");  // never the address
            known.Clear();
            return null;
        }
        if (known.Count >= Cap) known.Clear();
        known[address] = place;
        return place;
    }

    static bool Public(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast);
        var b = ip.GetAddressBytes();
        return !(b[0] is 10 or 0 or >= 224 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    /// <summary>Opens the database file of the `geoip_file` setting for the server's life; the places, or null, and the one line the server prints at its start (on: the file's name and build date). A missing or wrong file only hides the dots.</summary>
    public static (Places? Places, string Message) Open(string? path, Action<string>? log = null, Func<double>? clock = null)
    {
        if (path is null) return (null, "distance dots: off (no geoip_file)");
        if (!File.Exists(path)) return (null, $"distance dots: off ({path} not found)");
        try
        {
            var db = new Mmdb(path);
            return (new Places(db.Find, clock, log), $"distance dots: from {Path.GetFileName(path)} (built {(db.Built is { } built ? built.ToString("yyyy-MM-dd") : "on an unknown date")})");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (null, $"distance dots: off ({path}: {e.Message})");
        }
    }
}

public static class HostDistance
{
    /// <summary>Under this many kilometres is short, from it to <see cref="Long"/> medium; the bounds are the dots' tuning knob (the sphere's rounding: 1 degree of latitude is 111.195 km).</summary>
    public const double Medium = 3000, Long = 7000;
    const double EarthRadiusKm = 6371;

    public static double Km((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var (dLat, dLon) = (Rad(b.Lat - a.Lat), Rad(b.Lon - a.Lon));
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>`short` under <see cref="Medium"/> km, `medium` from it to under <see cref="Long"/>, `long` from there.</summary>
    public static string Word(double km) => km < Medium ? "short" : km < Long ? "medium" : "long";

    /// <summary>`short`, `medium` or `long` for two places; null when either is unknown.</summary>
    public static string? Bucket((double Lat, double Lon)? a, (double Lat, double Lon)? b) =>
        a is { } from && b is { } to ? Word(Km(from, to)) : null;
}
