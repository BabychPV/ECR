using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="CellStyle"/> (<c>doc.CellStyle</c>) — користувацької заливки,
/// кольору тексту й жирності комірки.
/// </summary>
/// <remarks>
/// ⛔ Зовнішніх ключів НЕМАЄ навмисно (як у <c>ext.RowWindowValue</c>): архівація звільняє
/// партиції <c>doc.TableRow</c> через <c>TRUNCATE … WITH (PARTITIONS)</c>, а SQL Server
/// відмовляє в ньому, якщо на таблицю посилається будь-який ключ. Партиційний стовпець — перший
/// у ключі; розміщення на <c>ps_ByPeriodKey</c> задає <c>07-partition-tables.sql</c>.
/// </remarks>
public sealed class CellStyleConfiguration : IEntityTypeConfiguration<CellStyle>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CellStyle> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CellStyle", "doc", t =>
        {
            // Лише #rrggbb: рядок, що дійшов до бази, ніколи не стане CSS-ін'єкцією на клієнті.
            t.HasCheckConstraint(
                "CK_CellStyle_Background",
                "BackgroundHex IS NULL OR BackgroundHex LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
            t.HasCheckConstraint(
                "CK_CellStyle_Foreground",
                "ForegroundHex IS NULL OR ForegroundHex LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
        });

        builder.HasKey(x => new { x.PeriodKeyValue, x.TableRowId, x.ColumnDefId }).HasName("PK_CellStyle");

        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.TableRowId).HasColumnName("TableRowId");
        builder.Property(x => x.ColumnDefId).HasColumnName("ColumnDefId");

        builder.Property(x => x.BackgroundHex).HasColumnType("char(7)");
        builder.Property(x => x.ForegroundHex).HasColumnType("char(7)");
        builder.Property(x => x.IsBold).HasDefaultValue(false, "DF_CellStyle_Bold");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");

        builder.Ignore(x => x.Address);
        builder.Ignore(x => x.IsBlank);
    }
}
