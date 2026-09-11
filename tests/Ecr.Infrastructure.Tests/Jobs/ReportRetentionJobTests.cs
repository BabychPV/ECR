// tests/Ecr.Infrastructure.Tests/Jobs/ReportRetentionJobTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Прибирання зайвих зрізів звітності (<c>B16</c> §4, <c>D-71</c>).
/// </summary>
/// <remarks>
/// ⛔ Q-2xx (аудит фази 3, звітність). Задачі не існувало взагалі:
/// `BuildReportSnapshotHandler` створює НОВИЙ зріз на кожен виклик і ніколи не
/// переписує старий (ФВ-9.17), тож без прибирання `rpt.ReportSnapshot` і
/// `rpt.ReportRow` ростуть вічно. Тест доводить РЕАЛЬНИМ прогоном проти
/// SQLEXPRESS: не поточні й не подані зрізи (разом із рядками) зникають, а
/// поточний і поданий лишаються недоторканими.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportRetentionJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прибирає_лише_НЕ_поточні_і_НЕ_подані_зрізи()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        var tag = Guid.NewGuid().ToString("N")[..8];

        long staleId1;
        long staleId2;
        long currentId;
        long submittedId;

        await using (var db = chain.CreateContext())
        {
            var def = new ReportDef(
                EcrCode.Create($"RPT{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Retention test" }),
                isRegulatory: true);
            db.ReportDefs.Add(def);
            await db.SaveChangesAsync(CancellationToken.None);

            var version = new ReportVersion(def.Id, "1.0", "[]", "{}", Now);
            version.Publish();
            db.ReportVersions.Add(version);
            await db.SaveChangesAsync(CancellationToken.None);

            // Три зрізи (звіт × проєкт × період): два — стара побудова, яку
            // ЩОЙНО перемкнули з IsCurrent, третій — чинний. Лише третій
            // (UX_ReportSnapshot_Current) може лишатися IsCurrent = 1.
            var stale1 = new ReportSnapshot(
                version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Draft, Now, null);
            stale1.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);

            var stale2 = new ReportSnapshot(
                version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Draft, Now, null);
            stale2.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);

            var current = new ReportSnapshot(
                version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Approved, Now, null);
            current.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);
            current.MakeCurrent();

            // Поданий зріз — ІНШИЙ період, щоб не зіткнутися з унікальним
            // індексом поточності: подання не скасовує поточність (D-71,
            // ReportSnapshotSync), тож у реальних даних поданий і поточний
            // зріз того самого періоду цілком можуть бути РІЗНИМИ рядками.
            var submitted = new ReportSnapshot(
                version.Id, document.ProjectId, document.PeriodKey.Value + 1, SnapshotStatus.Approved, Now, null);
            submitted.Complete(rowCount: 1, contentHash: null, calculationRunId: null, parametersJson: null);
            submitted.MarkSubmitted(userId: 7);

            db.ReportSnapshots.AddRange(stale1, stale2, current, submitted);
            await db.SaveChangesAsync(CancellationToken.None);

            staleId1 = stale1.Id;
            staleId2 = stale2.Id;
            currentId = current.Id;
            submittedId = submitted.Id;

            foreach (var id in new[] { staleId1, staleId2, currentId, submittedId })
            {
                var row = new ReportRow(id, rowNo: 1, columnCode: "A");
                row.SetValue(valueString: null, valueNumeric: 1m, valueDate: null);
                db.ReportRows.Add(row);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var jobDb = chain.CreateContext())
        {
            var job = new ReportRetentionJob(jobDb, new TestClock(Now));
            await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        await using var verify = chain.CreateContext();

        var remainingSnapshots = await verify.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == staleId1 || s.Id == staleId2 || s.Id == currentId || s.Id == submittedId)
            .Select(s => s.Id)
            .ToListAsync(CancellationToken.None);

        // ⛔ Головне твердження: НЕ поточні й НЕ подані зникли, поточний і
        // поданий лишилися — рівно межа з D-71 («крім IsSubmitted і
        // IsCurrent»), а не «усе старе».
        Assert.DoesNotContain(staleId1, remainingSnapshots);
        Assert.DoesNotContain(staleId2, remainingSnapshots);
        Assert.Contains(currentId, remainingSnapshots);
        Assert.Contains(submittedId, remainingSnapshots);

        var remainingRows = await verify.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == staleId1 || r.SnapshotId == staleId2
                        || r.SnapshotId == currentId || r.SnapshotId == submittedId)
            .Select(r => r.SnapshotId)
            .ToListAsync(CancellationToken.None);

        // ⛔ Рядки зникають РАЗОМ зі своїм зрізом: FK_RepRow_Snap —
        // DeleteBehavior.Restrict (не каскадний), тож рядок, забутий у
        // rpt.ReportRow, був би доказом того, що видалення зробили в
        // неправильному порядку.
        Assert.DoesNotContain(staleId1, remainingRows);
        Assert.DoesNotContain(staleId2, remainingRows);
        Assert.Contains(currentId, remainingRows);
        Assert.Contains(submittedId, remainingRows);

        var run = await verify.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.JobCode == ReportRetentionJob.Code)
            .OrderByDescending(r => r.StartedAt)
            .FirstAsync(CancellationToken.None);

        // ⚠ Не рівно 2: `rpt.ReportSnapshot` — таблиця, спільна з РЕШТОЮ
        // тестів колекції (той самий SQLEXPRESS), тож прогін міг прибрати і
        // зайві зрізи, лишені іншими тестами. Головний доказ — per-ID
        // перевірки вище; тут лишається переконатися, що прогін узагалі щось
        // знайшов і зафіксував це чесно (не нуль на пустому місці).
        Assert.Equal("Succeeded", run.Status);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex("\"snapshotsDeleted\":([2-9]|[1-9]\\d+)"),
            run.DetailsJson);
    }
}
