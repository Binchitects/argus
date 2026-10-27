using System.Net;
using System.Text.RegularExpressions;
using Llm.Api;
using Llm.Api.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The stack's features (deploy/features): which are on, and the app leaving out those that are off.</summary>
[Collection(nameof(AppCollection))]
public sealed partial class FeaturesTests(AppFixture app)
{
    private static Features With(string? on) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Features:On"] = on }).Build());

    [Fact]
    public void Unset_every_feature_is_on_and_set_only_those_named_whose_required_features_are_on_too()
    {
        Assert.Empty(With(null).Off);
        var some = With("sandbox, lab,lean,meetings");
        Assert.True(some.IsOn("lab"));
        Assert.True(some.IsOn("lean"));
        // Meetings needs speech, which is not named.
        Assert.False(some.IsOn("meetings"));
        Assert.False(some.IsOn("pictures"));
        Assert.Contains("speech", some.Off);
        // Lean needs the lab, which needs the sandbox.
        Assert.False(With("lab,lean").IsOn("lean"));
        // Empty: the core only, as the stack runs with no COMPOSE_PROFILES.
        Assert.Equal(Features.All.Count, With("").Off.Count);
        Assert.Throws<ArgumentException>(() => some.IsOn("no-such-feature"));
    }

    [Fact]
    public void A_service_runs_when_it_is_the_cores_or_its_feature_is_on()
    {
        var f = With("pictures,monitoring");
        Assert.True(f.Runs("imagegen"));
        Assert.True(f.Runs("loki"));
        Assert.False(f.Runs("videogen"));
        Assert.False(f.Runs("audio"));
        Assert.True(f.Runs("postgres"));
    }

    [Fact]
    public void The_list_is_well_formed_every_required_feature_is_listed_and_the_defaults_hold_their_requirements()
    {
        var ids = Features.All.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Features.All, f => Assert.Matches(Id(), f.Id));
        Assert.All(Features.All.SelectMany(f => f.Requires), r => Assert.Contains(r, ids));
        Assert.All(Features.All.Where(f => f.Needs is not null), f => Assert.Contains(f.Needs, new[] { "gpu", "gitlab" }));
        var services = Features.All.SelectMany(f => f.Services).ToList();
        Assert.Equal(services.Count, services.Distinct().Count());
        // A new installation's features are all on together.
        var defaults = With(string.Join(",", Features.All.Where(f => f.OnByDefault).Select(f => f.Id)));
        Assert.All(Features.All.Where(f => f.OnByDefault), f => Assert.True(defaults.IsOn(f.Id), f.Id));
    }

    [Fact]
    public void Every_endpoint_that_is_part_of_a_feature_names_one_deploy_features_lists()
    {
        var named = app.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<FeatureMetadata>()?.Id).OfType<string>().Distinct().Order().ToList();
        Assert.Equal(["agents", "apps", "code-studio", "data", "decks", "lab", "media", "meetings", "monitoring"], named);
    }

    [Fact]
    public async Task A_feature_that_is_off_answers_404_with_what_turns_it_on_leaves_its_modules_unasked_and_is_named_in_me()
    {
        var on = string.Join(",", Features.All.Select(f => f.Id).Where(id => id is not ("lab" or "decks" or "pictures")));
        await using var f = app.Create(app.ConnectionStringFor("features_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Features:On"] = on });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        var lab = await admin.GetAsync("/api/lab/notebooks");
        await StatusAssert.Is(HttpStatusCode.NotFound, lab);
        var body = await admin.JsonAsync(lab);
        Assert.Equal("feature-off", body.GetProperty("status").GetString());
        Assert.Contains("installer.sh repair --with lab", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync("/api/decks"));
        // The media studio needs pictures: off with it.
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync("/api/media/items"));
        // On: answers as before.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/meetings"));

        var me = await admin.JsonAsync(await admin.GetAsync("/api/auth/me"));
        // Those named off, and those that need them (the media studio, the studio's models, Lean), in deploy/features' order.
        Assert.Equal(["pictures", "lean", "studio", "lab", "decks", "media"], me.GetProperty("featuresOff").EnumerateArray().Select(e => e.GetString()));
        Assert.False(await f.Services.GetRequiredService<Modules>().HasAsync("imagegen"));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Id();
}
