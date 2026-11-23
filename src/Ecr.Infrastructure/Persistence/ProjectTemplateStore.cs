using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>B74 P3 (D-576): шаблони проєкту (<c>doc.ProjectTemplate</c>).</summary>
/// <remarks>
/// ⚠ Основний шаблон — шаблон версії <c>doc.fn_VersionOf(проєкт, 0)</c> (шлях сумісності PT01 відповідає саме за нього), а
/// не пряме читання <c>Project.TemplateVersionId</c>: сторож B74 нових читань поля не пускає, а в P6 колонка зникає.
/// </remarks>
public sealed class ProjectTemplateStore(EcrDbContext db) : IProjectTemplateStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectTemplateRow>> ListAsync(int projectId, CancellationToken ct)
        => await Rows(projectId, templateId: null).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ProjectTemplateRow?> FindAsync(int projectId, int templateId, CancellationToken ct)
        => await Rows(projectId, templateId).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private IQueryable<ProjectTemplateRow> Rows(int projectId, int? templateId)
    {
        var primary = db.TemplateVersions
            .Where(v => v.Id == EcrDbContext.VersionOf(projectId, 0))
            .Select(v => (int?)v.TemplateId);

        return
            from pt in db.ProjectTemplates.AsNoTracking()
            where pt.ProjectId == projectId && (templateId == null || pt.TemplateId == templateId)
            join t in db.Templates.AsNoTracking() on pt.TemplateId equals t.Id
            join v in db.TemplateVersions.AsNoTracking() on pt.CurrentTemplateVersionId equals v.Id
            let isPrimary = primary.FirstOrDefault() == pt.TemplateId
            orderby t.Code
            select new ProjectTemplateRow(
                pt.Id,
                pt.TemplateId,
                t.Code,
                t.NameL10n,
                pt.CurrentTemplateVersionId,
                v.Version,
                v.Layout,
                isPrimary,
                db.Documents.Count(d => d.ProjectId == projectId
                                        && (d.TemplateId == pt.TemplateId || (d.TemplateId == null && isPrimary))));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Той самий блок, що й <c>DocumentStore.LockProjectTemplateVersionAsync</c> і перенос версії: приєднання,
    /// від'єднання, створення документа й перенос версії в одному проєкті йдуть по черзі.
    /// </remarks>
    public async Task<ProjectTemplateLockFacts?> LockProjectAsync(int projectId, CancellationToken ct)
    {
        _ = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "LockProjectAsync викликано поза транзакцією: блок рядка проєкту звільнився б одразу.");

        var locked = await db.Database
            .SqlQuery<int>($"""
                SELECT Id AS Value
                FROM   doc.Project WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE  Id = {projectId}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (locked.Count == 0)
        {
            return null;
        }

        return await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new ProjectTemplateLockFacts(
                p.Status,
                p.IsArchiving,
                p.PeriodKind,
                db.TemplateVersions
                    .Where(v => v.Id == EcrDbContext.VersionOf(projectId, 0))
                    .Select(v => (int?)v.TemplateId)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> CountDocumentsAsync(int projectId, int templateId, bool isPrimary, CancellationToken ct)
        => db.Documents
            .AsNoTracking()
            .CountAsync(d => d.ProjectId == projectId
                             && (d.TemplateId == templateId || (isPrimary && d.TemplateId == null)), ct);

    /// <inheritdoc />
    public Task<bool> HasOwnRouteAsync(int projectId, int templateId, CancellationToken ct)
        => db.ApprovalRoutes
            .AsNoTracking()
            .AnyAsync(r => r.IsActive && r.ProjectId == projectId && r.TemplateId == templateId, ct);

    /// <inheritdoc />
    public Task<int> CountAsync(int projectId, CancellationToken ct)
        => db.ProjectTemplates.AsNoTracking().CountAsync(pt => pt.ProjectId == projectId, ct);

    /// <inheritdoc />
    public void Attach(int projectId, int templateId, int currentTemplateVersionId)
        => db.ProjectTemplates.Add(new ProjectTemplate(projectId, templateId, currentTemplateVersionId));

    /// <inheritdoc />
    public async Task DetachAsync(int projectId, int templateId, CancellationToken ct)
    {
        await db.ProjectTemplateVersions
            .Where(t => t.ProjectId == projectId && t.TemplateId == templateId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        await db.ProjectTemplates
            .Where(t => t.ProjectId == projectId && t.TemplateId == templateId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
