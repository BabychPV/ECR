using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// ✎ B75: конфігурація <see cref="RowLock"/> (<c>doc.RowLock</c>): рядок таблиці документа, заблокований на проміжок дат.
/// </summary>
/// <remarks>
/// ⚠ Індекс — під читання рішення про запис: «діючі блокування документа за період» (<c>IX_RowLock_Doc_Period</c>,
/// фільтр <c>ReleasedAt IS NULL</c>); зняті рядки лишаються історією і в індекс не потрапляють.
/// <para>
/// ⚠ FK — NO ACTION (конвенція бази): видалення чернетки прибирає блокування явно (<c>DocumentDeletionStore</c>).
/// Хто зняв — без FK, як у <c>doc.DocumentPeriodReopen</c>.
/// </para>
/// </remarks>
public sealed class RowLockConfiguration : IEntityTypeConfiguration<RowLock>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RowLock> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RowLock", "doc", t =>
        {
            t.HasCheckConstraint("CK_RowLock_Range", "LockUntil > LockFrom");
            t.HasCheckConstraint(
                "CK_RowLock_Period", "PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 0 AND 99");
            t.HasCheckConstraint(
                "CK_RowLock_Release",
                "(ReleasedAt IS NULL AND ReleasedByUserId IS NULL) OR (ReleasedAt IS NOT NULL AND ReleasedByUserId IS NOT NULL)");
        });

        builder.HasKey(x => x.Id).HasName("PK_RowLock");
        builder.Property(x => x.Id).UseIdentityColumn();

        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.TableCode).HasMaxLength(RowLock.MaxTableCodeLength).IsRequired();
        builder.Property(x => x.RowKey).HasMaxLength(RowLock.MaxRowKeyLength).IsRequired();
        builder.Property(x => x.LockFrom).HasColumnType("datetime2(3)");
        builder.Property(x => x.LockUntil).HasColumnType("datetime2(3)");
        builder.Property(x => x.Note).HasMaxLength(RowLock.MaxNoteLength);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ReleasedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.DocumentId, x.PeriodKeyValue })
               .HasFilter("[ReleasedAt] IS NULL")
               .IncludeProperties(x => new { x.TableCode, x.RowKey, x.LockFrom, x.LockUntil, x.OwnerUserId })
               .HasDatabaseName("IX_RowLock_Doc_Period");

        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_RowLock_Document");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.OwnerUserId)
               .HasConstraintName("FK_RowLock_Owner");
    }
}
