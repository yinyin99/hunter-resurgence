using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Resurgence.Web;

/// <summary>The reverse proxy in front of the app (FORWARDED_TRUSTED): only its addresses may set the client address and the scheme.</summary>
public static class ForwardedProxy
{
    /// <summary>Trusts X-Forwarded-For and X-Forwarded-Proto from the comma-separated addresses and networks (a.b.c.d or a.b.c.d/nn) only; the default loopback entries are removed.
    /// Nothing trusted forwards nothing (empty lists would trust every sender).</summary>
    public static void Configure(ForwardedHeadersOptions o, string? trusted)
    {
        o.ForwardedHeaders = string.IsNullOrWhiteSpace(trusted) ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var entry in (trusted ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/')) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
            else o.KnownProxies.Add(IPAddress.Parse(entry));
        }
    }
}
