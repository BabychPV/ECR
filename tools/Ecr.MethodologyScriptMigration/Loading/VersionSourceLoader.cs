using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.MethodologyScriptMigration.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Ecr.MethodologyScriptMigration.Loading;

/// <summary>
/// Збирає <see cref="VersionSource"/> версії методології з БАЗИ — лише читання (план, розд. 8.1–8.2).
/// </summary>
/// <remarks>
/// ⛔ Нічого не пише й не відстежує змін: запити <c>AsNoTracking</c> (формули — через
/// <see cref="MethodologyStore.GetFormulasAsync"/>, де відстеження є, але <c>SaveChanges</c> не викликається).
/// Склад версії читають ТІ САМІ методи <see cref="MethodologyStore"/>, що й старий рушій (формули, константи,
/// правила, категорії, виходи, речовини, імпорти на дату чинності), а колонки статусу активності — ТОЙ САМИЙ
/// <see cref="RowActivityFilterSource"/>, що й прогін.
/// </remarks>
/// <param name="db">Контекст бази.</param>
/// <param name="formulaEngine">Рушій формул: потрібен лише для топологічного порядку чернеток.</param>
public sealed class VersionSourceLoader(EcrDbContext db, IFormulaEngine formulaEngine)
{
    private readonly VersionSourceReader _reader = new(db, formulaEngine);

    /// <summary>Усі версії (за потреби однієї методології): за кодом методології, датою, Id.</summary>
    /// <param name="methodologyCode">Код методології; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<VersionRef>> ListAsync(string? methodologyCode, CancellationToken ct)
    {
        var query = from v in db.MethodologyVersions.AsNoTracking()
                    join m in db.Methodologies.AsNoTracking() on v.MethodologyId equals m.Id
                    where methodologyCode == null || m.Code == methodologyCode
                    select new { v, m };
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        return
        [
            .. rows
                .OrderBy(r => r.m.Code, StringComparer.Ordinal)
                .ThenBy(r => r.v.EffectiveFrom ?? DateOnly.MaxValue)
                .ThenBy(r => r.v.Id)
                .Select(r => ToRef(r.v, r.m)),
        ];
    }

    /// <summary>Читає одну версію: те саме спільне читання (<see cref="IVersionSourceReader"/>), що й API перемикання.</summary>
    /// <param name="versionId">Id версії.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<LoadedVersion> LoadAsync(int versionId, CancellationToken ct) => _reader.LoadAsync(versionId, ct);

    private static VersionRef ToRef(MethodologyVersion v, Methodology m)
        => new(v.Id, m.Id, m.Code, v.Version, v.EffectiveFrom, v.Status, v.Level, v.Engine, m.Kind);
}
