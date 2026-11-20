using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Workflow;

/// <summary>
/// Замикання таблиць застарілості для методологій-скриптів (MS, план §13.1, §13.9): які ще таблиці, окрім
/// власних таблиць аркуша, можуть зробити число методології аркуша застарілим.
/// </summary>
/// <remarks>
/// ⛔ Методологія-скрипт читає не лише власну таблицю: <c>table T = …</c> дає їй чужу таблицю, а колонка
/// <c>Calculated</c> — результат іншої методології, чий вхід лежить у ще одній таблиці. Правка в будь-якій з них
/// робить число методології-читача застарілим так само, як правка власної таблиці. Рахується разом із замиканням
/// за графом формул шаблону (<c>SubmitSheetHandler.FreshnessTablesAsync</c>) — це лише додаток до нього.
/// </remarks>
public static class ScriptFreshnessScope
{
    /// <summary>
    /// Додає в <paramref name="scope"/> таблиці, які читають скрипти методологій аркуша, і таблиці виходів
    /// методологій-джерел результатів (транзитивно).
    /// </summary>
    /// <param name="store">Порт читань рушія скриптів; <c>null</c> — нічого не додається.</param>
    /// <param name="snapshot">Структура версії шаблону документа: код таблиці → її Id.</param>
    /// <param name="scope">Таблиці замикання; доповнюється на місці.</param>
    /// <param name="boundMethodologyIds">Методології, прив'язані до таблиць аркуша.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task ExtendAsync(
        IScriptRuntimeStore? store,
        TemplateVersionSnapshot snapshot,
        ISet<int> scope,
        IReadOnlyCollection<int> boundMethodologyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(boundMethodologyIds);

        if (store is null || boundMethodologyIds.Count == 0)
        {
            return;
        }

        var closure = await store.GetSourceClosureAsync(boundMethodologyIds, ct).ConfigureAwait(false);
        var readCodes = await store.GetReadTableCodesAsync(closure, ct).ConfigureAwait(false);

        // Таблиці виходів джерел — там лежать входи джерел; для самих методологій аркуша вони вже в `scope`.
        var boundTables = closure.Count > boundMethodologyIds.Count
            ? await store.GetBoundTableDefIdsAsync(closure, ct).ConfigureAwait(false)
            : [];

        if (readCodes.Count == 0 && boundTables.Count == 0)
        {
            return;
        }

        var codes = new HashSet<string>(readCodes, StringComparer.OrdinalIgnoreCase);
        foreach (var table in snapshot.Sheets.SelectMany(s => s.Tables).Where(t => !t.IsDeleted))
        {
            if (codes.Contains(table.Code) || boundTables.Contains(table.Id))
            {
                scope.Add(table.Id);
            }
        }
    }
}
