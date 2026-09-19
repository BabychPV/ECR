using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IUnitStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class UnitStore(EcrDbContext db) : IUnitStore
{
    /// <inheritdoc />
    public Task<Unit?> FindUnitByCodeAsync(string code, CancellationToken ct)
        => db.Units.FirstOrDefaultAsync(u => u.Code == code, ct);

    /// <inheritdoc />
    public Task<bool> DimensionExistsAsync(byte dimensionId, CancellationToken ct)
        => db.Dimensions.AnyAsync(d => d.Id == dimensionId, ct);

    /// <inheritdoc />
    public void AddUnit(Unit unit) => db.Units.Add(unit);

    /// <inheritdoc />
    public Task<Unit?> FindUnitByIdAsync(int unitId, CancellationToken ct)
        => db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

    /// <inheritdoc />
    public void RemoveUnit(Unit unit) => db.Units.Remove(unit);

    /// <inheritdoc />
    public async Task<UsageResponse> FindUnitUsageAsync(int unitId, int take, CancellationToken ct)
    {
        var total = 0;
        var items = new List<UsageItemDto>();

        // Рахує джерело повністю, а в перелік бере лише те, що ще вміщається.
        // ⚠ Джерело приходить уже ВПОРЯДКОВАНИМ: `OrderBy` після проєкції в
        // конструктор запису EF не перекладає.
        async Task AddAsync(string kind, IQueryable<Hit> source)
        {
            total += await source.CountAsync(ct).ConfigureAwait(false);
            if (items.Count >= take)
            {
                return;
            }

            var page = await source.Take(take - items.Count).ToListAsync(ct).ConfigureAwait(false);
            items.AddRange(page.Select(h => new UsageItemDto(
                kind, h.Id.ToString(CultureInfo.InvariantCulture), h.Label, h.Route)));
        }

        await AddAsync(
            "templateColumn",
            from c in db.ColumnDefs
            where c.UnitId == unitId
            join t in db.TableDefs on c.TableDefId equals t.Id
            join s in db.SheetDefs on t.SheetDefId equals s.Id
            join v in db.TemplateVersions on s.TemplateVersionId equals v.Id
            orderby c.Id
            select new Hit(
                c.Id, t.Code + "." + c.Code, "/admin/templates/" + v.TemplateId + "/versions/" + v.Id))
            .ConfigureAwait(false);

        await AddAsync(
            "registryField",
            from f in db.RegistryFieldDefs
            where f.UnitId == unitId
            join r in db.RegistryDefs on f.RegistryDefId equals r.Id
            orderby f.Id
            select new Hit(f.Id, r.Code + "." + f.Code, "/admin/registries/" + r.Code + "/definition"))
            .ConfigureAwait(false);

        await AddAsync(
            "methodologyConstant",
            from c in db.MethodologyConstants
            where c.UnitId == unitId
            join v in db.MethodologyVersions on c.MethodologyVersionId equals v.Id
            orderby c.Id
            select new Hit(c.Id, c.Code, "/admin/methodologies/" + v.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            "methodologyFormula",
            from f in db.MethodologyFormulas
            where f.OutputUnitId == unitId
            join v in db.MethodologyVersions on f.MethodologyVersionId equals v.Id
            orderby f.Id
            select new Hit(f.Id, f.Code, "/admin/methodologies/" + v.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            "methodologyOutput",
            from o in db.MethodologyOutputs
            where o.UnitId == unitId
            join v in db.MethodologyVersions on o.MethodologyVersionId equals v.Id
            orderby o.Id
            select new Hit(o.Id, o.Code, "/admin/methodologies/" + v.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            "fieldMap",
            db.EntityFieldMaps
                .Where(m => m.SourceUnitId == unitId || m.TargetUnitId == unitId)
                .OrderBy(m => m.Id)
                .Select(m => new Hit(m.Id, m.SourceField, "/admin/mapping")))
            .ConfigureAwait(false);

        await AddAsync(
            "unitConversion",
            from c in db.UnitConversions
            where c.FromUnitId == unitId || c.ToUnitId == unitId
            join f in db.Units on c.FromUnitId equals f.Id
            join t in db.Units on c.ToUnitId equals t.Id
            orderby c.Id
            select new Hit(c.Id, f.Code + " -> " + t.Code, "/admin/units"))
            .ConfigureAwait(false);

        await AddAsync(
            "derivedUnit",
            db.Units
                .Where(u => u.NumeratorUnitId == unitId || u.DenominatorUnitId == unitId)
                .OrderBy(u => u.Id)
                .Select(u => new Hit(u.Id, u.Code, "/admin/units")))
            .ConfigureAwait(false);

        // ⚠ Базова одиниця розмірності: через неї йде кожна конверсія, тож
        // вона не видаляється ніколи — і це видно тим самим переліком, без
        // окремого правила в обробнику.
        await AddAsync(
            "dimensionBase",
            db.Dimensions
                .Where(d => d.BaseUnitId == unitId)
                .OrderBy(d => d.Id)
                .Select(d => new Hit(d.Id, d.Code, null)))
            .ConfigureAwait(false);

        // Таблиці даних: один рядок на таблицю, без підрахунку (див. порт).
        foreach (var (table, any) in new (string, Func<Task<bool>>)[]
        {
            ("doc.CellValue", () => db.CellValues.AnyAsync(x => x.ValueUnitId == unitId, ct)),
            ("dic.RegistryValue", () => db.RegistryValues.AnyAsync(x => x.ValueUnitId == unitId, ct)),
            ("calc.CalculationResult", () => db.CalculationResults.AnyAsync(x => x.UnitId == unitId, ct)),
            ("ext.RawData", () => db.RawDataPoints.AnyAsync(x => x.UnitId == unitId, ct)),
        })
        {
            if (!await any().ConfigureAwait(false))
            {
                continue;
            }

            total++;
            if (items.Count < take)
            {
                items.Add(new UsageItemDto("data", table, table, null));
            }
        }

        return new UsageResponse(total, items);
    }

    /// <summary>Проміжний рядок пошуку посилань.</summary>
    private sealed record Hit(int Id, string Label, string? Route);
}
