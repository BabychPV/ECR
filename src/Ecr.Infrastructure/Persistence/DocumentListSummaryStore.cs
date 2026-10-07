using System.Text.Json;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentListSummaryStore"/>: один агрегований запит.</summary>
public sealed class DocumentListSummaryStore(EcrDbContext db) : IDocumentListSummaryStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Сирий SQL, а не LINQ: стан документа — агрегат по аркушах, а лічильник
    /// — агрегат по цих агрегатах; SQL Server не дає агрегувати вираз із
    /// підзапитом, тож потрібна похідна таблиця, якої LINQ тут надійно не дає.
    ///
    /// ⚠ Аркуш складу БЕЗ рядка <c>wf.ApprovalState</c> — чернетка: рядок стану
    /// з'являється лише з першим поданням (<c>S-17</c>). Статуси — значення
    /// <c>DocumentStatus</c>: 0 Draft, 1 Submitted, 2 Approved, 3 Rejected.
    ///
    /// ⚠ <c>PeriodKey</c> стоїть у предикаті обох партиційованих таблиць
    /// (урок <c>WR-05</c>).
    /// </remarks>
    public async Task<DocumentListSummaryResponse> SummarizeAsync(
        int? projectId, int periodKey, IReadOnlyCollection<int>? visibleProjectIds,
        SummaryRestrictions? restrictions, CancellationToken ct)
    {
        // ⛔ Схований від читача аркуш не вносить стан (LEFT JOIN нижче його не бачить), а документ
        // проєкту зі звуженням не потрапляє у «З зауваженнями»: збережений підсумок перевірки —
        // по ВСЬОМУ документу, тож рахувати по ньому значить розкрити помилки схованого.
        var hiddenJson = JsonSerializer.Serialize(
            (restrictions?.HiddenSheets ?? []).Select(h => new { p = h.ProjectId, c = h.SheetCode }));
        var narrowedJson = JsonSerializer.Serialize(restrictions?.NarrowedProjects ?? []);

        // Фільтр за проєктом і межа грантів зводяться в ОДИН перелік: сирий
        // запит не вміє «параметр або NULL» без явного типу параметра.
        IEnumerable<int>? allowed = visibleProjectIds;
        if (projectId is { } single)
        {
            allowed = allowed is null ? [single] : allowed.Where(id => id == single);
        }

        var restrict = allowed is null ? 0 : 1;
        var projectsJson = JsonSerializer.Serialize(allowed ?? []);

        var row = await db.Database
            .SqlQuery<SummaryRow>($"""
                SELECT
                    COALESCE(SUM(CASE WHEN x.Rejected = 0 AND (x.Draft > 0 OR x.Sheets = 0) THEN 1 ELSE 0 END), 0) AS Draft,
                    COALESCE(SUM(CASE WHEN x.Rejected = 0 AND x.Draft = 0 AND x.Submitted > 0 THEN 1 ELSE 0 END), 0) AS Submitted,
                    COALESCE(SUM(CASE WHEN x.Sheets > 0 AND x.Approved = x.Sheets THEN 1 ELSE 0 END), 0) AS Approved,
                    COALESCE(SUM(CASE WHEN x.Rejected > 0 THEN 1 ELSE 0 END), 0) AS Rejected,
                    COALESCE(SUM(CASE WHEN x.LastErrorCount > 0 THEN 1 ELSE 0 END), 0) AS WithIssues,
                    COALESCE(SUM(x.Approved), 0) AS SheetsApproved,
                    COALESCE(SUM(x.Sheets), 0) AS SheetsTotal
                FROM (
                    SELECT d.Id,
                           COUNT(s.SheetDefId) AS Sheets,
                           COALESCE(SUM(CASE WHEN s.SheetDefId IS NOT NULL AND COALESCE(a.Status, 0) = 0 THEN 1 ELSE 0 END), 0) AS Draft,
                           COALESCE(SUM(CASE WHEN a.Status = 1 THEN 1 ELSE 0 END), 0) AS Submitted,
                           COALESCE(SUM(CASE WHEN a.Status = 2 THEN 1 ELSE 0 END), 0) AS Approved,
                           COALESCE(SUM(CASE WHEN a.Status = 3 THEN 1 ELSE 0 END), 0) AS Rejected,
                           (SELECT TOP (1) v.ErrorCount
                            FROM wf.ValidationResult v
                            WHERE v.DocumentId = d.Id AND v.PeriodKey = {periodKey}
                              AND d.ProjectId NOT IN (SELECT CAST(n.value AS int) FROM OPENJSON({narrowedJson}) n)
                            ORDER BY v.RunAt DESC) AS LastErrorCount
                    FROM doc.Document d
                    LEFT JOIN doc.DocumentSheet s
                      ON s.DocumentId = d.Id AND s.IsIncluded = 1
                     AND NOT EXISTS (SELECT 1
                                     FROM cfg.SheetDef sd
                                     JOIN OPENJSON({hiddenJson}) WITH (p int '$.p', c nvarchar(200) '$.c') h
                                       ON h.p = d.ProjectId AND h.c = sd.Code COLLATE DATABASE_DEFAULT
                                     WHERE sd.Id = s.SheetDefId)
                    LEFT JOIN wf.ApprovalState a
                      ON a.DocumentId = s.DocumentId AND a.SheetDefId = s.SheetDefId AND a.PeriodKey = {periodKey}
                    WHERE {restrict} = 0
                       OR d.ProjectId IN (SELECT CAST(j.value AS int) FROM OPENJSON({projectsJson}) j)
                    GROUP BY d.Id, d.ProjectId
                ) x
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        return new DocumentListSummaryResponse(
            row.Draft, row.Submitted, row.Approved, row.Rejected, row.WithIssues,
            row.SheetsApproved, row.SheetsTotal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, long>> SampleDocumentPerProjectAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        var ids = projectIds.ToArray();
        var rows = await db.Documents
            .AsNoTracking()
            .Where(d => ids.Contains(d.ProjectId))
            .GroupBy(d => d.ProjectId)
            .Select(g => new { ProjectId = g.Key, DocumentId = g.Min(d => d.Id) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.ProjectId, r => r.DocumentId);
    }

    /// <summary>Рядок агрегату; імена колонок — імена властивостей.</summary>
    public sealed record SummaryRow(
        int Draft, int Submitted, int Approved, int Rejected, int WithIssues, int SheetsApproved, int SheetsTotal);
}
