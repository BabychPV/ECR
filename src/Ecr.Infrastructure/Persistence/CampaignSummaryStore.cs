using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Reporting.Dto;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICampaignSummaryStore"/> (<c>BE-22</c>).</summary>
/// <remarks>
/// ⚠ Три запити, а не один: перелік проєктів і лічильник зрізів LINQ дає
/// точно, а от «стан документа — найгірший зі станів його аркушів» лишається
/// агрегатом по агрегату, якого SQL Server не бере без похідної таблиці
/// (той самий привід до сирого SQL, що й у <see cref="DocumentListSummaryStore"/>).
/// Разом вони йдуть по щонайбільше <c>limit</c> проєктах, тобто по обмеженому
/// переліку ідентифікаторів, а не по всій базі.
/// </remarks>
public sealed class CampaignSummaryStore(EcrDbContext db) : ICampaignSummaryStore
{
    /// <inheritdoc />
    public async Task<CampaignProjectPage> ListAsync(int periodKey, int limit, CancellationToken ct)
    {
        // ⛔ Кампанія — це проєкти, у яких цей період ВЗАГАЛІ Є. Перелік усіх
        // проєктів системи показав би торішні разом із наступними, і питання
        // «хто затримує» потонуло б у рядках, яких воно не стосується.
        var inCampaign = db.Periods
            .Where(p => p.PeriodKeyValue == periodKey)
            .Select(p => p.ProjectId)
            .Distinct();

        var total = await inCampaign.CountAsync(ct).ConfigureAwait(false);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => inCampaign.Contains(p.Id))
            .OrderBy(p => p.Code)
            .Take(limit)
            .Select(p => new { p.Id, p.Code, p.NameL10n })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (projects.Count == 0)
        {
            return new CampaignProjectPage(total, []);
        }

        var ids = projects.ConvertAll(p => p.Id);
        var stages = await StagesAsync(periodKey, ids, ct).ConfigureAwait(false);

        // ⚠ Лише ПОТОЧНІ зрізи: повторна побудова створює новий рядок, а не
        // переписує старий, і без `IsCurrent` проєкт, зріз якого перебудували
        // тричі, виглядав би втричі готовішим за сусіда.
        var snapshots = await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => ids.Contains(s.ProjectId) && s.PeriodKey == periodKey && s.IsCurrent)
            .GroupBy(s => s.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct)
            .ConfigureAwait(false);

        var rows = projects.ConvertAll(p =>
        {
            // Проєкт без жодного документа не має рядка в агрегаті — і це не
            // те саме, що нулі «все в чернетках»: кампанія тут не починалася.
            var stage = stages.GetValueOrDefault(p.Id);

            return new CampaignProjectSummary(
                p.Id,
                p.Code,
                p.NameL10n,
                stage?.Documents ?? 0,
                stage?.Draft ?? 0,
                stage?.Submitted ?? 0,
                stage?.Approved ?? 0,
                stage?.Rejected ?? 0,
                snapshots.GetValueOrDefault(p.Id));
        });

        return new CampaignProjectPage(total, rows);
    }

    /// <summary>Лічильники етапів по кожному проєкту переліку.</summary>
    /// <remarks>
    /// ⚠ Аркуш складу БЕЗ рядка <c>wf.ApprovalState</c> — чернетка: рядок
    /// стану з'являється лише з першим поданням (<c>S-17</c>). Статуси —
    /// значення <c>DocumentStatus</c>: 0 Draft, 1 Submitted, 2 Approved,
    /// 3 Rejected.
    ///
    /// ⚠ <c>PeriodKey</c> стоїть у предикаті партиційованої
    /// <c>wf.ApprovalState</c> (урок <c>WR-05</c>).
    /// </remarks>
    private async Task<Dictionary<int, StageRow>> StagesAsync(
        int periodKey, IReadOnlyList<int> projectIds, CancellationToken ct)
    {
        var projectsJson = JsonSerializer.Serialize(projectIds);

        var rows = await db.Database
            .SqlQuery<StageRow>($"""
                SELECT x.ProjectId,
                       COUNT(*) AS Documents,
                       COALESCE(SUM(CASE WHEN x.Rejected = 0 AND (x.Draft > 0 OR x.Sheets = 0) THEN 1 ELSE 0 END), 0) AS Draft,
                       COALESCE(SUM(CASE WHEN x.Rejected = 0 AND x.Draft = 0 AND x.Submitted > 0 THEN 1 ELSE 0 END), 0) AS Submitted,
                       COALESCE(SUM(CASE WHEN x.Sheets > 0 AND x.Approved = x.Sheets THEN 1 ELSE 0 END), 0) AS Approved,
                       COALESCE(SUM(CASE WHEN x.Rejected > 0 THEN 1 ELSE 0 END), 0) AS Rejected
                FROM (
                    SELECT d.Id, d.ProjectId,
                           COUNT(s.SheetDefId) AS Sheets,
                           COALESCE(SUM(CASE WHEN s.SheetDefId IS NOT NULL AND COALESCE(a.Status, 0) = 0 THEN 1 ELSE 0 END), 0) AS Draft,
                           COALESCE(SUM(CASE WHEN a.Status = 1 THEN 1 ELSE 0 END), 0) AS Submitted,
                           COALESCE(SUM(CASE WHEN a.Status = 2 THEN 1 ELSE 0 END), 0) AS Approved,
                           COALESCE(SUM(CASE WHEN a.Status = 3 THEN 1 ELSE 0 END), 0) AS Rejected
                    FROM doc.Document d
                    LEFT JOIN doc.DocumentSheet s
                      ON s.DocumentId = d.Id AND s.IsIncluded = 1
                    LEFT JOIN wf.ApprovalState a
                      ON a.DocumentId = s.DocumentId AND a.SheetDefId = s.SheetDefId AND a.PeriodKey = {periodKey}
                    WHERE d.ProjectId IN (SELECT CAST(j.value AS int) FROM OPENJSON({projectsJson}) j)
                    GROUP BY d.Id, d.ProjectId
                ) x
                GROUP BY x.ProjectId
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.ProjectId);
    }

    /// <summary>Рядок агрегату етапів; імена колонок — імена властивостей.</summary>
    public sealed record StageRow(
        int ProjectId, int Documents, int Draft, int Submitted, int Approved, int Rejected);
}
