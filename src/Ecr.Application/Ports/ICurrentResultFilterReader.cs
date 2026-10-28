// src/Ecr.Application/Ports/ICurrentResultFilterReader.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Звужене читання чисел актуального прогону: лише ті результати, які можуть дістатися комірок заданих таблиць
/// (Q-17, наборове читання зрізу API).
/// </summary>
/// <remarks>
/// ⛔ Це ЗВУЖЕННЯ <see cref="ICalculationResultStore.ReadCurrentAsync"/>, а не інше правило: ті самі прогони (власний
/// документний, інакше проєктний), те саме упорядкування, та сама стеля <c>MaxResults</c> (50 000) —
/// але лише для відфільтрованого набору. Фільтр — НАДМНОЖИНА того, що вибирає накладення
/// (<c>CalculatedCellOverlay</c>): воно й надалі повторює точну умову (код виходу, версія, екземпляр) у пам'яті.
/// <para>
/// Окремий порт (а не новий член <see cref="ICalculationResultStore"/>): тест-подвійники старого порту не знають про
/// звуження, і накладення, не знайшовши цього порту в реалізації, читає повний набір, як до Q-17.
/// </para>
/// <para>
/// ⚠ Навіщо: на Land результатів документа за період — p50 1 440, p95 95 920, і майже всі належать ІНШИМ таблицям
/// документа (зріз однієї таблиці вичитував їх усі, сортував і переносив у памʼять; понад 50 000 — відмова 422).
/// </para>
/// </remarks>
public interface ICurrentResultFilterReader
{
    /// <summary>Числа актуального прогону документа за період, звужені до <paramref name="filter"/>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="filter">Що саме може знадобитися накладенню.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Результати в порядку рядка й коду виходу, як у <see cref="ICalculationResultStore.ReadCurrentAsync"/>.</returns>
    public Task<IReadOnlyList<CalculationResultRow>> ReadCurrentMatchingAsync(
        long documentId, int periodKey, CurrentResultFilter filter, CancellationToken ct);
}

/// <summary>Звуження читання чисел: результат потрібен, лише якщо він збігається з УСІМА трьома множинами.</summary>
/// <param name="TableInstanceIds">
/// Екземпляри таблиць зрізу; результат без екземпляра (<c>TableInstanceId IS NULL</c>, до D-401) потрібен завжди.
/// </param>
/// <param name="OutputCodes">Коди виходів прив'язок таблиць (порівняння без урахування регістру, як у накладенні).</param>
/// <param name="MethodologyVersionIds">Версії методологій прив'язок таблиць.</param>
public sealed record CurrentResultFilter(
    IReadOnlyCollection<long> TableInstanceIds,
    IReadOnlyCollection<string> OutputCodes,
    IReadOnlyCollection<int> MethodologyVersionIds);
