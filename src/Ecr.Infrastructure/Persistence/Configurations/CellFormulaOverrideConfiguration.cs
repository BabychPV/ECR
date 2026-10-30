using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// ✎ D-461: конфігурація <see cref="CellFormulaOverride"/> (<c>doc.CellFormulaOverride</c>) — позначки «ручне значення
/// замість формули».
/// </summary>
/// <remarks>
/// ⛔ Зовнішніх ключів НЕМАЄ навмисно (як у <c>doc.CellStyle</c>): архівація звільняє партиції <c>doc.TableRow</c>
/// через <c>TRUNCATE … WITH (PARTITIONS)</c>. Партиційний стовпець — перший у ключі; розміщення на
/// <c>ps_ByPeriodKey</c> задає <c>07-partition-tables.sql</c>. Індекс за екземпляром — для зрізу (одна таблиця ×
/// період), за періодом — для перерахунку документа (усі таблиці періоду).
/// </remarks>
public sealed class CellFormulaOverrideConfiguration : IEntityTypeConfiguration<CellFormulaOverride>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CellFormulaOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CellFormulaOverride", "doc", t =>
            t.HasCheckConstraint("CK_CellFormulaOverride_Reason", "LEN(Reason) > 0"));

        builder.HasKey(x => new { x.PeriodKeyValue, x.TableRowId, x.ColumnDefId }).HasName("PK_CellFormulaOverride");

        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.TableRowId).HasColumnName("TableRowId");
        builder.Property(x => x.ColumnDefId).HasColumnName("ColumnDefId");
        builder.Property(x => x.Reason).HasMaxLength(CellFormulaOverride.MaxReasonLength).IsRequired();
        builder.Property(x => x.SetAt).HasColumnType("datetime2(3)");

        builder.HasIndex(x => new { x.PeriodKeyValue, x.TableInstanceId })
               .HasDatabaseName("IX_CellFormulaOverride_Instance");

        builder.Ignore(x => x.Address);
    }
}
