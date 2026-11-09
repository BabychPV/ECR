using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>doc.PermitSegment</c> — feature (j): later permits of a row's permit cell or of a header field, «from date».
/// </summary>
/// <remarks>
/// ⚠ Exactly one target (<c>CK_PermitSegment_Target</c>). One segment per target and date — two filtered unique indexes
/// (header / row); they are also the read path («the history of this document»: <c>DocumentId</c> leads both).
/// FK — NO ACTION (database convention): deleting a draft document removes its segments explicitly
/// (<c>DocumentDeletionStore</c>).
/// </remarks>
public sealed class PermitSegmentConfiguration : IEntityTypeConfiguration<PermitSegment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PermitSegment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PermitSegment", "doc", t => t.HasCheckConstraint(
            "CK_PermitSegment_Target",
            "(HeaderFieldDefId IS NOT NULL AND TableDefId IS NULL AND RowKey IS NULL AND ColumnDefId IS NULL AND SheetCopyId IS NULL) "
            + "OR (HeaderFieldDefId IS NULL AND TableDefId IS NOT NULL AND RowKey IS NOT NULL AND ColumnDefId IS NOT NULL)"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.RowKey).HasMaxLength(PermitSegment.MaxRowKeyLength);
        builder.Property(x => x.ValidFrom).HasColumnType("date");
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");

        builder.HasIndex(x => new { x.DocumentId, x.HeaderFieldDefId, x.ValidFrom })
               .IsUnique()
               .HasFilter("[HeaderFieldDefId] IS NOT NULL")
               .HasDatabaseName("UQ_PermitSegment_Header");

        builder.HasIndex(x => new { x.DocumentId, x.TableDefId, x.SheetCopyId, x.RowKey, x.ColumnDefId, x.ValidFrom })
               .IsUnique()
               .HasFilter("[TableDefId] IS NOT NULL")
               .HasDatabaseName("UQ_PermitSegment_Row");

        builder.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_PermitSegment_Document");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.RegistryEntryId)
               .HasConstraintName("FK_PermitSegment_Entry");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId)
               .HasConstraintName("FK_PermitSegment_CreatedBy");
    }
}

/// <summary>
/// <c>doc.CellPermitPart</c> — feature (j): the parts of a month value split between permits.
/// </summary>
/// <remarks>
/// ⚠ Physical placement on <c>ps_ByPeriodKey</c> — <c>07-partition-tables.sql</c> (EF cannot place on a partition
/// scheme); therefore every index carries <c>PeriodKey</c>. No FK to <c>doc.TableRow</c> (logical address) — the
/// archive truncates the period partitions (<c>arc.usp_ArchiveYear</c>), as for <c>doc.CellStyle</c>.
/// </remarks>
public sealed class CellPermitPartConfiguration : IEntityTypeConfiguration<CellPermitPart>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CellPermitPart> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CellPermitPart", "doc", t =>
        {
            t.HasCheckConstraint("CK_CellPermitPart_Days", "DayTo >= DayFrom");
            t.HasCheckConstraint("CK_CellPermitPart_PartNo", "PartNo >= 1");
        });

        builder.HasKey(x => new { x.PeriodKeyValue, x.Id }).HasName("PK_CellPermitPart");
        builder.Property(x => x.Id).ValueGeneratedOnAdd().UseIdentityColumn();
        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.RowKey).HasMaxLength(PermitSegment.MaxRowKeyLength).IsRequired();
        builder.Property(x => x.DayFrom).HasColumnType("date");
        builder.Property(x => x.DayTo).HasColumnType("date");
        builder.Property(x => x.ValueNumeric).HasColumnType("decimal(34,16)");

        builder.HasIndex(x => new { x.PeriodKeyValue, x.DocumentId, x.TableDefId, x.SheetCopyId, x.RowKey, x.ColumnDefId, x.PartNo })
               .IsUnique()
               // ⛔ No filter: EF would add «[SheetCopyId] IS NOT NULL» for the nullable column and leave the ORIGINAL sheet
               // (SheetCopyId NULL) without uniqueness. SQL Server treats NULLs as equal in a unique index — what we want.
               .HasFilter(null)
               .HasDatabaseName("UQ_CellPermitPart_Cell");

        builder.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_CellPermitPart_Document");
        builder.HasOne<TableDef>().WithMany().HasForeignKey(x => x.TableDefId)
               .HasConstraintName("FK_CellPermitPart_Table");
        builder.HasOne<RegistryEntry>().WithMany().HasForeignKey(x => x.RegistryEntryId)
               .HasConstraintName("FK_CellPermitPart_Entry");
    }
}
