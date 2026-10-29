using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 перф: форма запиту читання власників не залежить від ЧИСЛА колонок. Колишній відбір
/// <c>ColumnDefId IN (≈200 параметрів)</c> давав у 5–9 разів повільніший зріз (лінійна перевірка списку на комірку,
/// 200 пошуків на рядок, ~0,5 с компіляції плану). Тест детермінований: дивиться на згенерований SQL, не на час.
/// </summary>
public sealed class MonthOwnerCellsQueryShapeTests
{
    private static EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(shape-test-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);

    [Fact]
    [Trait("Decision", "Q-17")]
    public void Запит_власників_не_несе_списку_колонок_і_число_параметрів_не_росте_з_колонками()
    {
        using var db = Context();
        var periods = Enumerable.Range(202601, 12).ToList();

        var sql = MonthRoutedCellStore.OwnerCellsQuery(db, 1L, periods, [724], [202601]).ToQueryString();

        Assert.DoesNotContain("[ColumnDefId] IN", sql, StringComparison.Ordinal);
        Assert.Contains("[PeriodKey] IN", sql, StringComparison.Ordinal);

        // Число параметрів залежить від періодів/таблиць/якорів, а не від колонок (їх тут немає взагалі).
        var parameterCount = sql.Split('\n').Count(l => l.StartsWith("DECLARE @", StringComparison.Ordinal));
        Assert.True(parameterCount <= (2 * periods.Count) + 4,$"параметрів {parameterCount}: {sql}");
    }

    /// <summary>
    /// L6 перф: відбір «якір АБО EXISTS(ColumnDef)» на кожній комірці SQL Server виконував через Table Spool
    /// (~111 тис. логічних читань робочої таблиці на запит, 150–3400 мс; звіт S07 × 12 читачів). Форма має лишатись
    /// двома гілками UNION ALL зі звичайним з'єднанням за PK <c>cfg.ColumnDef</c>, без корельованого EXISTS.
    /// </summary>
    [Fact]
    [Trait("Decision", "Q-17")]
    public void Запит_власників_без_корельованого_EXISTS_і_двома_гілками_UNION_ALL()
    {
        using var db = Context();

        var sql = MonthRoutedCellStore.OwnerCellsQuery(
            db, 1L, Enumerable.Range(202601, 12).ToList(), [724], [202601]).ToQueryString();

        Assert.DoesNotContain("EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", sql, StringComparison.Ordinal);
        Assert.Contains("[cfg].[ColumnDef]", sql, StringComparison.Ordinal);
    }
}
