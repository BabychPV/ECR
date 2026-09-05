using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IReportDefinitionStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class ReportDefinitionStore(EcrDbContext db) : IReportDefinitionStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Береться лише <c>Published</c>. Зріз, побудований за чернеткою,
    /// виглядав би як звичайний і потрапив би в регуляторну вʼюху — а описи
    /// правлять саме тоді, коли ще не впевнені в них.
    /// </remarks>
    public async Task<int?> FindCurrentVersionIdAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var found = await db.ReportVersions
            .AsNoTracking()
            .Where(v => v.Status == TemplateVersionStatus.Published)
            .Where(v => db.ReportDefs.Any(d => d.Id == v.ReportDefId && d.Code == code && d.IsActive))

            // Найновіша опублікована: версій буває кілька, і чинна — остання.
            .OrderByDescending(v => v.CreatedAt)
            .Take(1)
            .Select(v => (int?)v.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.Count > 0 ? found[0] : null;
    }
}
