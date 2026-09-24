using Llm.Api;
using Llm.Core.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

if (args is ["healthcheck"])
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    // One JSON object per line: promtail ships it to Loki as-is.
    builder.Logging.ClearProviders().AddJsonConsole();
}

var connectionString = DatabaseSettings.ConnectionString(builder.Configuration);
builder.Services.AddDbContext<AppDbContext>(o =>
{
    o.UseNpgsql(connectionString);
    o.UseOpenIddict<Guid>();
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

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();
app.UseCsrfGuard();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = StaticCaching.Apply });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapAppIdentity();

var api = app.MapGroup("/api");
// Public: the sign-in page shows the name, headline and where to get help.
api.MapGet("/info", (Microsoft.Extensions.Options.IOptionsMonitor<Llm.Api.Settings.BrandingOptions> branding) =>
{
    var b = branding.CurrentValue;
    return new
    {
        name = string.IsNullOrWhiteSpace(b.ProductName) ? AppInfo.Current.Name : b.ProductName,
        version = AppInfo.Current.Version,
        signInHeadline = b.SignInHeadline,
        supportContact = string.IsNullOrWhiteSpace(b.SupportContact) ? null : b.SupportContact,
    };
});
// Unknown API paths are a 404, never the single-page app's index.html.
api.MapFallback(() => Results.NotFound());
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = StaticCaching.Apply });

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await StartupDatabase.MigrateAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);
    // On a first start the settings table did not exist when the source first read it.
    savedSettings.Provider.Reload();
    await app.BootstrapIdentityAsync();
}
// What the restart-bound settings were at start, to tell whether a restart is due.
app.Services.GetRequiredService<Llm.Api.Settings.SettingsAtStart>();

await app.RunAsync();
return 0;

public partial class Program;
