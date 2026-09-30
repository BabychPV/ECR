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
        builder.Property(x => x.ValueNumeric).HasPrecision(34, 16);
        builder.Property(x => x.ValueDate).HasColumnType("datetime2(3)");

        // ⛔ Директива registry-lookup, PR A1. Той самий прийом, що вже working
        // для FK_MC_Substance (MethodologyConstant.SubstanceEntryId): long? у
        // CLR, HasConversion<int?>() у зберіганні — фізична колонка лишається
        // int (RegistryEntry.Id теж конвертований у int, RegistryEntryConfiguration.cs:41),
        // а CLR-типи по обидва боки FK тепер збігаються (long), тож EF дозволяє
        // зіставити зовнішній ключ. Раніше тут був голий int? — CLR-типи не
        // збігалися з RegistryEntry.Id (long), і FK додати було неможливо
        // (детальний розбір — Q-222, знятий цим фіксом).
        builder.Property(x => x.ValueRegistryEntryId).HasConversion<int?>();

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

        // ⛔ Директива registry-lookup, PR A1 (Q-222, закрито). Комірка
        // Lookup-типу фізично не може посилатися на неіснуючий запис
        // довідника — той самий захист, що FK_CellValue_Column уже дає
        // колонці. RESTRICT, а не CASCADE: видалення запису довідника, на
        // який посилаються дані, — окрема бізнес-помилка
        // (`RegistryStore.CountReferencesAsync`, ФВ-8.13), не мовчазне
        // видалення чужих комірок.
        builder.HasOne<Domain.Entities.Dictionaries.RegistryEntry>()
               .WithMany()
               .HasForeignKey(x => x.ValueRegistryEntryId)
               .HasConstraintName("FK_CellValue_Entry")
               .OnDelete(DeleteBehavior.Restrict);

        // FK_CellValue_Unit — той самий захист для одиниці вимірювання:
        // Unit.Id це голий int (не Entity<T>, без конвертації), тому
        // ValueUnitId (int?) і Unit.Id (int) сумісні напряму.
        builder.HasOne<Domain.Entities.Units.Unit>()
               .WithMany()
               .HasForeignKey(x => x.ValueUnitId)
               .HasConstraintName("FK_CellValue_Unit")
               .OnDelete(DeleteBehavior.Restrict);

        // ⚠ Єдиний некластерний індекс — під підрахунок заповненості
        // `tables/status` (TableFillStore): вузький (без значень), лише введені
        // комірки. Виміряно на 2.06 млн комірок: читання 675 → 471, CPU
        // 109–234 → 63–93 мс, ~44 МБ. PeriodKey першим — вимога 07 (THROW 50031);
        // розміщення на ps_ByPeriodKey задає міграція BE21CellValueFillIndex.
        builder.HasIndex(x => new { x.PeriodKeyValue, x.TableRowId, x.ColumnDefId })
               .HasDatabaseName("IX_CellValue_Fill")
               .HasFilter("[IsCalculated] = 0");

        // ⛔ R-05, B-18: «Where used» довідника й одиниці та перевірка
        // видалення запису (`RegistryStore.CountReferencesAsync`) питають
        // «чи посилається на це хоч одна комірка» — глобально, БЕЗ PeriodKey
        // (див. той метод: ключ партиції там змінив би відповідь). Без індексу
        // це скан усієї doc.CellValue: на стенді 2.06 млн рядків — 7.4 с і
        // 3 млн читань dic.RegistryEntry. Фільтр — лише комірки з посиланням:
        // індекс крихітний. Вирівняний по ps_ByPeriodKey (PeriodKey — неявний
        // ключ розділу; розміщення задає міграція B18HotPathIndexes), тож
        // TRUNCATE … WITH (PARTITIONS) архівації не ламається.
        builder.HasIndex(x => x.ValueRegistryEntryId)
               .HasDatabaseName("IX_CellValue_RegistryEntry")
               .HasFilter("[ValueRegistryEntryId] IS NOT NULL");

        builder.HasIndex(x => x.ValueUnitId)
               .HasDatabaseName("IX_CellValue_Unit")
               .HasFilter("[ValueUnitId] IS NOT NULL");
    }
}
