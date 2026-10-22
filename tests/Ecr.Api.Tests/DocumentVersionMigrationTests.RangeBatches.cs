// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.RangeBatches.cs
using System.Globalization;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Б1 (2026-10-21): комірки, оформлення, рядки й індекс пошуку переносяться ДІАПАЗОНАМИ за ключем з окремим комітом
/// на кожен, а транзакція переносу (блокування структури документів, версія проєкту) лишається відкритою до кінця.
/// </summary>
/// <remarks>
/// ⚠ Послаблення атомарності свідоме (див. коментар у <c>DocumentVersionMigrationStore</c>): обрив лишає зафіксовані
/// діапазони, версія проєкту не перемикається, а повтор доводить перенос до кінця.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private const int RangeTestRows = 6_000;     // 18 тис. комірок
    private const int RangeTestRangeRows = 1_000;   // ≈ 7 діапазонів

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Діапазонний_перенос_нічого_не_губить_не_дублює_і_комітить_діапазони_окремо()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(RangeTestRows).ConfigureAwait(true);
        var before = await AggregatesAsync(s).ConfigureAwait(true);
        var perColumnBefore = await CellsPerColumnAsync(s, s.Doc.ColumnDefIds).ConfigureAwait(true);

        var stats = new List<MigrationRangeStat>();
        var visibleWhileOpen = new List<bool>();
        var gateRefused = new List<bool>();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);

        // Транзакція переносу тримає структуру документа виняткове (як це робить обробник).
        await new SheetEditGate(db).EnterStructureAsync(s.Doc.DocumentId, exclusive: true, CancellationToken.None).ConfigureAwait(true);

        var store = new DocumentVersionMigrationStore(db)
        {
            RangeRows = RangeTestRangeRows,
            RangeObserver = stat =>
            {
                stats.Add(stat);
                if (stat.Kind == "cells")
                {
                    // Діапазон уже ЗАФІКСОВАНИЙ: сторонній сеанс бачить його, хоч транзакція переносу ще відкрита…
                    visibleWhileOpen.Add(CountMovedCells(s) > 0);

                    // …а блокування структури документа писарям недоступне: вони чекають і дістають 409.
                    gateRefused.Add(TrySharedStructureLock(s.Doc.DocumentId) == -1);
                }
            },
        };

        await store.ApplyAsync(
                s.Doc.ProjectId, s.TargetVersionId, OnlyLabelsPlan(s, targetRows, (RangeTestRows * 3L) + 2), null, null, CancellationToken.None)
            .ConfigureAwait(true);

        var cellRanges = stats.Where(x => x.Kind == "cells").ToList();
        Assert.True(cellRanges.Count >= 6, $"Діапазонів комірок {cellRanges.Count}: коміт на діапазон нічого не довів.");
        Assert.All(visibleWhileOpen, Assert.True);
        Assert.All(gateRefused, refused => Assert.True(refused, "Блокування структури документа звільнилося до коміту переносу."));

        // До коміту транзакції переносу версія проєкту НЕ перемкнена (її змінює лише фініш у цій транзакції).
        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        await tx.CommitAsync().ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));

        // Лічильники, суми й контрольні суми (без Id описів) — до і після однакові.
        Assert.Equal(before, await AggregatesAsync(s).ConfigureAwait(true));

        // Розподіл за колонками: кожна цільова колонка отримала рівно стільки, скільки мала вихідна.
        var perColumnAfter = await CellsPerColumnAsync(
            s, [s.TargetColumns["C1"], s.TargetColumns["C2"], s.TargetColumns["C3"]]).ConfigureAwait(true);
        Assert.Equal(perColumnBefore, perColumnAfter);

        // ColumnDefId — лише цільової версії, TableDefId узгоджено з колонкою; нічого не лишилось на вихідних описах.
        Assert.Equal(0, await ScalarAsync(
            """
            SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
            WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti
              AND NOT EXISTS (SELECT 1 FROM cfg.ColumnDef cd WHERE cd.Id = cv.ColumnDefId AND cd.TableDefId = cv.TableDefId)
            """,
            s).ConfigureAwait(true));
        Assert.Equal(0, await ScalarAsync(
            """
            SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
            WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti AND cv.TableDefId <> @td
            """,
            s, target: true).ConfigureAwait(true));
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.CellStyle cs JOIN doc.TableRow r ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId "
            + "WHERE cs.PeriodKey = @pk AND r.TableInstanceId = @ti AND cs.ColumnDefId IN (SELECT Id FROM cfg.ColumnDef WHERE TableDefId = @oldTd)",
            s).ConfigureAwait(true));
        Assert.Equal(2, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.DocumentIndexValue WHERE DocumentId = @doc AND ColumnDefId IN (SELECT Id FROM cfg.ColumnDef WHERE TableDefId = @td)",
            s, target: true).ConfigureAwait(true));

    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Повторний_прогін_завершених_діапазонів_нічого_не_змінює_і_не_видаляє_перенесене()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(RangeTestRows).ConfigureAwait(true);
        var before = await AggregatesAsync(s).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, (RangeTestRows * 3L) + 2);

        // Перший прогін: усі діапазони зафіксовано, а транзакцію переносу відкочено (збій після важкої частини):
        // екземпляр таблиці і версія проєкту — як були, дані — уже на цільових описах.
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            await new DocumentVersionMigrationStore(db) { RangeRows = RangeTestRangeRows }
                .ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, null, CancellationToken.None).ConfigureAwait(true);
            await tx.RollbackAsync().ConfigureAwait(true);
        }

        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId "
            + "WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti AND cv.TableDefId = @oldTd", s).ConfigureAwait(true));

        // Другий прогін: жоден діапазон нічого не змінює (ідемпотентність), індекс пошуку не знищено (мутація: DELETE
        // індексу за `m.o` без `m.n` видаляє вже перенесені рядки — тест червоніє).
        var second = new List<MigrationRangeStat>();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            await new DocumentVersionMigrationStore(db) { RangeRows = RangeTestRangeRows, RangeObserver = second.Add }
                .ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, null, CancellationToken.None).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        Assert.Contains(second, x => x.Kind == "cells");
        Assert.All(second.Where(x => x.Kind is "cells" or "index"), x => Assert.Equal(0, x.Rows));
        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.Equal(before, await AggregatesAsync(s).ConfigureAwait(true));
        Assert.Equal(2, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.DocumentIndexValue WHERE DocumentId = @doc AND ColumnDefId IN (SELECT Id FROM cfg.ColumnDef WHERE TableDefId = @td)",
            s, target: true).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Обрив_між_діапазонами_лишає_зафіксоване_а_повтор_доводить_перенос_до_кінця()
    {
        var (s, targetRows) = await ArrangeRangeVolumeAsync(RangeTestRows).ConfigureAwait(true);
        var before = await AggregatesAsync(s).ConfigureAwait(true);
        var plan = OnlyLabelsPlan(s, targetRows, (RangeTestRows * 3L) + 2);

        // ── Перший запуск: Cancel після третього діапазону комірок ──
        using var cts = new CancellationTokenSource();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
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
                () => store.ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, null, cts.Token)).ConfigureAwait(true);
            await tx.RollbackAsync().ConfigureAwait(true);
        }

        // Транзакція відкотилася (версія проєкту стара, екземпляр таблиці на старому описі), але зафіксовані діапазони
        // лишилися: частина комірок уже на цільових колонках, частина — ще на вихідних.
        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.Equal(s.Doc.TableDefId, await ScalarAsync(
            "SELECT TableDefId FROM doc.TableInstance WHERE PeriodKey = @pk AND Id = @ti", s).ConfigureAwait(true));
        var moved = CountMovedCells(s);
        var left = await ScalarAsync(
            "SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId "
            + "WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti AND cv.TableDefId = @oldTd", s).ConfigureAwait(true);
        Assert.True(moved > 0 && left > 0, $"Очікувано часткове перенесення: перенесено {moved}, лишилось {left}.");

        // ── Повторний запуск (новий токен, нова транзакція) доводить до кінця ──
        var resumed = new List<MigrationRangeStat>();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            var store = new DocumentVersionMigrationStore(db) { RangeRows = RangeTestRangeRows, RangeObserver = resumed.Add };
            await store.ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, null, null, CancellationToken.None).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.Equal(before, await AggregatesAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId "
            + "WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti AND cv.TableDefId = @oldTd", s).ConfigureAwait(true));
        Assert.Equal(2, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.DocumentIndexValue WHERE DocumentId = @doc AND ColumnDefId IN (SELECT Id FROM cfg.ColumnDef WHERE TableDefId = @td)",
            s, target: true).ConfigureAwait(true));

        // Повтор перенес лише залишок: сумарно змінено менше, ніж при повному переносі з нуля.
        var resumedRows = resumed.Where(x => x.Kind == "cells").Sum(x => x.Rows);
        Assert.True(resumedRows > 0 && resumedRows < (RangeTestRows * 4L) + 5, $"Повтор змінив {resumedRows} рядків.");
    }

    private async Task<(Scenario S, IReadOnlyList<int> TargetRows)> ArrangeRangeVolumeAsync(int volumeRows)
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var targetRows = await TargetRowDefsAsync(s).ConfigureAwait(true);
        await BulkInsertAsync(s, volumeRows).ConfigureAwait(true);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            -- Оформлення комірок: кожна друга вставлена комірка колонки C1.
            INSERT doc.CellStyle (PeriodKey, TableRowId, ColumnDefId, BackgroundHex, IsBold, UpdatedByUserId, UpdatedAt)
            SELECT r.PeriodKey, r.Id, @c1, '#ABCDEF', 0, @user, SYSUTCDATETIME()
            FROM   doc.TableRow r
            WHERE  r.PeriodKey = @pk AND r.TableInstanceId = @ti AND r.RowKey LIKE N'V%' AND r.Id % 2 = 0;

            -- Похідний індекс пошуку: два значення (по колонках C1 і C2).
            INSERT doc.DocumentIndexValue (DocumentId, ColumnDefId, ValueString) VALUES (@doc, @c1, N'idx1'), (@doc, @c2, N'idx2');
            """;
        command.Parameters.Add(new SqlParameter("@pk", s.Doc.PeriodKey.Value));
        command.Parameters.Add(new SqlParameter("@ti", s.Doc.TableInstanceId));
        command.Parameters.Add(new SqlParameter("@c1", s.Doc.ColumnDefIds[0]));
        command.Parameters.Add(new SqlParameter("@c2", s.Doc.ColumnDefIds[1]));
        command.Parameters.Add(new SqlParameter("@user", s.UserId));
        command.Parameters.Add(new SqlParameter("@doc", s.Doc.DocumentId));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        return (s, targetRows);
    }

    /// <summary>Лічильники, суми й контрольні суми значень, оформлення й рядків екземпляра — БЕЗ Id описів.</summary>
    private async Task<string> AggregatesAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT('cells:', COUNT_BIG(*), ':', ISNULL(SUM(cv.ValueNumeric), 0), ':',
                          CHECKSUM_AGG(CHECKSUM(cv.TableRowId, cv.ValueString, cv.ValueNumeric, cv.IsEmpty)))
            FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
            WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti
            UNION ALL
            SELECT CONCAT('styles:', COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(cs.TableRowId, cs.BackgroundHex)))
            FROM doc.CellStyle cs JOIN doc.TableRow r ON r.PeriodKey = cs.PeriodKey AND r.Id = cs.TableRowId
            WHERE cs.PeriodKey = @pk AND r.TableInstanceId = @ti
            UNION ALL
            SELECT CONCAT('rows:', COUNT_BIG(*), ':', CHECKSUM_AGG(CHECKSUM(r.Id, r.RowKey, r.Ordinal, r.IsDeleted)))
            FROM doc.TableRow r WHERE r.PeriodKey = @pk AND r.TableInstanceId = @ti;
            """;
        command.Parameters.Add(new SqlParameter("@pk", s.Doc.PeriodKey.Value));
        command.Parameters.Add(new SqlParameter("@ti", s.Doc.TableInstanceId));
        var parts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            parts.Add(reader.GetString(0));
        }

        return string.Join("|", parts);
    }

    private async Task<string> CellsPerColumnAsync(Scenario s, IReadOnlyList<int> columnIds)
    {
        var parts = new List<string>();
        foreach (var column in columnIds)
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT_BIG(*) FROM doc.CellValue WHERE PeriodKey = @pk AND ColumnDefId = @c";
            command.Parameters.Add(new SqlParameter("@pk", s.Doc.PeriodKey.Value));
            command.Parameters.Add(new SqlParameter("@c", column));
            parts.Add(Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture)!);
        }

        return string.Join(",", parts);
    }

    private int CountMovedCells(Scenario s)
    {
        using var connection = new SqlConnection(sql.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId "
            + "WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti AND cv.TableDefId = @td";
        command.Parameters.Add(new SqlParameter("@pk", s.Doc.PeriodKey.Value));
        command.Parameters.Add(new SqlParameter("@ti", s.Doc.TableInstanceId));
        command.Parameters.Add(new SqlParameter("@td", s.TargetTableDefId));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary><c>sp_getapplock</c> спільно, без очікування, з іншого сеансу: -1 — зайнято (виняткове тримає транзакція переносу).</summary>
    private int TrySharedStructureLock(long documentId)
    {
        using var connection = new SqlConnection(sql.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @code int;
            EXEC @code = sp_getapplock @Resource = @r, @LockMode = 'Shared', @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @code;
            """;
        command.Parameters.Add(new SqlParameter("@r", SheetEditGate.StructureResourceOf(documentId)));
        var code = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        transaction.Rollback();
        return code;
    }

    private async Task<int> ProjectVersionAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Projects.AsNoTracking().Where(p => p.Id == s.Doc.ProjectId)
            .Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(false);
    }

    private async Task<int> ScalarAsync(string text, Scenario s, bool target = false)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.Add(new SqlParameter("@pk", s.Doc.PeriodKey.Value));
        command.Parameters.Add(new SqlParameter("@ti", s.Doc.TableInstanceId));
        command.Parameters.Add(new SqlParameter("@doc", s.Doc.DocumentId));
        command.Parameters.Add(new SqlParameter("@td", target ? s.TargetTableDefId : s.Doc.TableDefId));
        command.Parameters.Add(new SqlParameter("@oldTd", s.Doc.TableDefId));
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
