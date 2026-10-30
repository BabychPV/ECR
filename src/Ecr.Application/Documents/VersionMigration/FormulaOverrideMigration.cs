using Ecr.Application.Documents.FormulaOverride;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// ✎ D-461: що стає з ручними значеннями замість формул при переносі документа на іншу версію шаблону.
/// </summary>
/// <remarks>
/// ⚠ Правило (рішення людини 30.10): ручне значення на формулі, що в ЦІЛЬОВІЙ версії і далі дозволяє ручне значення,
/// переїжджає як ручне (позначка йде за колонкою разом зі значенням, <c>DocumentVersionMigrationStore</c>); інакше —
/// <c>OverrideLost</c> у звіті сухого прогону, і при застосуванні позначка знімається, а комірка знову стає обчисленою
/// (перерахунок проєкту рахує її за формулою; журнал — <c>FormulaRestored</c> з причиною «Version migration»).
/// <para>
/// ⚠ Це НЕ <c>dataLoss</c> і перенос не блокує: значення не зникає без сліду, його замінює формула нової версії.
/// </para>
/// </remarks>
public static class FormulaOverrideMigration
{
    /// <summary>Причина рядків журналу, коли перенос повертає формулу.</summary>
    public const string Reason = "Version migration";

    /// <summary>Вид пункту звіту.</summary>
    public const string ItemKind = "OverrideLost";

    /// <summary>
    /// Позначки, що НЕ переживуть перенос (формула цільової версії не дозволяє ручне значення, або колонки немає).
    /// </summary>
    /// <param name="marks">Позначки проєкту.</param>
    /// <param name="targetColumnOf">Колонка цільової версії для колонки позначки; <c>null</c> — відповідника немає.</param>
    /// <param name="target">Знімок цільової версії.</param>
    public static List<ProjectFormulaOverride> Lost(
        IReadOnlyList<ProjectFormulaOverride> marks,
        Func<int, int?> targetColumnOf,
        TemplateVersionSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(targetColumnOf);
        ArgumentNullException.ThrowIfNull(target);

        var policies = new Dictionary<int, FormulaOverridePolicy>();
        var tables = target.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        var lost = new List<ProjectFormulaOverride>();

        foreach (var mark in marks)
        {
            if (targetColumnOf(mark.Address.ColumnDefId) is not { } columnId
                || !target.ColumnsById.TryGetValue(columnId, out var column)
                || !tables.TryGetValue(column.TableDefId, out var table))
            {
                lost.Add(mark);
                continue;
            }

            if (!policies.TryGetValue(table.Id, out var policy))
            {
                policies[table.Id] = policy = FormulaOverridePolicy.For(table);
            }

            if (!policy.Allows(mark.RowKey, column))
            {
                lost.Add(mark);
            }
        }

        return lost;
    }

    /// <summary>Пункти звіту: по одному на колонку вихідної версії (шлях <c>SHEET.TABLE.COLUMN</c>) з кількістю.</summary>
    public static IEnumerable<VersionMigrationItem> Items(
        IReadOnlyList<ProjectFormulaOverride> lost, TemplateVersionSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(lost);
        ArgumentNullException.ThrowIfNull(source);

        foreach (var group in lost.GroupBy(m => m.Address.ColumnDefId).OrderBy(g => g.Key))
        {
            var path = source.ColumnsById.TryGetValue(group.Key, out var column)
                ? PathOf(source, column)
                : group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            yield return new VersionMigrationItem(path, ItemKind, ChangeClass.Presentation, group.LongCount());
        }
    }

    private static string PathOf(TemplateVersionSnapshot snapshot, ColumnDef column)
    {
        foreach (var sheet in snapshot.Sheets)
        {
            if (sheet.Tables.FirstOrDefault(t => t.Id == column.TableDefId) is { } table)
            {
                return $"{sheet.Code}.{table.Code}.{column.Code}";
            }
        }

        return column.Code;
    }
}
