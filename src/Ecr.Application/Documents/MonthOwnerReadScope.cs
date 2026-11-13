// src/Ecr.Application/Documents/MonthOwnerReadScope.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Q-17: межа читання колонок з ІНШИМ власником (таблиця «місяці в колонках»): значення такої колонки читач бачить лише
/// тоді, коли область його ролі містить ПЕРІОД ВЛАСНИКА (<c>InPeriod(власник)</c>), а не лише період перегляду.
/// </summary>
/// <remarks>
/// Одне правило на всіх читачів логічного зрізу (<see cref="ICellStore"/> за прапора віддає значення власника): зріз
/// таблиці (<see cref="GetTableSliceHandler"/>) і перегляд прив'язки дозволу (B44). Інакше другий читач став би бічним
/// ходом до значень, які перший маскує.
/// </remarks>
internal static class MonthOwnerReadScope
{
    /// <summary>
    /// Колонки таблиці з іншим власником, період якого НЕ в області ролі читача.
    /// </summary>
    /// <param name="router">Роутер власності; <c>null</c> — лише тести, що конструюють обробник вручну.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="table">Таблиця.</param>
    /// <param name="viewPeriod">Період екземпляра перегляду.</param>
    /// <param name="anyPeriod">Область читання документа БЕЗ звуження періодом (<c>ReadScopeAsync</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Прапор вимкнено, таблиця поза маршрутизацією або власників нема — порожньо БЕЗ звернень до бази.</remarks>
    public static async Task<HashSet<int>> BlockedColumnsAsync(
        IMonthColumnRouter? router, long documentId, TableDef table, int viewPeriod, DocumentReadScope anyPeriod, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(anyPeriod);

        var blocked = new HashSet<int>();
        if (router is not { Enabled: true } || !router.AppliesToTable(table))
        {
            return blocked;
        }

        var document = await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return blocked;
        }

        // ⚠ Q-17 перф: InPeriod будує НОВУ область (індекси по всій версії шаблону) — на кожну чужу колонку
        // (до сотень) це ~70 мс на зріз. Область на період-власник одна, колонки — з її власним кешем.
        var scopeByOwner = new Dictionary<PeriodKey, DocumentReadScope>();
        foreach (var foreign in await router
                     .ForeignColumnsAsync(document, table, new PeriodKey(viewPeriod), ct)
                     .ConfigureAwait(false))
        {
            if (!scopeByOwner.TryGetValue(foreign.OwnerPeriod, out var ownerScope))
            {
                scopeByOwner[foreign.OwnerPeriod] = ownerScope = anyPeriod.InPeriod(foreign.OwnerPeriod);
            }

            if (!ownerScope.CanReadColumn(foreign.Column.Id))
            {
                blocked.Add(foreign.Column.Id);
            }
        }

        return blocked;
    }
}
