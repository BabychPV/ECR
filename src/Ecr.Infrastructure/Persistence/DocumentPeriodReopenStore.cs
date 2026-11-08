using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentPeriodReopenStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <param name="db">Контекст бази.</param>
public sealed class DocumentPeriodReopenStore(EcrDbContext db) : IDocumentPeriodReopenStore
{
    /// <summary>Правило ефективного стану — те саме, що в рішенні про запис (F-08).</summary>
    private static readonly PeriodStateCalculator PeriodStates = new();

    /// <inheritdoc />
    public async Task<DocumentPeriodFacts?> GetFactsAsync(
        long documentId, int periodKey, DateTime utcNow, CancellationToken ct)
    {
        // Один запит: документ → проєкт, період — підзапитом (як `PeriodStore.FindPeriodStateAsync`).
        var row = await (
                from document in db.Documents.AsNoTracking()
                where document.Id == documentId
                join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                select new
                {
                    project.Id,
                    project.Code,
                    project.Status,
                    // Версія ПЕРІОДУ (шкала версій проєкту), а не чинна: журнал описує зміну саме цього місяця.
                    TemplateVersionId = EcrDbContext.VersionOf(project.Id, periodKey),
                    project.TimeZoneId,
                    project.PeriodEnd,
                    project.YearGraceOffsetDays,
                    document.BusinessKey,
                    Period = db.Periods
                        .AsNoTracking()
                        .Where(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
                        .FirstOrDefault(),
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // ⚠ Ефективний стан лише для АКТИВНОГО проєкту — як у рішенні про запис (`A7-25`).
        PeriodState? state = row.Period is null
            ? null
            : row.Status == ProjectStatus.Active
                ? PeriodStates.Effective(
                    row.Period,
                    utcNow,
                    YearGraceWindow.For(
                        row.PeriodEnd, row.YearGraceOffsetDays, SiteTimeZone.Create(row.TimeZoneId).ToTimeZoneInfo()))
                : row.Period.State;

        return new DocumentPeriodFacts(
            row.Id, row.Code, row.Status, row.TemplateVersionId, row.TimeZoneId, row.BusinessKey, state);
    }

    /// <inheritdoc />
    public Task<DocumentPeriodReopen?> FindOpenAsync(long documentId, int periodKey, CancellationToken ct)
        => db.DocumentPeriodReopens
            .FirstOrDefaultAsync(x => x.DocumentId == documentId && x.PeriodKeyValue == periodKey && x.ClosedAt == null, ct);

    /// <inheritdoc />
    public Task<DocumentPeriodReopenView?> FindActiveViewAsync(
        long documentId, int periodKey, DateTime utcNow, CancellationToken ct)
        => (from x in db.DocumentPeriodReopens.AsNoTracking()
            where x.DocumentId == documentId
                  && x.PeriodKeyValue == periodKey
                  && x.ClosedAt == null
                  && x.OpenUntil > utcNow
            join u in db.Users.AsNoTracking() on x.OpenedByUserId equals u.Id into users
            from u in users.DefaultIfEmpty()
            select new DocumentPeriodReopenView(
                x.Id, x.PeriodKeyValue, x.OpenUntil, x.Reason, x.OpenedByUserId, u == null ? null : u.DisplayName, x.OpenedAt))
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public void Add(DocumentPeriodReopen exception) => db.DocumentPeriodReopens.Add(exception);
}
