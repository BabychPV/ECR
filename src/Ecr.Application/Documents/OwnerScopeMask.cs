using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Q-17 (умови вмикання прапора): колонки таблиць «місяці в колонках», значення яких читач бачити НЕ МОЖЕ, бо
/// ПЕРІОД ВЛАСНИКА (місячний m — період m; введений не-місячний — якір року) поза областю його ролі за періодами
/// (D-214), хоча період перегляду — в області.
/// </summary>
/// <remarks>
/// ⚠ Використовують канали, що віддають значення зрізу ПОЗА <c>GetTableSliceHandler</c> (там своя перевірка по таблиці):
/// вивантаження документа. Прапор вимкнено або проєкт не місячний — порожньо БЕЗ звернень до бази.
/// </remarks>
internal static class OwnerScopeMask
{
    /// <summary>Усі такі колонки документа за період перегляду.</summary>
    /// <param name="router">Роутер власності; <c>null</c> — Q-17 не діє.</param>
    /// <param name="metadata">Знімки метаданих.</param>
    /// <param name="anyPeriod">Межі читання документа (без періоду).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="viewPeriod">Період перегляду (вивантаження).</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<HashSet<int>> BlockedColumnIdsAsync(
        IMonthColumnRouter? router,
        IMetadataCache? metadata,
        DocumentReadScope anyPeriod,
        long documentId,
        PeriodKey viewPeriod,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(anyPeriod);

        var blocked = new HashSet<int>();
        if (router is not { Enabled: true } || metadata is null)
        {
            return blocked;
        }

        var document = await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return blocked;
        }

        var snapshot = await metadata.GetAsync(document.TemplateVersionId, ct).ConfigureAwait(false);
        foreach (var table in snapshot.Sheets.Where(s => !s.IsDeleted).SelectMany(s => s.Tables))
        {
            if (!router.AppliesToTable(table))
            {
                continue;
            }

            foreach (var foreign in await router.ForeignColumnsAsync(document, table, viewPeriod, ct).ConfigureAwait(false))
            {
                if (!anyPeriod.InPeriod(foreign.OwnerPeriod).CanReadColumn(foreign.Column.Id))
                {
                    blocked.Add(foreign.Column.Id);
                }
            }
        }

        return blocked;
    }
}
