// src/Ecr.Application/Calculations/CalculatedOwnerRouting.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// ✎ LAND-YEAR L2b: власники колонок <c>Calculated</c> і їхні числа — ОДНЕ правило для сітки
/// (<see cref="CalculatedCellOverlay"/>, зріз таблиці, експорт) і для рушія скрипта (<c>ScriptRuntime</c>, читання
/// результатів інших методологій, план §13.9): що бачить користувач у комірці, те й читає скрипт.
/// </summary>
/// <remarks>
/// Колонка <c>Calculated</c> з ЧУЖИМ власником (версія YearWithMonthColumns: місячна колонка m → період m) показує число
/// з актуального прогону періоду-власника і його екземпляра цієї таблиці, а не число періоду перегляду.
/// </remarks>
public static class CalculatedOwnerRouting
{
    /// <summary>
    /// Колонки <c>Calculated</c> таблиці з чужим власником і їхні періоди-власники для екземпляра перегляду.
    /// </summary>
    /// <param name="router">Роутер власності; <c>null</c> — тест-подвійник без маршрутизації.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="instance">Екземпляр перегляду.</param>
    /// <param name="table">Опис таблиці зі знімка метаданих.</param>
    /// <param name="calculated">Колонки <c>Calculated</c> таблиці, що цікавлять викликача.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Колонка → період-власник; <c>null</c> — власників нема: роутер вимкнено, версія іншої розкладки, копія аркуша,
    /// таблиця поза маршрутизацією (БЕЗ звернень до бази, як і до L2: числа періоду перегляду).
    /// </returns>
    /// <remarks>
    /// Доступ до періоду-власника тут не вирішується — комірки колонок, власник яких поза областю ролі читача, маскує
    /// зріз таблиці далі, після накладення.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<int, int>?> OwnerPeriodsAsync(
        IMonthColumnRouter? router,
        long documentId,
        TableInstanceRef instance,
        TableDef table,
        IReadOnlySet<int> calculated,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(calculated);

        if (router is not { Enabled: true }
            || instance.SheetCopyId is not null
            || !router.RoutesComputedVersion(instance.TemplateVersionId)
            || !router.AppliesToTable(table))
        {
            return null;
        }

        var routing = await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (routing is null)
        {
            return null;
        }

        var foreign = await router
            .ForeignColumnsAsync(routing, table, new PeriodKey(instance.PeriodKey), ct)
            .ConfigureAwait(false);
        var owners = foreign
            .Where(f => calculated.Contains(f.Column.Id))
            .ToDictionary(f => f.Column.Id, f => f.OwnerPeriod.Value);
        return owners.Count == 0 ? null : owners;
    }
}

/// <summary>
/// Числа актуальних прогонів періодів-власників і екземпляри-власники таблиць (див. <see cref="CalculatedOwnerRouting"/>).
/// </summary>
/// <param name="Results">Числа методологій актуальних прогонів періодів-власників.</param>
/// <param name="Instances">Оригінальний екземпляр таблиці в кожному періоді-власнику.</param>
public sealed record CalculatedOwnerResults(
    IReadOnlyList<YearResult> Results, IReadOnlyDictionary<(int PeriodKey, int TableDefId), long> Instances)
{
    /// <summary>Нічого не прочитано.</summary>
    public static CalculatedOwnerResults Empty { get; } = new([], new Dictionary<(int PeriodKey, int TableDefId), long>());

    /// <summary>Екземпляр-власник таблиці в періоді; <c>null</c> — немає.</summary>
    /// <param name="periodKey">Період-власник.</param>
    /// <param name="tableDefId">Таблиця.</param>
    public long? InstanceOf(int periodKey, int tableDefId)
        => Instances.TryGetValue((periodKey, tableDefId), out var id) ? id : null;

    /// <summary>
    /// Один набір запитів на всі таблиці виклику (<see cref="ICalculationYearReader"/>: екземпляри документа за
    /// періоди-власники, далі числа лише екземплярів-власників цих таблиць і числа без екземпляра).
    /// </summary>
    /// <param name="results">Сховище чисел.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="tables">Таблиці з періодами-власниками їхніх колонок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Немає колонок із чужим власником (усі версії, крім YearWithMonthColumns) — жодного запиту. Сховище без читача року
    /// (тест-подвійник) — порожньо: такі колонки лишаються без числа, а не з числом періоду перегляду.
    /// </remarks>
    public static async Task<CalculatedOwnerResults> ReadAsync(
        ICalculationResultStore results,
        long documentId,
        IReadOnlyCollection<(int TableDefId, IReadOnlyCollection<int> OwnerPeriods)> tables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0 || results is not ICalculationYearReader year)
        {
            return Empty;
        }

        var periods = tables.SelectMany(t => t.OwnerPeriods).Distinct().Order().ToList();
        var tableDefIds = tables.Select(t => t.TableDefId).ToHashSet();

        // Оригінальний екземпляр таблиці в кожному періоді-власнику (копії аркушів власниками не бувають).
        var instances = new Dictionary<(int PeriodKey, int TableDefId), long>();
        foreach (var instance in await year.ReadTableInstancesAsync(documentId, periods, ct).ConfigureAwait(false))
        {
            if (instance.SheetCopyId is null && tableDefIds.Contains(instance.TableDefId))
            {
                instances.TryAdd((instance.PeriodKey, instance.TableDefId), instance.TableInstanceId);
            }
        }

        if (instances.Count == 0)
        {
            return new CalculatedOwnerResults([], instances);
        }

        var facts = await year
            .ReadYearAsync(
                new CalculationYearReadRequest(
                    documentId, periods, [.. instances.Values], [], IncludeRows: false, SliceResults: true, Aggregate: false),
                ct)
            .ConfigureAwait(false);

        return new CalculatedOwnerResults(facts.Results, instances);
    }
}
