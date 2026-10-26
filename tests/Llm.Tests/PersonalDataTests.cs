using System.Net;
using System.Text.Json;
using Llm.Api;
using Llm.Api.Safeguards;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Llm.Tests;

/// <summary>Personal and health data found, masked or kept from cloud models, and never in the app's logs.</summary>
[Collection(nameof(AppCollection))]
public sealed class PersonalDataTests(AppFixture app)
{
    [Theory]
    [Trait("Control", "DAT-08")]
    [InlineData("Patient DOB: 1984-03-12, seen today.", "dob", "1984-03-12")]
    [InlineData("She was born on 12 March 1984 in Leeds.", "dob", "12 March 1984")]
    [InlineData("Date of birth 03/12/1984", "dob", "03/12/1984")]
    [InlineData("MRN: 00123456 admitted", "mrn", "00123456")]
    [InlineData("His NHS number is 943 476 5919.", "nhs", "943 476 5919")]
    [InlineData("کد ملی: 0012345679", "national_id", "0012345679")]
    [InlineData("national code 123-456789-1 on file", "national_id", "123-456789-1")]
    [InlineData("Medicare 1EG4-TE5-MK73 on the card", "medicare", "1EG4-TE5-MK73")]
    public void Health_and_identity_data_are_found_by_what_names_them_and_their_check_digits(string text, string kind, string value)
    {
        var found = Assert.Single(Pii.Find(text), f => f.Kind == kind);
        Assert.Equal(value, text.Substring(found.Index, found.Length));
        Assert.DoesNotContain(value, Pii.Mask(text), StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Control", "DAT-08")]
    [InlineData("His NHS number is 943 476 5918.")]
    [InlineData("کد ملی: 0012345678")]
    [InlineData("national code 1111111111")]
    [InlineData("The build 2026-10-12 shipped; order 943 476 5919 arrived.")]
    [InlineData("MRN looks fine")]
    public void A_wrong_check_digit_or_a_number_nothing_names_is_not_taken_for_one(string text)
    {
        Assert.DoesNotContain(Pii.Find(text), f => f.Kind is "nhs" or "national_id" or "dob" or "mrn");
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public void The_apps_log_lines_carry_no_personal_data_but_keep_the_addresses_security_needs()
    {
        var formatter = new ScrubbedJsonFormatter();
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Who", "ana@example.com"), new("Phone", "+44 7700 900123"), new("From", "10.0.0.5"),
            new("{OriginalFormat}", "Sign-in for {Who} ({Phone}) from {From}"),
        };
        using var writer = new StringWriter();
        var entry = new LogEntry<List<KeyValuePair<string, object?>>>(LogLevel.Warning, "Llm.Api.Test", new EventId(7), state,
            new InvalidOperationException("ana@example.com, DOB: 1984-03-12"), (_, _) => "Sign-in for ana@example.com (+44 7700 900123) from 10.0.0.5");
        formatter.Write(entry, null, writer);
        var line = writer.ToString().Trim();
        Assert.DoesNotContain("ana@example.com", line, StringComparison.Ordinal);
        Assert.DoesNotContain("7700 900123", line, StringComparison.Ordinal);
        Assert.DoesNotContain("1984-03-12", line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("Warning", root.GetProperty("LogLevel").GetString());
        Assert.Equal("Sign-in for [email] ([phone]) from 10.0.0.5", root.GetProperty("Message").GetString());
        Assert.Equal("[email]", root.GetProperty("State").GetProperty("Who").GetString());
        Assert.Equal("Sign-in for {Who} ({Phone}) from {From}", root.GetProperty("State").GetProperty("{OriginalFormat}").GetString());
        Assert.Contains("[date of birth]", root.GetProperty("Exception").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task The_compliance_check_wants_a_rule_keeping_health_data_from_cloud_models_and_the_templates_give_one()
    {
        await using var f = app.Create(app.ConnectionStringFor("pii_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<JsonElement> CheckAsync() =>
            (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "personal-data");
        Assert.Equal("warn", (await CheckAsync()).GetProperty("state").GetString());

        var templates = (await admin.JsonAsync(await admin.GetAsync("/api/admin/safeguards/templates"))).EnumerateArray().ToList();
        var health = templates.Single(t => t.GetProperty("name").GetString() == "Health and identity data to cloud models");
        Assert.Equal(Pii.HealthKinds, health.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(["@external"], health.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/safeguards/rules", health));
        var check = await CheckAsync();
        Assert.Equal("ok", check.GetProperty("state").GetString());
        Assert.Contains("Health and identity data to cloud models", check.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
