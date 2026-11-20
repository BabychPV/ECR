using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IScriptMigrationStore"/> над <see cref="EcrDbContext"/> (MS, план 8.5).</summary>
public sealed class ScriptMigrationStore(EcrDbContext db) : IScriptMigrationStore
{
    private const int Max = 10_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImporterVersion>> GetImporterVersionsAsync(int libraryMethodologyId, CancellationToken ct)
    {
        var rows = await (
                from i in db.MethodologyImports.AsNoTracking()
                join v in db.MethodologyVersions.AsNoTracking() on i.MethodologyVersionId equals v.Id
                join m in db.Methodologies.AsNoTracking() on v.MethodologyId equals m.Id
                where i.ImportedMethodologyId == libraryMethodologyId
                select new { VersionId = v.Id, MethodologyId = m.Id, MethodologyCode = m.Code, v.Version, v.Engine, v.Status })
            .Distinct()
            .OrderBy(x => x.MethodologyCode)
            .ThenBy(x => x.VersionId)
            .Take(Max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(x => new ImporterVersion(x.VersionId, x.MethodologyId, x.MethodologyCode, x.Version, x.Engine, x.Status))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateOnly>> GetLibraryEditionDatesAsync(int versionId, CancellationToken ct)
    {
        var version = await db.MethodologyVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => new { v.MethodologyId, v.EffectiveFrom })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (version?.EffectiveFrom is not { } start)
        {
            return [];
        }

        var later = await db.MethodologyVersions.AsNoTracking()
            .Where(v => v.MethodologyId == version.MethodologyId && v.Id != versionId
                        && v.EffectiveFrom != null && v.EffectiveFrom > start
                        && (v.Status == Domain.Enums.TemplateVersionStatus.Published
                            || v.Status == Domain.Enums.TemplateVersionStatus.Deprecated))
            .Select(v => v.EffectiveFrom)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await LibraryEditionDates.LoadAsync(db, start, later.Count == 0 ? null : later.Min(), ct).ConfigureAwait(false);
    }
}
