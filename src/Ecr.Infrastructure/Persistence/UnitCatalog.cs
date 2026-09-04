using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IUnitCatalog"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Знімок береться одним запитом і кешується на час запиту: перевірка
/// публікації обходить тисячі формул, і кожна може згадати <c>CONVERT</c>.
/// </remarks>
public sealed class UnitCatalog(EcrDbContext db) : IUnitCatalog
{
    /// <summary>Стеля вибірки: одиниць у системі десятки, не тисячі.</summary>
    private const int MaxUnits = 5_000;

    private UnitCatalogSnapshot? _cached;

    /// <inheritdoc />
    public async Task<UnitCatalogSnapshot> GetAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var units = await db.Units
            .AsNoTracking()
            .OrderBy(u => u.Code)
            .Take(MaxUnits)
            .Select(u => new UnitRow(u.Id, u.Code, u.DimensionId, u.NumeratorUnitId, u.DenominatorUnitId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Похідні одиниці складаються ПОСИЛАННЯМИ на чисельник і знаменник
        // (ФВ-16.2), а не розбираються з рядка «g/s». Розбір рядка означав би,
        // що «kg/h» і «kg / h» — різні одиниці, а «kgh» — теж якась.
        var derived = units
            .Where(u => u.NumeratorUnitId is not null && u.DenominatorUnitId is not null)
            .ToDictionary(
                u => $"{u.NumeratorUnitId}|{u.DenominatorUnitId}",
                u => u.Id,
                StringComparer.Ordinal);

        _cached = new UnitCatalogSnapshot(
            units.ToDictionary(
                u => u.Code,
                u => new UnitRef(u.Id, u.Code, u.DimensionId),
                StringComparer.OrdinalIgnoreCase),
            derived);

        return _cached;
    }

    /// <summary>Рядок довідника одиниць.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: анонімний розриває вираз фігурною дужкою,
    /// і архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» бачить
    /// половину інструкції без межі (`D1-08`).
    /// </remarks>
    private sealed record UnitRow(
        int Id, string Code, byte DimensionId, int? NumeratorUnitId, int? DenominatorUnitId);
}
