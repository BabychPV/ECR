using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="RegistryImportProfile"/> — профілів імпорту довідника
/// (RT-06, FEATURE-REGISTRY-TABLES §3.2, міграція <c>RK05RegistryImportProfile</c>).
/// </summary>
/// <remarks>
/// ⛔ Імена обмежень — за <c>02a-db-schema.md</c>: безіменне обмеження неможливо
/// прибрати скриптом, не з'ясувавши спершу його випадкове ім'я на конкретній базі.
/// </remarks>
public sealed class RegistryImportProfileConfiguration : IEntityTypeConfiguration<RegistryImportProfile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistryImportProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ⛔ Синтаксис специфікації тримає й база: профіль, вставлений повз домен
        // (скрипт, перенесення), зі зламаним JSON зламав би майстер імпорту тому,
        // хто його відкриє наступним.
        builder.ToTable("RegistryImportProfile", "cfg", t => t.HasCheckConstraint(
            "CK_RegImpProfile_Json", "ISJSON([SpecJson]) = 1"));
        builder.HasKey(x => x.Id).HasName("PK_RegistryImportProfile");
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SpecJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.RegistryDefId, x.Code })
               .IsUnique().HasDatabaseName("UQ_RegistryImportProfile");

        builder.HasOne<RegistryDef>().WithMany().HasForeignKey(x => x.RegistryDefId)
               .HasConstraintName("FK_RegImpProfile_Def");
    }
}
