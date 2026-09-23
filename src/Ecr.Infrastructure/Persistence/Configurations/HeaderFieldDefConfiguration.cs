using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="HeaderFieldDef"/> — поле шапки документа, рівень
/// версії шаблону (не таблиці), за зразком <see cref="ColumnDefConfiguration"/>.
/// </summary>
public sealed class HeaderFieldDefConfiguration : IEntityTypeConfiguration<HeaderFieldDef>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<HeaderFieldDef> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("HeaderFieldDef", "cfg", t => t.HasCheckConstraint(
            "CK_HeaderFieldDef_Lookup", "DataType <> 5 OR LookupRegistryDefId IS NOT NULL"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DataType).HasConversion<byte>();

        // DEFAULT-и з іменами за зразком ColumnDef/RegistryFieldDef: безіменне
        // обмеження неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsRequired).HasDefaultValue(false, "DF_HeaderFieldDef_Req");
        builder.Property(x => x.IsDeleted).HasDefaultValue(false, "DF_HeaderFieldDef_Del");
        builder.LocalizedText(x => x.LabelL10n);

        builder.HasIndex(x => new { x.TemplateVersionId, x.Code })
               .IsUnique().HasDatabaseName("UQ_HeaderFieldDef");

        builder.HasOne<RegistryDef>()
               .WithMany()
               .HasForeignKey(x => x.LookupRegistryDefId)
               .HasConstraintName("FK_HeaderFieldDef_Registry")
               .OnDelete(DeleteBehavior.Restrict);
    }
}
