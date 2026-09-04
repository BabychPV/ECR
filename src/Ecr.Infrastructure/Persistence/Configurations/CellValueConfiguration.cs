using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Конфігурація <see cref="CellValue"/>.
/// </summary>
/// <remarks>
/// Ключ складений і **починається з партиційного стовпця**: без цього індекси
/// не вирівняні, і партиційні операції неможливі. Сурогатного <c>Id</c> немає
/// навмисно — він коштував би ~0.9 ГБ/рік і не давав би нічого (R-A1).
/// </remarks>
public sealed class CellValueConfiguration : IEntityTypeConfiguration<CellValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CellValue> builder)
        => throw new NotImplementedException(
            "TODO:\n" +
            "builder.ToTable(\"CellValue\", \"doc\");\n" +
            "builder.HasKey(x => new { x.PeriodKeyValue, x.TableRowId, x.ColumnDefId });\n" +
            "Property(PeriodKeyValue).HasColumnName(\"PeriodKey\");\n" +
            "Property(ValueString).HasMaxLength(1000);\n" +
            "Property(ValueNumeric).HasPrecision(28, 10);\n" +
            "Property(ValueDate).HasColumnType(\"datetime2(3)\");\n" +
            "FK на TableRow — СКЛАДЕНИЙ (PeriodKey, TableRowId) → doc.TableRow (PeriodKey, Id);\n" +
            "FK на ColumnDef — СКЛАДЕНИЙ (TableDefId, ColumnDefId) → cfg.ColumnDef (TableDefId, Id):\n" +
            "  для цього на ColumnDef має бути HasAlternateKey(x => new { x.TableDefId, x.Id }),\n" +
            "  інакше EF не побудує зв'язок (ТЗ §13.5 п.3);\n" +
            "FK ValueRegistryEntryId → dic.RegistryEntry, ValueUnitId → uom.Unit;\n" +
            "жодного некластерного індексу: усі альтернативні доступи — через doc.DocumentIndexValue.");
}
