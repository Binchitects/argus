using Llm.Api.Operations;

namespace Llm.Api;

/// <summary>Marks an endpoint as part of a feature: while the feature is off, it answers 404.</summary>
public sealed record FeatureMetadata(string Id);

public static class FeatureEndpoints
{
    /// <summary>The endpoints answer only while the feature is on (deploy/features).</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string id) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new FeatureMetadata(Features.Of(id).Id));

    /// <summary>A request to a feature that is off: 404, with what turns it on.</summary>
    public static void UseFeatures(this WebApplication app) => app.Use(async (http, next) =>
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<FeatureMetadata>() is { } f && !http.RequestServices.GetRequiredService<Features>().IsOn(f.Id))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new
            {
                status = "feature-off",
                error = $"{Features.Of(f.Id).Title} is not installed here. An admin turns it on with: installer.sh repair --with {f.Id}",
            });
            return;
        }
        await next(http);
    });
}
