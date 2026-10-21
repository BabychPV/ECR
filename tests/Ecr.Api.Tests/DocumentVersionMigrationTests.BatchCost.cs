// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.BatchCost.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Hotfix «integ-migrate-batch» + Б1 (2026-10-21): перенос 4,87 млн комірок був квадратичним — кожна пачка
/// <c>UPDATE TOP (@batch)</c> з предикатом без ключа перечитувала кластерний індекс крізь уже перенесені рядки.
/// Сторож: сумарні ЛОГІЧНІ читання переносу ростуть лінійно з обсягом (N → 4N дає ≈ ×4, а не ×16).
/// </summary>
/// <remarks>
/// ⚠ Тест не міряє секунди (плаваючий): він порівнює логічні читання сесії переносу (<c>sys.dm_exec_sessions</c>),
/// які фіксує <see cref="DocumentVersionMigrationStore.RangeObserver"/> після коміту кожного діапазону.
/// Мутаційний доказ: прибрати з <c>RangeCommandText</c> предикат <c>TableRowId BETWEEN @lo AND @hi</c> — кожен
/// діапазон читає весь період, відношення стає ≈ ×16, тест червоніє.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private const int CostBaseRows = 5_000;      // 15 тис. комірок
    private const int CostRangeRows = 250;       // 20 діапазонів проти 80: квадратичний доданок помітний на тлі сталих витрат діапазону

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Вартість_переносу_комірок_росте_лінійно_з_обсягом_а_не_квадратично()
    {
        var small = await MeasureCellRangesAsync(CostBaseRows).ConfigureAwait(true);
        var large = await MeasureCellRangesAsync(CostBaseRows * 4).ConfigureAwait(true);

        Assert.True(small.Count >= 4, $"Замало діапазонів для висновку: {small.Count}.");
        Assert.True(large.Count >= small.Count * 3, $"4N мало дати ≈ ×4 діапазонів: {small.Count} -> {large.Count}.");

        // ⛔ Головне: діапазон змінює лише СВОЇ рядки (3 комірки + сам рядок на кожен рядок TableRow), а кожна комірка
        // зачеплена рівно один раз. Без предиката за ключем перший діапазон переносить усе, решта — порожні: вартість
        // діапазону перестає залежати від його розміру (мутація: прибрати `TableRowId BETWEEN @lo AND @hi` — червоніє).
        foreach (var (stats, volumeRows) in new[] { (small, CostBaseRows), (large, CostBaseRows * 4) })
        {
            Assert.All(stats, r => Assert.True(
                r.Rows <= (CostRangeRows * 4L) + 8, $"Діапазон {r.Index} змінив {r.Rows} рядків при {CostRangeRows} рядках TableRow."));
            Assert.Equal((volumeRows * 4L) + 3, stats.Sum(r => r.Rows));
        }

        var smallTotal = small.Sum(r => r.LogicalReads);
        var largeTotal = large.Sum(r => r.LogicalReads);
        var ratio = largeTotal / (double)smallTotal;
        Console.WriteLine(FormattableString.Invariant(
            $"RANGE-COST ranges={small.Count}/{large.Count} reads={smallTotal}/{largeTotal} ratio={ratio:F2}"));

        // ⛔ Предмет тесту: у 4 рази більше даних — не більш ніж у ~6 разів більше читань (квадратична — ×16).
        Assert.True(ratio <= 6.0, $"Читання ростуть швидше за обсяг: ×{ratio:F1} при ×4 даних ({smallTotal} -> {largeTotal}).");

        // Діапазон не дорожчає з просуванням: пізні не більш ніж удвічі дорожчі за ранні (повні діапазони, без хвоста).
        var full = large.Take(large.Count - 1).Select(r => r.LogicalReads).ToList();
        var quarter = Math.Max(1, full.Count / 4);
        var early = full.Take(quarter).Average();
        var late = full.Skip(full.Count - quarter).Average();
        Assert.True(late <= early * 2, $"Вартість діапазону росте: ранні {early:F0} читань, пізні {late:F0}.");
    }

    /// <summary>Переносить <paramref name="volumeRows"/> рядків × 3 комірки й повертає статистику діапазонів комірок.</summary>
    private async Task<List<MigrationRangeStat>> MeasureCellRangesAsync(int volumeRows)
    {
        // ⚠ Свій період: у спільному 202601 тисячі екземплярів інших тестів дають сталу вартість прелюдії кожного
        // діапазону (~20 тис. читань), яка ховає різницю між лінійним і квадратичним переносом.
        var s = await ArrangeAsync(Target.OnlyLabels, periodKey: 209900 + (volumeRows / CostBaseRows)).ConfigureAwait(true);
        var targetRows = await TargetRowDefsAsync(s).ConfigureAwait(true);
        await BulkInsertAsync(s, volumeRows).ConfigureAwait(true);

        var stats = new List<MigrationRangeStat>();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
        var store = new DocumentVersionMigrationStore(db) { RangeRows = CostRangeRows, RangeObserver = stats.Add };
        await store.ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, OnlyLabelsPlan(s, targetRows, (volumeRows * 3L) + 2), null, null, CancellationToken.None)
            .ConfigureAwait(true);
        await tx.CommitAsync().ConfigureAwait(true);

        return [.. stats.Where(x => x.Kind == "cells")];
    }

    /// <summary>План «ті самі колонки й рядки, нові Id цільової версії» для сценарію <see cref="Target.OnlyLabels"/>.</summary>
    private static VersionMigrationPlan OnlyLabelsPlan(Scenario s, IReadOnlyList<int> targetRows, long transferred)
        => new(
            new Dictionary<int, int> { [s.Doc.SheetDefId] = s.TargetSheetDefId },
            new Dictionary<int, int> { [s.Doc.TableDefId] = s.TargetTableDefId },
            [
                new VersionMigrationColumn(s.Doc.ColumnDefIds[0], s.TargetColumns["C1"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[1], s.TargetColumns["C2"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[2], s.TargetColumns["C3"], s.TargetTableDefId),
            ],
            new Dictionary<int, int> { [s.Doc.RowDefIds[0]] = targetRows[0], [s.Doc.RowDefIds[1]] = targetRows[1] },
            new Dictionary<int, int>(), [], [], transferred, 0, 0);
}
