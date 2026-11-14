// src/Ecr.Application/Documents/YearDocumentPeriods.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// ✎ LAND-YEAR L6: місяці року документа річної розкладки (<c>YearWithMonthColumns</c>) — вхід «річних режимів» читань
/// (Issues, журнал аркуша, журнал переходів, проблеми розрахунку): дані місяця m лежать у періоді m, а річний перегляд
/// відкрито на якорі.
/// </summary>
public static class YearDocumentPeriods
{
    /// <summary>
    /// Ключі календаря проєкту в році <paramref name="period"/> за зростанням, якщо документ — річної розкладки
    /// (<see cref="IMonthColumnRouter.RoutesComputedVersion"/>); інакше <c>null</c> (читання — як для одного періоду).
    /// </summary>
    /// <param name="router">Роутер місячних колонок; <c>null</c> (старі збирання) — <c>null</c>.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="period">Період перегляду (його рік задає місяці).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Документ і календар — з кешу роутера (scope); вимкнений роутер — без звернень до бази.</remarks>
    public static async Task<IReadOnlyList<int>?> ResolveAsync(
        IMonthColumnRouter? router, long documentId, PeriodKey period, CancellationToken ct)
    {
        if (router is not { Enabled: true } || !period.IsValid
            || await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is not { } document
            || !router.RoutesComputedVersion(document.TemplateVersionId))
        {
            return null;
        }

        var months = await router.YearPeriodKeysAsync(document.ProjectId, period.Year, ct).ConfigureAwait(false);
        return months.Count == 0 ? null : months;
    }

    /// <summary>
    /// Чи документ річної розкладки (без читання календаря) — для позначки місяця в рядках відповіді й поза річним режимом.
    /// </summary>
    /// <param name="router">Роутер місячних колонок.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<bool> IsYearLayoutAsync(IMonthColumnRouter? router, long documentId, CancellationToken ct)
        => router is { Enabled: true }
           && await router.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is { } document
           && router.RoutesComputedVersion(document.TemplateVersionId);
}
