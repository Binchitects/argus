using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

public partial class AppDbContext
{
    /// <summary>SAML assertions already used for a sign-in, shared by every replica (Llm.Api/Company/SamlReplay).</summary>
    public DbSet<UsedSamlAssertion> UsedSamlAssertions => Set<UsedSamlAssertion>();

    private static void SamlModel(ModelBuilder builder)
    {
        builder.Entity<UsedSamlAssertion>(e =>
        {
            e.ToTable("saml_assertions");
            e.Property(x => x.Id).HasMaxLength(256);
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}
