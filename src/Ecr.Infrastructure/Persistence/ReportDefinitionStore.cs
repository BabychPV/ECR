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

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Два запити, а не <c>Include</c>: <c>ReportVersion</c> не має
    /// навігаційної властивості на <c>ReportDef</c> (лише зовнішній ключ,
    /// <c>FK_RV_Def</c>), тому з'єднання складається тут. Стеля стоїть на
    /// обох — описів звітів десятки (<c>ФВ-10.7</c> називає сім державних
    /// форм), а версій за роки накопичуються сотні.
    /// </remarks>
    public async Task<IReadOnlyList<ReportDefinitionDto>> ListAsync(CancellationToken ct)
    {
        var defs = await db.ReportDefs
            .AsNoTracking()
            .OrderBy(d => d.Code)
            .Take(MaxDefinitions)
            .Select(d => new { d.Id, d.Code, d.NameL10n, d.IsRegulatory, d.IsActive })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = defs.ConvertAll(d => d.Id);

        var versions = await db.ReportVersions
            .AsNoTracking()
            .Where(v => ids.Contains(v.ReportDefId))
            .OrderByDescending(v => v.CreatedAt)
            .Take(MaxVersions)
            .Select(v => new
            {
                v.Id, v.ReportDefId, v.Version, v.Status, v.ColumnsJson, v.RulesJson, v.CreatedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return defs.ConvertAll(d => new ReportDefinitionDto(
            d.Id,
            d.Code,
            d.NameL10n,
            d.IsRegulatory,
            d.IsActive,
            versions
                .Where(v => v.ReportDefId == d.Id)
                .Select(v => new ReportVersionDto(
                    v.Id, v.Version, v.Status.ToString(), v.ColumnsJson, v.RulesJson, v.CreatedAt))
                .ToList()));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        // ⚠ Без фільтра на `IsActive`: код тримає УНІКАЛЬНИЙ індекс
        // (`UQ_ReportDef`), і виведений з обігу опис однаково займає його.
        // Інакше «заведіть звіт заново» падало б помилкою провайдера замість
        // зрозумілої відмови.
        return db.ReportDefs.AsNoTracking().AnyAsync(d => d.Code == code, ct);
    }

    /// <summary>Стеля переліку описів звітів.</summary>
    private const int MaxDefinitions = 500;

    /// <summary>Стеля переліку версій в одному запиті.</summary>
    private const int MaxVersions = 2000;
}
