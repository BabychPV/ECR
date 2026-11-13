using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Land-year L0 (<c>.sync-local/reports/LAND-YEAR-plan-1112.md</c> §a.3): псевдонім аргументу методології на місячну
/// колонку. Для запуску періоду місяця <see cref="MonthNumber"/> аргумент <c>@<see cref="Alias"/></c> бере значення
/// колонки <see cref="ColumnDefId"/> тієї самої таблиці.
/// </summary>
/// <remarks>
/// ⚠ Окрема таблиця, а не поле колонки: одна місячна колонка обслуговує два псевдоніми (T9a_19). PT01 створює лише
/// таблицю; запис (bulk <c>PUT /template-versions/{id}/month-input-aliases</c>, лише чернетка), перевірка публікації,
/// клон версії й читання — окремий крок L0 разом із контрактом (рішення координатора 13.11, Q3).
/// </remarks>
public sealed class MonthInputAlias : Entity<int>
{
    private MonthInputAlias() { }

    /// <summary>Створює псевдонім.</summary>
    /// <param name="tableDefId">Таблиця версії.</param>
    /// <param name="alias">Ім'я аргументу (колишній код селектора / формули).</param>
    /// <param name="monthNumber">Місяць 1..12.</param>
    /// <param name="columnDefId">Місячна колонка тієї самої таблиці.</param>
    public MonthInputAlias(int tableDefId, string alias, byte monthNumber, int columnDefId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentOutOfRangeException.ThrowIfLessThan(monthNumber, (byte)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(monthNumber, (byte)12);

        TableDefId = tableDefId;
        Alias = alias;
        MonthNumber = monthNumber;
        ColumnDefId = columnDefId;
    }

    /// <summary>Таблиця.</summary>
    public int TableDefId { get; private set; }

    /// <summary>Ім'я аргументу.</summary>
    public string Alias { get; private set; } = null!;

    /// <summary>Місяць 1..12.</summary>
    public byte MonthNumber { get; private set; }

    /// <summary>Колонка-джерело.</summary>
    public int ColumnDefId { get; private set; }
}
