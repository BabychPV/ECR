using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>Конфігурація <see cref="DocumentIndexValue"/>.</summary>
public sealed class DocumentIndexValueConfiguration : IEntityTypeConfiguration<DocumentIndexValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentIndexValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentIndexValue", "doc");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ValueString).HasMaxLength(400);
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(34,16)");
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");

        builder.HasIndex(x => new { x.DocumentId, x.ColumnDefId })
               .IsUnique().HasDatabaseName("UQ_DocumentIndexValue");

        // Індекс пошуку: саме він робить фільтр по документах можливим без
        // жодного некластерного індексу на doc.CellValue.
        builder.HasIndex(x => new { x.ColumnDefId, x.ValueString })
               .HasDatabaseName("IX_DocumentIndexValue_Search")
               .IncludeProperties(x => x.DocumentId);

        builder.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_DocIx_Doc");
        builder.HasOne<ColumnDef>().WithMany().HasForeignKey(x => x.ColumnDefId)
               .HasConstraintName("FK_DocIx_Column");
    }
}
