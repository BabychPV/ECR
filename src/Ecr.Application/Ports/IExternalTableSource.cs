// src/Ecr.Application/Ports/IExternalTableSource.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання ОДНІЄЇ таблиці-довідника зовнішнього SQL-джерела цілком за налаштованим запитом (FLERT, склад газу,
/// <c>D-476</c>): не часовий ряд і не атрибути елементів, а знімок довідника, який ECR дзеркалить.
/// </summary>
/// <remarks>
/// ⛔ Лише ЧИТАННЯ. Реалізація виконує запит у транзакції, яку завжди відкочує, і в джерело не пише нічого
/// (<c>D-44</c>; сторож <c>FlertReadOnlyAdapterTests</c>). Текст запиту — конфігурація, типового тексту немає
/// (як у <c>SqlDataSource</c>): ненаштований запит — відмова <c>ECR-INT-0503</c> <c>.queryNotConfigured</c>.
/// </remarks>
public interface IExternalTableSource
{
    /// <summary>Транспорт, який реалізує адаптер.</summary>
    public ExternalTransport Transport { get; }

    /// <summary>Виконує налаштований запит <paramref name="queryKey"/> джерела і повертає всі рядки (до стелі).</summary>
    /// <param name="dataSourceId">Джерело (<c>ext.DataSource</c>).</param>
    /// <param name="queryKey">Ключ запиту в каналі налаштувань, напр. <c>Sql:CompositionQuery</c>.</param>
    /// <param name="maxRows">Стеля рядків; більше — <see cref="ExternalTable.Truncated"/>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// <c>ECR-INT-0503</c> — джерело недоступне, ненаштоване чи відхилене політикою адреси.
    /// </exception>
    public Task<ExternalTable> ReadTableAsync(int dataSourceId, string queryKey, int maxRows, CancellationToken ct);
}

/// <summary>Результат <see cref="IExternalTableSource.ReadTableAsync"/>.</summary>
/// <param name="Columns">Імена колонок результату в порядку запиту.</param>
/// <param name="Rows">Рядки; значення в порядку <paramref name="Columns"/>, <c>DBNull</c> → <c>null</c>.</param>
/// <param name="Truncated">За стелею лишився ще хоч один рядок: знімок НЕПОВНИЙ.</param>
public sealed record ExternalTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);
