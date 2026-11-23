using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// ✎ P-1123-4: заяви перепідписання одиниць (<see cref="VersionMigrationUnitRelabel"/>) із мапи переносу → словник
/// планувальника «шлях <c>SHEET.TABLE.COLUMN</c> вихідної версії → Id одиниць».
/// </summary>
/// <remarks>
/// ⛔ Перепідписання — НЕ перерахунок: значення комірок переносяться як є (байт у байт), змінюється лише одиниця колонки.
/// Тому дозволено лише з БЕЗРОЗМІРНОЇ одиниці (розмірність <c>Dimensionless</c>, напр. <c>one</c>) — «число вже було в
/// цій одиниці, підпису не було». Перепідписання між двома фізичними одиницями (кг → т) означало б інше число і
/// лишається <c>guardedWithData</c>. Колонка, якої у вихідній версії немає, — відмова (заява не може тихо не спрацювати).
/// </remarks>
public static class VersionMigrationUnitRelabels
{
    /// <summary>Код розмірності безрозмірних одиниць (<c>uom.Dimension</c>).</summary>
    public const string DimensionlessDimension = "Dimensionless";

    /// <summary>Розв'язує заяви; <c>null</c> — заяв немає.</summary>
    /// <param name="renames">Мапа переносу із запиту.</param>
    /// <param name="source">Знімок вихідної версії.</param>
    /// <param name="units">Каталог одиниць; <c>null</c> при наявних заявах — відмова.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-SCHM-0422</c>, ключ <c>migrateRenameMapInvalid</c>.</exception>
    public static async Task<IReadOnlyDictionary<string, VersionMigrationResolvedRelabel>?> ResolveAsync(
        IReadOnlyList<VersionMigrationTableRename>? renames, TemplateVersionSnapshot source, IUnitCatalog? units, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var declared = renames?.Where(r => r?.UnitRelabels is { Count: > 0 }).ToList() ?? [];
        if (declared.Count == 0)
        {
            return null;
        }

        if (units is null)
        {
            throw Invalid("Перепідписання одиниць недоступне: каталог одиниць не підключено.");
        }

        var catalog = await units.GetAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, VersionMigrationResolvedRelabel>(StringComparer.Ordinal);
        foreach (var rename in declared)
        {
            var tables = source.Sheets.Where(s => !s.IsDeleted && (rename.SheetCode is null || string.Equals(s.Code.ToString(), rename.SheetCode, StringComparison.Ordinal)))
                .SelectMany(s => s.Tables.Where(t => !t.IsDeleted && string.Equals(t.Code.ToString(), rename.FromTableCode, StringComparison.Ordinal)).Select(t => (Sheet: s, Table: t)))
                .ToList();
            foreach (var relabel in rename.UnitRelabels!)
            {
                if (!catalog.Units.TryGetValue(relabel.FromUnitCode, out var from)
                    || !string.Equals(from.DimensionCode, DimensionlessDimension, StringComparison.Ordinal))
                {
                    throw Invalid($"«{rename.FromTableCode}.{relabel.ColumnCode}»: перепідписати можна лише безрозмірну одиницю, а «{relabel.FromUnitCode}» не така.");
                }

                if (!catalog.Units.TryGetValue(relabel.ToUnitCode, out var to))
                {
                    throw Invalid($"«{rename.FromTableCode}.{relabel.ColumnCode}»: одиниці «{relabel.ToUnitCode}» в каталозі немає.");
                }

                var hits = tables.Where(x => x.Table.Columns.Any(c => !c.IsDeleted && string.Equals(c.Code.ToString(), relabel.ColumnCode, StringComparison.Ordinal))).ToList();
                if (hits.Count == 0)
                {
                    throw Invalid($"Перепідписання одиниці: колонки «{rename.FromTableCode}.{relabel.ColumnCode}» у вихідній версії немає.");
                }

                foreach (var (sheet, table) in hits)
                {
                    result[$"{sheet.Code}.{table.Code}.{relabel.ColumnCode}"] =
                        new VersionMigrationResolvedRelabel(from.Id, to.Id, from.Code, to.Code);
                }
            }
        }

        return result;
    }

    private static BusinessRuleException Invalid(string message)
        => new(
            ErrorCodes.GuardedChangeWithoutStrategy,
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-SCHM-0422.migrateRenameMapInvalid",
                ["maxTableRenames"] = VersionMigrationRenames.MaxTableRenames.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxColumnRenames"] = VersionMigrationRenames.MaxColumnRenames.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}
