using Llm.Api.Access;
using Llm.Api;
using Llm.Core.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

if (args is ["healthcheck"])
{
    return await HealthProbe.RunAsync();
}
// scripts/sign-code-arena.sh: the Windows builds Authenticode-signed, in a container of this image with no network.
if (args is ["sign-code-arena", .. var signFolder] && signFolder.Length <= 1)
{
    return await Llm.Api.Downloads.CodeArenaSigner.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error,
        signFolder is [var folder] ? folder : "/downloads/code-arena");
}

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    // One JSON object per line: promtail ships it to Loki as-is, with personal data masked first.
    builder.Logging.ClearProviders()
        .AddConsole(o => o.FormatterName = ScrubbedJsonFormatter.FormatterName)
        .AddConsoleFormatter<ScrubbedJsonFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();
}

var connectionString = DatabaseSettings.ConnectionString(builder.Configuration);
// People's files sealed at rest with a key from APP_KEY (Llm.Core's FileSealing); without APP_KEY they are stored as they are.
var fileKey = builder.Configuration["Auth:DataKey"] is { Length: > 0 } dataKey ? FileSealer.Derive(dataKey) : null;
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
{
    o.UseNpgsql(connectionString);
    // Tests (Database:StrictQueries) fail on a page of rows taken in no order, which comes back in any order.
    if (builder.Configuration.GetValue<bool>("Database:StrictQueries"))
    {
        o.ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.RowLimitingOperationWithoutOrderByWarning));
    }
    ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)o).AddOrUpdateExtension(new FileKeyExtension(fileKey));
    o.UseOpenIddict<Guid>();
    // The bell's news is pushed to people's devices too, whoever saves it.
    o.AddInterceptors(sp.GetRequiredService<Llm.Api.Notifications.PushOnSave>());
    // A chat's changes reach its owner's pages and Code Arenas at once, whoever saves them.
    o.AddInterceptors(sp.GetRequiredService<Llm.Api.Chat.ChatChangesOnSave>());
});
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.AddAppIdentity();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // The app publishes no port; the only way in is Traefik on the internal
    // network, so the forwarded headers it sets are the ones to believe.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Settings saved in the app (the Settings page) are configuration too, and win
// over the environment: added after Build so nothing is read before they are.
var savedSettings = new Llm.Api.Settings.DatabaseConfigurationSource(connectionString, builder.Configuration["Auth:DataKey"]);
builder.Services.AddSingleton(savedSettings.Provider);

var app = builder.Build();
((IConfigurationBuilder)app.Configuration).Add(savedSettings);
// arenactl reset-admin-password (root on the host): the password from stdin, then out; the app itself does not start.
if (args is ["reset-admin-password", .. var resetArgs])
{
    return await Llm.Api.Identity.AdminReset.RunAsync(app.Services, resetArgs, Console.In, Console.Out, Console.Error);
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();
app.UseCsrfGuard();
// Code Studio's terminals, language servers and debugger reach their workspace over WebSockets.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
// Code Studio's forwarded ports (their own hosts under the domain): answered here, before anything of Arena's API.
Llm.Api.Code.Studio.PortForwards.UsePortForwards(app);
app.UseTwoFactorEnrolment();
Llm.Api.Compliance.NoticeGate.UseNoticeGate(app);
app.UseAuthorization();
// Which Code Arena each person uses (its User-Agent): put down behind the request, and its person told when it is old.
app.Use((http, next) =>
{
    http.RequestServices.GetRequiredService<Llm.Api.Downloads.CodeArenaClients>().Seen(http);
    return next(http);
});
app.UseRateLimiter();
// A feature that is off (deploy/features): its API answers 404.
Llm.Api.FeatureEndpoints.UseFeatures(app);

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous("Liveness: up or not, nothing private.");
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous("Readiness: ready or not, nothing private.");

app.MapAppIdentity();

var api = app.MapGroup("/api");
// Public: the sign-in page shows the name, headline and where to get help.
api.MapGet("/info", async (Microsoft.Extensions.Options.IOptionsMonitor<Llm.Api.Settings.BrandingOptions> branding,
    Microsoft.Extensions.Options.IOptionsMonitor<Llm.Api.Compliance.NoticeOptions> notice, Llm.Core.Data.AppDbContext db,
    Microsoft.Extensions.Options.IOptions<Llm.Api.Operations.CodeIntelOptions> codeIntel, Microsoft.Extensions.Options.IOptions<Llm.Api.Identity.AuthOptions> auth,
    IConfiguration config, CancellationToken ct) =>
{
    var b = branding.CurrentValue;
    var icon = await Llm.Api.Settings.BrandingEndpoints.IconAsync(db, ct);
    return new
    {
        name = string.IsNullOrWhiteSpace(b.ProductName) ? AppInfo.Current.Name : b.ProductName,
        // The icon an admin chose (Settings → Branding); null: the app's own, /favicon.svg.
        icon = icon?.Url,
        // Where people's coding agents reach the code-intelligence service's MCP (CODE_INTEL_HOST): Code Arena asks here
        // rather than guess its name; null where it is off.
        codeIntelUrl = Llm.Api.Operations.Features.On(config, "code-intel") && codeIntel.Value.Enabled ? codeIntel.Value.PublicUrl(auth.Value, "/mcp") : null,
        version = AppInfo.Current.Version,
        signInHeadline = b.SignInHeadline,
        supportContact = string.IsNullOrWhiteSpace(b.SupportContact) ? null : b.SupportContact,
        // The AGPL's offer of the source to everyone who uses it over a network (LICENSING.md).
        license = "AGPL-3.0-only",
        source = b.SourceUrl,
        // What everyone accepts before using the service, shown on the sign-in page too.
        notice = notice.CurrentValue.On ? new { notice.CurrentValue.Title, notice.CurrentValue.Text, notice.CurrentValue.Version } : null,
    };
}).AllowAnonymous("The sign-in page's name, headline and help: public.");
api.MapFallback(() => Results.NotFound()).AllowAnonymous("A plain 404.");
// Pages come from the web container (src/web); Traefik sends only /api,
// /connect, /.well-known and /scim here. Anything else that arrives is a plain 404.
app.MapFallback(() => Results.NotFound()).AllowAnonymous("A plain 404.");

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    // Replicas starting together go through this one at a time.
    await using (await StartupDatabase.OneAtATimeAsync(connectionString, app.Logger, app.Lifetime.ApplicationStopping))
    {
        await StartupDatabase.MigrateAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);
        // On a first start the settings table did not exist when the source first read it.
        savedSettings.Provider.Reload();
        await app.BootstrapIdentityAsync();
    }
}
// What the restart-bound settings were at start, to tell whether a restart is due.
app.Services.GetRequiredService<Llm.Api.Settings.SettingsAtStart>();

await app.RunAsync();
return 0;

public partial class Program;
