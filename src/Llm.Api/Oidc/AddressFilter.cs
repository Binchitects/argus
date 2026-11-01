using System.Net;
using Llm.Api.Access;

namespace Llm.Api.Oidc;

/// <summary>
/// Which network addresses may reach Argus Arena at all (Settings → Network access): an allow list (empty: every address)
/// and a block list (refused whatever the allow list says). Traefik asks <see cref="Path"/> before every request, for every
/// host (the web, the app, the gateway, Argus), with the caller's address as it saw it; the lists are read from the saved
/// settings on each request (parsed once per value). A list is one address or range a line (10.0.0.0/8, 2001:db8::/32,
/// 192.168.1.20), commas also separate, and # starts a comment.
/// </summary>
public sealed class AddressFilter(IConfiguration config)
{
    public const string Path = "/api/authz/address";
    public const string AllowKey = "Access:AllowAddresses";
    public const string BlockKey = "Access:BlockAddresses";

    private sealed record Parsed(string? Text, IReadOnlyList<IPNetwork> Ranges);

    // Replaced whole when a list changes: a request reads one list as it was, never half of two.
    private Parsed _allow = new(null, []);
    private Parsed _block = new(null, []);

    /// <summary>A list as written: its ranges, or the first line that is not one.</summary>
    public static (IReadOnlyList<IPNetwork> Ranges, string? Wrong) Parse(string? text)
    {
        var ranges = new List<IPNetwork>();
        foreach (var raw in (text ?? "").Split(['\n', ','], StringSplitOptions.TrimEntries))
        {
            var line = (raw.IndexOf('#') is var hash and >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (IPNetwork.TryParse(line, out var range))
            {
                ranges.Add(range);
            }
            else if (IPAddress.TryParse(line, out var one))
            {
                ranges.Add(new IPNetwork(one, one.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                return (ranges, line);
            }
        }
        return (ranges, null);
    }

    private static IReadOnlyList<IPNetwork> Cached(ref Parsed cache, string? text)
    {
        var now = Volatile.Read(ref cache);
        if (now.Text == text)
        {
            return now.Ranges;
        }
        var parsed = new Parsed(text, Parse(text).Ranges);
        Volatile.Write(ref cache, parsed);
        return parsed.Ranges;
    }

    /// <summary>Why an address is refused (blocked, or not on the allow list), or null when it may come in.</summary>
    public static string? Refusal(IPAddress address, IReadOnlyList<IPNetwork> allow, IReadOnlyList<IPNetwork> block)
    {
        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (block.Any(r => r.Contains(a)))
        {
            return $"{a} is blocked";
        }
        return allow.Count > 0 && !allow.Any(r => r.Contains(a)) ? $"{a} is not on the allow list" : null;
    }

    /// <summary>Why this address is refused now, by the saved lists, or null.</summary>
    public string? Refusal(IPAddress address) =>
        Refusal(address, Cached(ref _allow, config[AllowKey]), Cached(ref _block, config[BlockKey]));

    /// <summary>Traefik's forward auth for the address alone: 204 to let the request in, 403 with why.</summary>
    public static void MapAddressFilter(IEndpointRouteBuilder app) =>
        app.MapMethods(Path, ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], (HttpContext ctx, AddressFilter filter) =>
        {
            // UseForwardedHeaders has put the address Traefik saw (X-Forwarded-For) here.
            if (ctx.Connection.RemoteIpAddress is not { } address || filter.Refusal(address) is not { } why)
            {
                return Results.NoContent();
            }
            return Results.Text($"Argus Arena does not take requests from this address ({why}). An admin changes it under Settings → Network access.",
                "text/plain; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);
        }).AllowAnonymous("Traefik's forward auth for the caller's network address, before every request.");
}
