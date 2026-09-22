using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="RegistryDefinitionDraft"/> (<c>BE-24</c> крок 2).</summary>
public sealed class RegistryDefinitionDraftConfiguration : IEntityTypeConfiguration<RegistryDefinitionDraft>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryDefinitionDraft> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RegistryDefinitionDraft", "cfg");
        builder.HasKey(x => x.RegistryDefId).HasName("PK_RegistryDefinitionDraft");
        builder.Property(x => x.RegistryDefId).ValueGeneratedNever();
        builder.Property(x => x.ContentJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(RegistryDefinitionDraft.ReasonMaxLength).IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne<RegistryDef>()
               .WithMany()
               .HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_RegDraft_Registry");
    }
}
