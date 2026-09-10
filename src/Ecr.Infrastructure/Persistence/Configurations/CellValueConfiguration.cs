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
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Порожня комірка не має значень; заповнена має рівно одне. Це
        // третій стан із R-B4, і тримається він обмеженням у базі, а не
        // домовленістю в коді.
        builder.ToTable("CellValue", "doc", t => t.HasCheckConstraint(
            "CK_CellValue_Empty",
            "IsEmpty = 0 OR (ValueString IS NULL AND ValueNumeric IS NULL AND ValueDate IS NULL " +
            "AND ValueBool IS NULL AND ValueRegistryEntryId IS NULL AND ValueUnitId IS NULL)"));

        // Партиційний стовпець ПЕРШИЙ у ключі — інакше кластерний індекс не
        // вирівняний зі схемою партиціонування, і TRUNCATE … WITH (PARTITIONS)
        // стає неможливим.
        builder.HasKey(x => new { x.PeriodKeyValue, x.TableRowId, x.ColumnDefId });

        builder.Property(x => x.PeriodKeyValue).HasColumnName("PeriodKey");
        builder.Property(x => x.TableRowId).HasColumnName("TableRowId");
        builder.Property(x => x.ColumnDefId).HasColumnName("ColumnDefId");
        builder.Property(x => x.TableDefId).HasColumnName("TableDefId");

        builder.Property(x => x.ValueString).HasMaxLength(1000);
        builder.Property(x => x.ValueNumeric).HasPrecision(28, 10);
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");

        // DEFAULT-и з іменами за 02a-db-schema.md: безіменне обмеження
        // неможливо прибрати скриптом, не з'ясувавши спершу його
        // випадкове ім'я на конкретній базі.
        builder.Property(x => x.IsCalculated).HasDefaultValue(false, "DF_CellValue_Calc");
        builder.Property(x => x.IsEmpty).HasDefaultValue(false, "DF_CellValue_Empty");

        builder.Ignore(x => x.Address);

        // Складений FK на рядок: комірка фізично не може відірватися від
        // свого рядка і не може перетнути межу партиції.
        builder.HasOne<TableRow>()
               .WithMany()
               .HasForeignKey(x => new { x.PeriodKeyValue, x.TableRowId })
               .HasConstraintName("FK_CellValue_Row")
               .OnDelete(DeleteBehavior.Restrict);

        // Складений FK на колонку (TableDefId, ColumnDefId): саме він не дає
        // комірці потрапити в колонку чужої таблиці. Потребує альтернативного
        // ключа на ColumnDef — він оголошений у ColumnDefConfiguration.
        builder.HasOne<Domain.Entities.Configuration.ColumnDef>()
               .WithMany()
               .HasForeignKey(x => new { x.TableDefId, x.ColumnDefId })
               .HasPrincipalKey(c => new { c.TableDefId, c.Id })
               .HasConstraintName("FK_CellValue_Column")
               .OnDelete(DeleteBehavior.Restrict);

        // ⛔ Q-222: FK_CellValue_Entry (ValueRegistryEntryId → dic.RegistryEntry)
        // з 02a-db-schema.md НАВМИСНО НЕ додано тут — не тому, що не
        // помітили, а тому, що додати правильно не вдалося: ValueRegistryEntryId
        // тут — голий int?, а RegistryEntry : Entity<long> (Id — long,
        // конвертований у int лише для зберігання, RegistryEntryConfiguration.cs:41).
        // EF звіряє СУМІСНІСТЬ CLR-типів залежного й головного ключа ДО
        // конвертації, тож int? проти long не проходить — попри те, що
        // фізично обидва зберігаються як int. Той самий зв'язок в іншому
        // місці (MethodologyConstant.SubstanceEntryId) зроблено правильно:
        // long? + HasConversion<int?>() — той самий CLR-тип, що в
        // RegistryEntry.Id, тому FK_MC_Substance вже працює. Виправити тут
        // так само означало б поміняти CellValue.ValueRegistryEntryId на
        // long? — а це ~18 файлів поза міграціями (RegistryStore,
        // AccessDecisionService, BulkCellLoader, ExcelExporter, ...) на
        // НАЙгарячішому шляху системи (~108 млн рядків/рік). Свідомо
        // залишено як окрема, задокументована прогалина — не мій виклик
        // мовчки поміняти тип на гарячому шляху без окремого рев'ю.
        //
        // FK_CellValue_Unit — не той самий випадок: Unit.Id це голий int
        // (не Entity<T>, без конвертації), тому ValueUnitId (int?) і
        // Unit.Id (int) сумісні напряму.

        builder.HasOne<Domain.Entities.Units.Unit>()
               .WithMany()
               .HasForeignKey(x => x.ValueUnitId)
               .HasConstraintName("FK_CellValue_Unit")
               .OnDelete(DeleteBehavior.Restrict);

        // ⚠ Жодного некластерного індексу. На ~108 млн рядків кожен коштує
        // гігабайти; усі альтернативні доступи йдуть через doc.DocumentIndexValue.
    }
}
