// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.PermitHistoryRepair.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-7, одноразове виправлення <c>MIGR:permit-history-repair</c> (сід, <c>99-epilogue.sql</c>) для документів, перенесених
/// ДО фіксу D-7: історія дозволів лишилась на Id вихідної версії.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    /// <summary>
    /// Одноразове виправлення: документ, перенесений ДО D-7 (історія на Id вихідної версії), після старту застосунку
    /// (сід) бачить історію на цільовій версії; рядок без відповідника лишається як є й звітується; повтор — 0 рядків.
    /// Мутація: прибрати <c>NOT EXISTS</c> на відкриту знахідку в кроці 4 — повторний старт дублює знахідку, тест червоний.
    /// (Умова версії в кроці 1 сама по собі не мутаційна: рядок на поточній версії відсіює й перевірка зайнятого місця.)
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-7")]
    public async Task Permit_history_left_on_the_source_version_is_repaired_by_the_seed_once()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var h = await ArrangePermitHistoryAsync(s, s.Doc.ColumnDefIds[1]).ConfigureAwait(true);

        // Колонка, якої цільова версія не має (без даних — Safe переносить): відповідника для ремонту немає.
        int orphanColumn;
        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var column = new ColumnDef(s.Doc.TableDefId, EcrCode.Create("CX_" + s.Tag), Name("Gone"), 9, CellDataType.Decimal);
            arrange.ColumnDefs.Add(column);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
            orphanColumn = column.Id;
        }

        await MigrateSafeAsync(s).ConfigureAwait(true);

        // Стан, який лишав перенос до D-7: історія на Id вихідної версії; плюс рядок на колонці без відповідника.
        long orphanId;
        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var documentId = s.Doc.DocumentId;
            var (sourceTable, sourceColumn, targetColumn) = (s.Doc.TableDefId, s.Doc.ColumnDefIds[1], s.TargetColumns["C2"]);
            await arrange.Database.ExecuteSqlAsync($"""
                UPDATE doc.PermitSegment SET TableDefId = {sourceTable}, ColumnDefId = {sourceColumn}
                WHERE DocumentId = {documentId} AND ColumnDefId = {targetColumn};
                UPDATE doc.CellPermitPart SET TableDefId = {sourceTable}, ColumnDefId = {sourceColumn}
                WHERE PeriodKey = 202601 AND DocumentId = {documentId} AND ColumnDefId = {targetColumn};
                UPDATE doc.PermitSegment SET HeaderFieldDefId = {h.SourceHeaderId}
                WHERE DocumentId = {documentId} AND HeaderFieldDefId = {h.TargetHeaderId};
                """).ConfigureAwait(true);

            var orphan = PermitSegment.ForRow(
                s.Doc.DocumentId, s.Doc.TableDefId, null, h.RowKey, orphanColumn, PermitFrom, h.EntryId, s.UserId, Now);
            arrange.PermitSegments.Add(orphan);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
            orphanId = orphan.Id;
        }

        // Передумова: читач цільової версії історії не бачить — саме це й дефект J01 після переносу.
        await using (var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            Assert.Empty(await new PermitHistoryStore(check)
                .RowSegmentsAsync(s.Doc.DocumentId, s.TargetTableDefId, null, h.RowKey, s.TargetColumns["C2"], default).ConfigureAwait(true));
        }

        var first = await RunSeedRepairAsync().ConfigureAwait(true);
        Assert.True(first.Rows >= 4, $"очікувалось ≥ 4 рядки (сегмент, дві частини, шапка): {first.Line}");
        Assert.True(first.Unmatched >= 1, first.Line);

        await AssertHistoryOnTargetAsync(s, h).ConfigureAwait(true);

        // ⛔ Рядок без відповідника не видалено і не перенесено.
        await using (var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var orphan = await check.PermitSegments.AsNoTracking().SingleAsync(x => x.Id == orphanId).ConfigureAwait(true);
            Assert.Equal((s.Doc.TableDefId, orphanColumn), (orphan.TableDefId!.Value, orphan.ColumnDefId!.Value));
        }

        // Адміністратор бачить його в журналі узгодженості — рівно одна відкрита знахідка на рядок.
        Assert.Equal(1, await OpenStaleIssuesAsync(orphanId).ConfigureAwait(true));

        var second = await RunSeedRepairAsync().ConfigureAwait(true);
        Assert.Equal(0, second.Rows);

        // Повторний старт знахідку не дублює.
        Assert.Equal(1, await OpenStaleIssuesAsync(orphanId).ConfigureAwait(true));
    }

    private async Task<int> OpenStaleIssuesAsync(long permitSegmentId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.ConsistencyIssue
                WHERE  RuleCode = N'PERMIT_HISTORY_STALE_VERSION' AND EntityType = N'doc.PermitSegment'
                  AND  EntityId = {permitSegmentId} AND ResolvedAt IS NULL
                """).SingleAsync().ConfigureAwait(false);
    }

    /// <summary>Сід тим самим шляхом, що й старт застосунку; рядок журналу секції <c>MIGR:permit-history-repair</c>.</summary>
    private async Task<(int Rows, int Unmatched, string Line)> RunSeedRepairAsync()
    {
        const string Prefix = "permit-history-repair: ";
        var messages = new List<string>();

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        await new SeedRunner(db).RunAsync(CancellationToken.None).ConfigureAwait(false);

        var line = Assert.Single(messages, m => m.StartsWith(Prefix, StringComparison.Ordinal));
        var parts = line[Prefix.Length..].Split(' ');
        return (
            int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture),
            line);
    }
}
