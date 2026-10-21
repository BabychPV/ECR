// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Resume.cs
using System.Globalization;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відновлення після обриву: watermark діапазонів у маркері проєкту, відбиток мапи (інша мапа - 422) і постумова
/// «жодної комірки на вихідних колонках» перед перемиканням версії.
/// </summary>
/// <remarks>
/// Мутації (червоніють): прибрати пропуск діапазонів за watermark - перший тест; прибрати звірку відбитка в
/// <c>BeginMigrationAsync</c> - другий; прибрати постумову в <c>ApplyCoreAsync</c> - третій.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Повтор_після_обриву_продовжує_з_watermark_а_не_з_початку()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(RangeTestRows).ConfigureAwait(true);
        var before = await AggregatesAsync(s).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, (RangeTestRows * 3L) + 2);

        // P2-1: результат валідації старої версії - має пережити обрив і зникнути лише разом з перемиканням версії.
        ExecuteSql("INSERT wf.ValidationResult (DocumentId, PeriodKey, RunAt, ErrorCount, WarningCount, InfoCount, MessagesJson) VALUES (" + s.Doc.DocumentId + ", " + s.Doc.PeriodKey.Value + ", SYSUTCDATETIME(), 0, 0, 0, N'[]')");

        await BeginAsync(s, plan).ConfigureAwait(true);

        // Перший прогін важкої частини: обрив після третього діапазону комірок (спостерігач бачить діапазон ПІСЛЯ коміту).
        using var cts = new CancellationTokenSource();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var store = new DocumentVersionMigrationStore(db)
            {
                RangeRows = RangeTestRangeRows,
                RangeObserver = stat =>
                {
                    if (stat is { Kind: "cells", Index: 2 })
                    {
                        cts.Cancel();
                    }
                },
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, cts.Token)).ConfigureAwait(true);
        }

        var json = await ProjectJsonAsync(s).ConfigureAwait(true);
        Assert.Contains("\"wmCellsHi\"", json, StringComparison.Ordinal);
        Assert.Contains("\"planHash\"", json, StringComparison.Ordinal);

        // Повтор: перші три діапазони (0..2) пропущено - перший виконаний має індекс >= 3.
        var resumed = new List<MigrationRangeStat>();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var store = new DocumentVersionMigrationStore(db) { RangeRows = RangeTestRangeRows, RangeObserver = resumed.Add };
            await BeginAsync(s, plan).ConfigureAwait(true);   // повторний Begin з тією самою мапою - дозволений і нічого не стирає
            await store.MoveDataAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, CancellationToken.None).ConfigureAwait(true);
        }

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM wf.ValidationResult WHERE DocumentId = @doc", s).ConfigureAwait(true));   // обрив/повтор важкої частини результатів не чіпають

        var firstCellsIndex = resumed.Where(x => x.Kind == "cells").Min(x => x.Index);
        Assert.True(firstCellsIndex >= 3, $"Повтор почав з діапазону {firstCellsIndex}, а мав з 3: watermark не діє.");

        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            await new DocumentVersionMigrationStore(db)
                .CompleteAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, null, CancellationToken.None).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        Assert.Equal(before, await AggregatesAsync(s).ConfigureAwait(true));
        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM wf.ValidationResult WHERE DocumentId = @doc", s).ConfigureAwait(true));   // зникли разом з перемиканням версії
        Assert.Null(await ProjectJsonAsync(s).ConfigureAwait(true));   // маркер і watermark знято разом з перемиканням версії
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Відновлення_з_іншою_мапою_422_і_маркер_та_дані_не_змінюються()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(500).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, 1_502);
        await BeginAsync(s, plan).ConfigureAwait(true);
        var jsonBefore = await ProjectJsonAsync(s).ConfigureAwait(true);

        // Інша мапа колонок (C3 вилучено з плану) відносно збереженої.
        var changed = plan with { Columns = [.. plan.Columns.Take(2)] };
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new DocumentVersionMigrationStore(db).BeginMigrationAsync(s.Doc.ProjectId, s.TargetVersionId, changed, s.UserId, CancellationToken.None))
            .ConfigureAwait(true);
        await tx.RollbackAsync().ConfigureAwait(true);

        Assert.Equal("ECR-SCHM-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-SCHM-0422.migrateResumeMismatch", refused.Details!["messageKey"]);
        Assert.Equal(jsonBefore, await ProjectJsonAsync(s).ConfigureAwait(true));
        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Без_важкої_частини_версію_не_перемикають_постумова_бачить_комірки_на_вихідних_колонках()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(300).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, 902);
        await BeginAsync(s, plan).ConfigureAwait(true);

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
        var refused = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new DocumentVersionMigrationStore(db).CompleteAsync(
                s.Doc.ProjectId, s.TargetVersionId, plan, null, null, CancellationToken.None)).ConfigureAwait(true);
        await tx.RollbackAsync().ConfigureAwait(true);

        Assert.Equal("ECR-CELL-0409", refused.ErrorCode);
        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.True(await MarkerPresentAsync(s).ConfigureAwait(true));
    }

    private async Task BeginAsync(Scenario s, VersionMigrationPlan plan)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
        await new DocumentVersionMigrationStore(db)
            .BeginMigrationAsync(s.Doc.ProjectId, s.TargetVersionId, plan, s.UserId, CancellationToken.None).ConfigureAwait(true);
        await tx.CommitAsync().ConfigureAwait(true);
    }

    private async Task<string?> ProjectJsonAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ExternalSettingsJson FROM doc.Project WHERE Id = @p";
        command.Parameters.Add(new SqlParameter("@p", s.Doc.ProjectId));
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is string text ? text : null;
    }
}
