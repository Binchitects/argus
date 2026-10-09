namespace Llm.Api;

/// <summary>Retention and legal hold, groups' credit, prices and costs, the gateway's guardrail, and storage: their services and endpoints.</summary>
public static class GovernanceWiring
{
    public static void AddGovernance(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<Retention.RetentionOptions>(config.GetSection("Retention"));
        services.AddScoped<Retention.Retention>();
        services.AddScoped<Retention.DataExport>();
        services.AddHostedService<Retention.RetentionSweep>();
        services.Configure<Gateway.CreditOptions>(config.GetSection("Credit"));
        services.AddSingleton<Gateway.CreditBook>();
        services.AddScoped<Gateway.Credit>();
        services.AddScoped<Gateway.GroupTeams>();
        services.Configure<Gateway.PriceOptions>(config.GetSection("Prices"));
        services.AddScoped<Gateway.PriceBook>();
        services.AddSingleton<Gateway.SpendLog>();
        services.AddScoped<Gateway.CostRecalculation>();
        services.AddScoped<Dashboards.PromptUsage>();
        Storage.StorageEndpoints.AddStorage(services, config);
    }

    public static void MapGovernance(this IEndpointRouteBuilder app)
    {
        Retention.RetentionEndpoints.MapRetention(app);
        Safeguards.GuardrailEndpoints.MapGuardrail(app);
        Dashboards.PromptEndpoints.MapPrompts(app);
        Storage.StorageEndpoints.MapStorage(app);
    }
}
