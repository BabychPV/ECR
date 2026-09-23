using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="DocumentHeaderValue"/> — за зразком
/// <see cref="CellValueConfiguration"/>, але без партиційного стовпця: шапка
/// не має ні періоду, ні рядка таблиці, лише документ і поле.
/// </summary>
public sealed class DocumentHeaderValueConfiguration : IEntityTypeConfiguration<DocumentHeaderValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentHeaderValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Порожнє значення не має заповнених полів; заповнене має рівно одне.
        // Той самий третій стан (R-B4), що й doc.CellValue.
        builder.ToTable("DocumentHeaderValue", "doc", t => t.HasCheckConstraint(
            "CK_DocumentHeaderValue_Empty",
            "IsEmpty = 0 OR (ValueString IS NULL AND ValueNumeric IS NULL AND ValueDate IS NULL " +
            "AND ValueBool IS NULL AND ValueRegistryEntryId IS NULL AND ValueUnitId IS NULL)"));

        builder.HasKey(x => new { x.DocumentId, x.HeaderFieldDefId });

        builder.Property(x => x.ValueString).HasMaxLength(ColumnDef.MaxStringLength);
        builder.Property(x => x.ValueNumeric).HasPrecision(34, 16);
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");
        builder.Property(x => x.ValueRegistryEntryId).HasConversion<int?>();

        builder.Property(x => x.IsEmpty).HasDefaultValue(false, "DF_DocumentHeaderValue_Empty");

        // ⛔ Restrict, як і решта FK на doc.Document у цій базі (жоден не
        // CASCADE, EcrDbContext.OnModelCreating): видалення документа-чернетки
        // прибирає значення шапки явно, у DocumentDeletionStore.DeleteAsync.
        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_DocumentHeaderValue_Document")
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<HeaderFieldDef>()
               .WithMany()
               .HasForeignKey(x => x.HeaderFieldDefId)
               .HasConstraintName("FK_DocumentHeaderValue_Field")
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Domain.Entities.Dictionaries.RegistryEntry>()
               .WithMany()
               .HasForeignKey(x => x.ValueRegistryEntryId)
               .HasConstraintName("FK_DocumentHeaderValue_Entry")
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Domain.Entities.Units.Unit>()
               .WithMany()
               .HasForeignKey(x => x.ValueUnitId)
               .HasConstraintName("FK_DocumentHeaderValue_Unit")
               .OnDelete(DeleteBehavior.Restrict);
    }
}
