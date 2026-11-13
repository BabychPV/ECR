using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IProjectStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class ProjectStore(EcrDbContext db) : IProjectStore
{
    /// <inheritdoc />
    public async Task<PagedResult<ProjectSummary>> ListAsync(
        CursorRequest page, IReadOnlyCollection<int> visibleIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(visibleIds);

        if (visibleIds.Count == 0)
        {
            return new PagedResult<ProjectSummary>([], null, TotalCount: null);
        }

        var after = Cursor.Decode(page.Cursor);

        // Періоди не завантажуються — рахується лише їхня кількість. Повний
        // граф на сотні проєктів дав би сотні тисяч рядків заради двох колонок
        // на екрані.
        var rows = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id > after && visibleIds.Contains(p.Id))
            .OrderBy(p => p.Id)
            .Take(page.Limit + 1)
            .Select(p => new ProjectSummary(
                p.Id,
                p.Code,
                p.Status,
                p.TimeZoneId,
                p.PeriodKind,
                p.CurrentPeriodId,
                db.Periods.Count(period => period.ProjectId == p.Id),
                db.Documents.Count(document => document.ProjectId == p.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).ToList();

        return new PagedResult<ProjectSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <summary>Рядок фактів видалення (сирий SQL: історія лежить у таблицях без сутностей у цьому контексті).</summary>
    private sealed class DeletionRow
    {
        public string Code { get; set; } = string.Empty;

        public int Status { get; set; }

        public int TemplateVersionId { get; set; }

        public int DocumentCount { get; set; }

        public int HistoryCount { get; set; }
    }

    /// <inheritdoc />
    public async Task<ProjectDeletionFacts?> GetDeletionFactsAsync(int projectId, CancellationToken ct)
    {
        var row = await db.Database
            .SqlQuery<DeletionRow>(
                $"""
                 SELECT p.Code AS Code,
                        CAST(p.Status AS int) AS Status,
                        p.TemplateVersionId AS TemplateVersionId,
                        (SELECT COUNT(*) FROM doc.Document d WHERE d.ProjectId = p.Id) AS DocumentCount,
                        (SELECT COUNT(*) FROM calc.CalculationRun r WHERE r.ProjectId = p.Id)
                      + (SELECT COUNT(*) FROM rpt.ReportSnapshot s WHERE s.ProjectId = p.Id)
                      + (SELECT COUNT(*) FROM itg.ArchiveRun a WHERE a.ProjectId = p.Id)
                      + (SELECT COUNT(*) FROM calc.RecalculationApproval ra WHERE ra.ProjectId = p.Id) AS HistoryCount
                   FROM doc.Project p
                  WHERE p.Id = {projectId}
                 """)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new ProjectDeletionFacts(
                row.Code, (Domain.Enums.ProjectStatus)row.Status, row.TemplateVersionId, row.DocumentCount, row.HistoryCount);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Порядок — за зовнішніми ключами (усі <c>NO ACTION</c>): спершу посилання проєкту на поточний період, далі
    /// кроки маршруту → маршрут, прив'язки версій, періоди, гранти на проєкт, сам проєкт. Транзакцію відкриває
    /// викликач (разом із записом журналу).
    /// </remarks>
    public async Task DeleteAsync(int projectId, CancellationToken ct)
    {
        await db.Database
            .ExecuteSqlAsync(
                $"""
                 UPDATE doc.Project SET CurrentPeriodId = NULL WHERE Id = {projectId};
                 DELETE st FROM wf.ApprovalStep st
                   JOIN wf.ApprovalRoute r ON r.Id = st.ApprovalRouteId
                  WHERE r.ProjectId = {projectId};
                 DELETE FROM wf.ApprovalRoute WHERE ProjectId = {projectId};
                 DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = {projectId};
                 DELETE FROM doc.ProjectTemplate WHERE ProjectId = {projectId};
                 DELETE FROM doc.Period WHERE ProjectId = {projectId};
                 DELETE FROM sec.ResourceGrant WHERE ResourceKind = {(int)Domain.Enums.ResourceKind.Project} AND ResourceId = {projectId};
                 DELETE FROM doc.Project WHERE Id = {projectId};
                 """,
                ct)
            .ConfigureAwait(false);
    }
}
