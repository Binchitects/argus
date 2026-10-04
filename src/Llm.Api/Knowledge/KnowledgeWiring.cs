namespace Llm.Api.Knowledge;

/// <summary>Company knowledge and retrieval: the embedder, the store, the connectors, the sync loop and the file index.</summary>
public static class KnowledgeWiring
{
    public static void AddKnowledge(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<KnowledgeOptions>(config.GetSection("Knowledge"));
        services.AddHttpClient(Embedder.Client, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddScoped<Embedder>();
        services.AddSingleton<VectorSupport>();
        services.AddScoped<KnowledgeStore>();
        services.AddScoped<GitLabConnector>();
        services.AddScoped<IKnowledgeConnector>(sp => sp.GetRequiredService<GitLabConnector>());
        services.AddScoped<IKnowledgeConnector, FolderConnector>();
        services.AddScoped<IKnowledgeConnector, WebsiteConnector>();
        services.AddScoped<KnowledgeSyncer>();
        services.AddSingleton<KnowledgeSync>();
        services.AddHostedService(sp => sp.GetRequiredService<KnowledgeSync>());
        services.AddSingleton<FileIndex>();
        services.AddHostedService(sp => sp.GetRequiredService<FileIndex>());
        services.AddScoped<Retrieval>();
        services.AddScoped<KnowledgeSearch>();
        services.AddScoped<KnowledgeTool>();
    }
}
