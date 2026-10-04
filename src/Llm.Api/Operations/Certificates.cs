using Llm.Api.Dashboards;

namespace Llm.Api.Operations;

/// <summary>The certificate Traefik serves, as its metrics tell: its name, when it expires, and who issued it.</summary>
/// <param name="Issuer">traefik: Traefik's own default (browsers warn); letsencrypt: from Let's Encrypt, which Traefik renews; own: one of your own (scripts/make-cert.sh, or a company's).</param>
public sealed record CertificateStatus(string Name, DateTimeOffset ExpiresAt, int Days, string Issuer);

/// <summary>Reads traefik_tls_certs_not_after from Prometheus, for Admin → Overview (the alert rule CertificateExpiresSoon reads the same).</summary>
public static class Certificates
{
    /// <summary>The name of the certificate Traefik makes for itself when it is given none.</summary>
    public const string TraefikDefault = "TRAEFIK DEFAULT CERT";

    /// <summary>The certificate that expires first (Traefik's own only when it serves no other); null when Prometheus does not know.</summary>
    public static async Task<CertificateStatus?> ReadAsync(PromDatasource prom, StackOptions stack, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<RawSeries> series;
        try
        {
            // Overview does not wait long for it.
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(5));
            // The newest certificate for each name: a renewed one replaces the old in what Traefik serves.
            series = await prom.InstantAsync("max by (cn) (traefik_tls_certs_not_after)", now, quick.Token);
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
        var certs = series.Select(s => (Name: s.Labels.GetValueOrDefault("cn", ""), At: s.Points.Count > 0 ? s.Points[0][1] : null)).Where(c => c.At is not null).ToList();
        var served = certs.Where(c => c.Name != TraefikDefault).ToList();
        var (name, at) = served.Count > 0 ? served.MinBy(c => c.At) : certs.FirstOrDefault(c => c.Name == TraefikDefault);
        if (at is not { } seconds)
        {
            return null;
        }
        var expires = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        var issuer = served.Count == 0 ? "traefik" : stack.Acme is { Length: > 0 } ? "letsencrypt" : "own";
        return new CertificateStatus(name, expires, (int)Math.Floor((expires - now).TotalDays), issuer);
    }
}
