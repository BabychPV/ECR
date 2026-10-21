// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.MarkerStore.cs
using Ecr.Application.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Маркер "міграція в процесі" на рівні сховища: співіснування з іншими ключами <c>ExternalSettingsJson</c>
/// (<c>CustomPeriodCount</c>), будь-який вміст колонки (NULL/порожній/не-JSON) і взаємне виключення важкої частини.
/// </summary>
/// <remarks>
/// Мутації (червоніють): знімати маркер заміною всього значення на NULL у <c>FinishSql</c> - перший тест (втрачено
/// CustomPeriodCount); прибрати умову оренди в <c>ClaimLeaseAsync</c> - другий тест (обидва запуски входять).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not json at all", null)]
    [InlineData("{\"CustomPeriodCount\":5}", "{\"CustomPeriodCount\":5}")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Маркер_співіснує_з_іншими_ключами_ExternalSettingsJson_і_знімається_без_слідів(string? initial, string? expectedAfter)
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(300).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, 902);
        await SetProjectJsonAsync(s, initial).ConfigureAwait(true);

        await BeginAsync(s, plan).ConfigureAwait(true);
        Assert.True(await MarkerPresentAsync(s).ConfigureAwait(true));

        // Незалежний атомарний письменник іншого ключа під час важкої частини не знімає маркер і сам не губиться.
        var written = false;
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var store = new DocumentVersionMigrationStore(db)
            {
                RangeRows = 100,
                RangeObserver = stat =>
                {
                    if (!written && stat.Kind == "cells")
                    {
                        written = true;
                        ExecuteSql("UPDATE doc.Project SET ExternalSettingsJson = JSON_MODIFY(ExternalSettingsJson, '$.Other', 7) WHERE Id = " + s.Doc.ProjectId);
                    }
                },
            };
            await store.MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, CancellationToken.None).ConfigureAwait(true);
        }

        Assert.True(written);
        Assert.True(await MarkerPresentAsync(s).ConfigureAwait(true));
        Assert.Contains("\"Other\":7", await ProjectJsonAsync(s).ConfigureAwait(true), StringComparison.Ordinal);

        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            await new DocumentVersionMigrationStore(db)
                .CompleteAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, null, CancellationToken.None).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        Assert.False(await MarkerPresentAsync(s).ConfigureAwait(true));
        var after = await ProjectJsonAsync(s).ConfigureAwait(true);
        // Чужий ключ Other лишається (його записав незалежний письменник); власні ключі маркера знято.
        if (expectedAfter is null)
        {
            Assert.Equal("{\"Other\":7}", after);
        }
        else
        {
            Assert.Contains("\"CustomPeriodCount\":5", after, StringComparison.Ordinal);
            Assert.Contains("\"Other\":7", after, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("migratingTo", after, StringComparison.Ordinal);
        Assert.DoesNotContain("wmCells", after, StringComparison.Ordinal);
        Assert.DoesNotContain("leaseUntil", after, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Два_паралельні_запуски_важкої_частини_одного_переносу_другий_отримує_409_поки_перший_тримає_оренду()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(1_500).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, 4_502);
        await BeginAsync(s, plan).ConfigureAwait(true);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
            var store = new DocumentVersionMigrationStore(db)
            {
                RangeRows = 500,
                RangeObserver = stat =>
                {
                    if (stat.Kind == "cells" && stat.Index == 0)
                    {
                        reached.TrySetResult();
                        release.Task.Wait(TimeSpan.FromSeconds(60));
                    }
                },
            };
            await store.MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, CancellationToken.None).ConfigureAwait(false);
        });

        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

            // Другий запуск важкої частини (реклейм задачі, синхронний шлях) - 409, діапазони не чіпає.
            await using var db2 = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
            var secondRanges = new List<MigrationRangeStat>();
            var refusedMove = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new DocumentVersionMigrationStore(db2) { RangeRows = 500, RangeObserver = secondRanges.Add }
                    .MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("ECR-DOC-4091", refusedMove.ErrorCode);
            Assert.Empty(secondRanges);

            // Той самий T1 повторного запуску відмовляє ще на постановці маркера.
            await using var tx = await db2.Database.BeginTransactionAsync().ConfigureAwait(true);
            var refusedBegin = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new DocumentVersionMigrationStore(db2)
                    .BeginMigrationAsync(s.Doc.ProjectId, s.TargetVersionId, plan, s.UserId, CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("ECR-DOC-4091", refusedBegin.ErrorCode);
            await tx.RollbackAsync().ConfigureAwait(true);
        }
        finally
        {
            release.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(true);

        // Оренду знято після завершення важкої частини: повтор (наприклад, після збою T2) не чекає її спливання.
        var json = await ProjectJsonAsync(s).ConfigureAwait(true);
        Assert.DoesNotContain("leaseUntil", json!, StringComparison.Ordinal);
        await using var db3 = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await new DocumentVersionMigrationStore(db3)
            .MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Прострочену_оренду_переймає_новий_виконавець_а_перший_втрачає_оренду_і_зупиняється()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(1_500).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, 4_502);
        await BeginAsync(s, plan).ConfigureAwait(true);

        // Оренда чужого виконавця, що спливла (процес помер): новий виконавець її перехоплює.
        ExecuteSql("UPDATE doc.Project SET ExternalSettingsJson = JSON_MODIFY(JSON_MODIFY(ExternalSettingsJson, '$.owner', 'dead'), '$.leaseUntil', '2000-01-01T00:00:00') WHERE Id = " + s.Doc.ProjectId);

        var stolen = false;
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var store = new DocumentVersionMigrationStore(db)
        {
            RangeRows = 500,
            RangeObserver = stat =>
            {
                if (!stolen && stat.Kind == "cells" && stat.Index == 0)
                {
                    // Після першого діапазону оренду «перехоплює» інший виконавець: наш наступний діапазон мусить відкотитись і зупинитись.
                    stolen = true;
                    ExecuteSql("UPDATE doc.Project SET ExternalSettingsJson = JSON_MODIFY(ExternalSettingsJson, '$.owner', 'thief') WHERE Id = " + s.Doc.ProjectId);
                }
            },
        };

        var lost = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("ECR-DOC-4091", lost.ErrorCode);
        Assert.True(stolen);
        // Другий діапазон відкотився цілим: зафіксовано рівно один діапазон (≈ 500 рядків × 3 комірки).
        Assert.InRange(CountMovedCells(s), 1, 1_600);
    }

    private async Task SetProjectJsonAsync(Scenario s, string? value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE doc.Project SET ExternalSettingsJson = @v WHERE Id = @p";
        command.Parameters.Add(new SqlParameter("@v", (object?)value ?? DBNull.Value));
        command.Parameters.Add(new SqlParameter("@p", s.Doc.ProjectId));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private void ExecuteSql(string text)
    {
        using var connection = new SqlConnection(sql.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
