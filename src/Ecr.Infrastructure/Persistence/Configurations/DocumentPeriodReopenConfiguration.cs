using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="DocumentPeriodReopen"/> (<c>doc.DocumentPeriodReopen</c>, приймальна 08.11 (i)):
/// закритий місяць, відкритий для одного документа.
/// </summary>
/// <remarks>
/// ⚠ Індекси — під два читання. (1) Рішення про запис і перерахунок: «чи діє виняток документа за місяць» —
/// <c>UQ_DocumentPeriodReopen_Active</c> (документ, місяць) з фільтром <c>ClosedAt IS NULL</c>; він же тримає
/// «один діючий виняток на документ × місяць» на рівні бази. (2) <c>PeriodStateJob</c> шукає прострочені —
/// <c>IX_DocumentPeriodReopen_OpenUntil</c> з тим самим фільтром. Закриті рядки лишаються доказом і в індекси
/// не потрапляють.
/// <para>
/// ⚠ FK — NO ACTION (конвенція бази): видалення чернетки прибирає винятки явно (<c>DocumentDeletionStore</c>).
/// Хто закрив — без FK на користувача: система (строк минув) пише <c>NULL</c>.
/// </para>
/// </remarks>
public sealed class DocumentPeriodReopenConfiguration : IEntityTypeConfiguration<DocumentPeriodReopen>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentPeriodReopen> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentPeriodReopen", "doc", t =>
        {
            t.HasCheckConstraint("CK_DocumentPeriodReopen_Reason", "LEN(Reason) > 0");
            t.HasCheckConstraint(
                "CK_DocumentPeriodReopen_Period",
                "PeriodKey / 100 BETWEEN 1900 AND 9999 AND PeriodKey % 100 BETWEEN 1 AND 99");
            t.HasCheckConstraint(
                "CK_DocumentPeriodReopen_Close",
                "(ClosedAt IS NULL AND CloseReason IS NULL AND ClosedByUserId IS NULL) "
                + "OR (ClosedAt IS NOT NULL AND CloseReason IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.Reason).HasMaxLength(DocumentPeriodReopen.MaxReasonLength).IsRequired();
        builder.Property(x => x.CloseReason).HasMaxLength(DocumentPeriodReopen.MaxCloseReasonLength);
        builder.Property(x => x.OpenUntil).HasColumnType("datetime2(3)");
        builder.Property(x => x.OpenedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.ClosedAt).HasColumnType("datetime2(3)");
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.DocumentId, x.PeriodKeyValue })
               .IsUnique()
               .HasFilter("[ClosedAt] IS NULL")
               .IncludeProperties(x => x.OpenUntil)
               .HasDatabaseName("UQ_DocumentPeriodReopen_Active");

        builder.HasIndex(x => x.OpenUntil)
               .HasFilter("[ClosedAt] IS NULL")
               .HasDatabaseName("IX_DocumentPeriodReopen_OpenUntil");

        builder.HasOne<Document>()
               .WithMany()
               .HasForeignKey(x => x.DocumentId)
               .HasConstraintName("FK_DocumentPeriodReopen_Document");

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(x => x.OpenedByUserId)
               .HasConstraintName("FK_DocumentPeriodReopen_OpenedBy");
    }
}
