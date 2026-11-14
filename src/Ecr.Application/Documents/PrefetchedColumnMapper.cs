// src/Ecr.Application/Documents/PrefetchedColumnMapper.cs

using Ecr.Application.Ports;

namespace Ecr.Application.Documents;

/// <summary>
/// Переклад Id колонок (C1), прочитаний ОДНИМ зверненням на весь набір методологій запиту; далі кожна версія
/// методології отримує свою підмножину без походу в базу.
/// </summary>
/// <remarks>
/// ⚠ Коректно лише тому, що переклад Id не залежить від інших Id у наборі: <see cref="IColumnPathMapper.MapToVersionAsync"/>
/// шукає відповідник кожної колонки за її власним шляхом «аркуш → таблиця → колонка». Тому підмножина спільної
/// відповіді — рівно те, що дав би окремий виклик із цією підмножиною. Живе один запит (екземпляр на виклик gate),
/// кешем не є.
/// </remarks>
internal sealed class PrefetchedColumnMapper : IColumnPathMapper
{
    private readonly IColumnPathMapper _inner;
    private readonly int _targetTemplateVersionId;
    private readonly IReadOnlyDictionary<int, int> _map;
    private readonly HashSet<int> _asked;

    private PrefetchedColumnMapper(
        IColumnPathMapper inner, int targetTemplateVersionId, IReadOnlyDictionary<int, int> map, HashSet<int> asked)
    {
        _inner = inner;
        _targetTemplateVersionId = targetTemplateVersionId;
        _map = map;
        _asked = asked;
    }

    /// <summary>Читає переклад усіх названих колонок одним викликом.</summary>
    /// <param name="inner">Справжній переклад; <c>null</c> — перекладу немає (Id локальні), повертається <c>null</c>.</param>
    /// <param name="targetTemplateVersionId">Версія шаблону документа.</param>
    /// <param name="columnIds">Усі Id, які далі питатимуться (з повторами).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Переклад для подальших викликів або <c>null</c>, якщо <paramref name="inner"/> — <c>null</c>.</returns>
    public static async Task<IColumnPathMapper?> CreateAsync(
        IColumnPathMapper? inner, int targetTemplateVersionId, IEnumerable<int> columnIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnIds);

        if (inner is null)
        {
            return null;
        }

        var asked = columnIds.ToHashSet();
        var map = asked.Count == 0
            ? new Dictionary<int, int>()
            : await inner.MapToVersionAsync([.. asked], targetTemplateVersionId, ct).ConfigureAwait(false);

        return new PrefetchedColumnMapper(inner, targetTemplateVersionId, map, asked);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, int>> MapToVersionAsync(
        IReadOnlyCollection<int> sourceColumnIds, int targetTemplateVersionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceColumnIds);

        // Інша версія або Id, якого не читали наперед, — чесно до справжнього перекладу (не вигадувати відсутність).
        if (targetTemplateVersionId != _targetTemplateVersionId || !sourceColumnIds.All(_asked.Contains))
        {
            return _inner.MapToVersionAsync(sourceColumnIds, targetTemplateVersionId, ct);
        }

        IReadOnlyDictionary<int, int> subset = sourceColumnIds
            .Distinct()
            .Where(_map.ContainsKey)
            .ToDictionary(id => id, id => _map[id]);

        return Task.FromResult(subset);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, int>> GetTemplateVersionsOfTablesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
        => _inner.GetTemplateVersionsOfTablesAsync(tableDefIds, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> LineageAsync(
        IReadOnlyCollection<int> columnIds, CancellationToken ct)
        => _inner.LineageAsync(columnIds, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> TableLineageAsync(
        IReadOnlyCollection<int> tableIds, CancellationToken ct)
        => _inner.TableLineageAsync(tableIds, ct);
}
