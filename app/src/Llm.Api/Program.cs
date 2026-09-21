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
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // The app publishes no port; the only way in is Traefik on the internal
    // network, so the forwarded headers it sets are the ones to believe.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = StaticCaching.Apply });

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var api = app.MapGroup("/api");
api.MapGet("/info", () => AppInfo.Current);
// Unknown API paths are a 404, never the single-page app's index.html.
api.MapFallback(() => Results.NotFound());
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = StaticCaching.Apply });

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await StartupDatabase.MigrateAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);
}

await app.RunAsync();
return 0;

public partial class Program;
