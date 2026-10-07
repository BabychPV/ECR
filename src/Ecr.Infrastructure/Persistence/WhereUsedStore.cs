// src/Ecr.Infrastructure/Persistence/WhereUsedStore.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IWhereUsedStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class WhereUsedStore(EcrDbContext db) : IWhereUsedStore
{
    /// <inheritdoc />
    public async Task<int?> FindConstantMethodologyAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await (from c in db.MethodologyConstants
                  where c.MethodologyVersionId == methodologyVersionId && c.Code == code
                  join v in db.MethodologyVersions on c.MethodologyVersionId equals v.Id
                  select (int?)v.MethodologyId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<VersionFormulaText>> ListVersionFormulasAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyFormulas
            .Where(f => f.MethodologyVersionId == methodologyVersionId)
            .OrderBy(f => f.Id)
            .Select(f => new VersionFormulaText(f.Id, f.Code, f.Expression))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> ColumnExistsAsync(int columnDefId, CancellationToken ct)
        => db.ColumnDefs.AnyAsync(c => c.Id == columnDefId && !c.IsDeleted, ct);

    /// <inheritdoc />
    public async Task<UsageResponse> FindColumnUsageAsync(int columnDefId, int take, CancellationToken ct)
    {
        var hits = new List<UsageItemDto>();
        var total = 0;

        // Та сама схема, що в `UnitStore`: рахуємо все, у перелік — що вміщається.
        async Task AddAsync(string kind, IQueryable<Hit> source)
        {
            total += await source.CountAsync(ct).ConfigureAwait(false);
            if (hits.Count < take)
            {
                var page = await source.Take(take - hits.Count).ToListAsync(ct).ConfigureAwait(false);
                hits.AddRange(page.Select(h => Item(kind, h)));
            }
        }

        // ⚠ `cfg.FormulaDependency` наповнюється публікацією версії шаблону:
        // чернетка, яку ще не публікували, своїх залежностей тут не має.
        var dependencies = db.FormulaDependencies.Where(d => d.ColumnDefId == columnDefId);

        await AddAsync(
            UsageKinds.TemplateFormula,
            from f in db.FormulaDefs
            where !f.IsDeleted && dependencies.Any(d => d.FormulaDefId == f.Id)
            join t in db.TableDefs on f.TableDefId equals t.Id
            join s in db.SheetDefs on t.SheetDefId equals s.Id
            join v in db.TemplateVersions on s.TemplateVersionId equals v.Id
            orderby f.Id
            select new Hit(
                f.Id,
                t.Code + "." + (db.ColumnDefs.Where(c => c.Id == f.ColumnDefId).Select(c => c.Code).FirstOrDefault() ?? "*"),
                "/admin/templates/" + v.TemplateId + "/versions/" + v.Id,
                t.NameL10n))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.CalculationBinding,
            from b in db.CalculationBindings
            where b.ColumnDefId == columnDefId || dependencies.Any(d => d.BindingId == b.Id)
            join m in db.Methodologies on b.MethodologyId equals m.Id
            orderby b.Id
            select new Hit(b.Id, m.Code + "." + b.OutputCode, "/admin/methodologies/" + m.Id + "/versions", m.NameL10n))
            .ConfigureAwait(false);

        // ⛔ C1: правила й вимоги ключуються Id колонки тієї версії шаблону, де їх писали, а колонка клон-версії має
        // інший Id. «Де використовується» колонки — це й посилання на її відповідники за шляхом у інших версіях
        // того самого шаблону; інакше для колонок клону зв'язок з методологіями ховається.
        var family = await ColumnFamilyAsync(columnDefId, ct).ConfigureAwait(false);

        await AddAsync(
            UsageKinds.MethodologyRequiredInput,
            from r in db.MethodologyRequiredInputs
            where family.Contains(r.ColumnDefId)
            join v in db.MethodologyVersions on r.MethodologyVersionId equals v.Id
            join m in db.Methodologies on v.MethodologyId equals m.Id
            orderby r.Id
            select new Hit(r.Id, m.Code + " " + v.Version, "/admin/methodologies/" + m.Id + "/versions", m.NameL10n))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.FieldMap,
            db.EntityFieldMaps
                .Where(m => m.TargetColumnDefId == columnDefId)
                .OrderBy(m => m.Id)
                .Select(m => new Hit(m.Id, m.SourceField, "/admin/mapping", null)))
            .ConfigureAwait(false);

        // ⛔ Ключі `MatchJson` правила — `ColumnDefId` рядком (`MethodologyRuleMatcher`).
        // SQL лише звужує кандидатів; остаточно рішення — за точним ключем
        // розібраного предиката, інакше `"12"` у ЗНАЧЕННІ або ключ `"123"`
        // зарахувалися б колонці 12.
        var keys = family.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(MethodologyRule Rule, int MethodologyId)>();
        foreach (var key in keys)
        {
            var found = await (
                    from r in db.MethodologyRules
                    where r.MatchJson.Contains("\"" + key + "\"")
                    join v in db.MethodologyVersions on r.MethodologyVersionId equals v.Id
                    orderby r.Id
                    select new { Rule = r, v.MethodologyId })
                .ToListAsync(ct).ConfigureAwait(false);
            candidates.AddRange(found.Select(f => (f.Rule, f.MethodologyId)));
        }

        var rules = candidates
            .Where(c => MethodologyRuleMatcher.Compile([c.Rule])[0].Pairs?.Any(p => keys.Contains(p.Key)) == true)
            .DistinctBy(c => c.Rule.Id)
            .OrderBy(c => c.Rule.Id)
            .ToList();

        total += rules.Count;
        hits.AddRange(rules.Take(Math.Max(0, take - hits.Count)).Select(c => Item(
            UsageKinds.MethodologyRule,
            new Hit(c.Rule.Id, c.Rule.Code, "/admin/methodologies/" + c.MethodologyId + "/versions", null))));

        return new UsageResponse(total, hits);
    }

    /// <summary>Сама колонка і її відповідники за шляхом «аркуш → таблиця → колонка» в інших версіях її шаблону.</summary>
    private async Task<List<int>> ColumnFamilyAsync(int columnDefId, CancellationToken ct)
    {
        var path = await (
                from column in db.ColumnDefs.AsNoTracking()
                where column.Id == columnDefId
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
                select new { version.TemplateId, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var family = new List<int> { columnDefId };
        if (path is null)
        {
            return family;
        }

        var siblings = await (
                from column in db.ColumnDefs.AsNoTracking()
                where column.Code == path.ColumnCode && column.Id != columnDefId
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                where table.Code == path.TableCode
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.Code == path.SheetCode
                join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
                where version.TemplateId == path.TemplateId
                select column.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        family.AddRange(siblings);
        return family;
    }

    private static UsageItemDto Item(string kind, Hit hit)
        => new UsageItemDto(kind, hit.Id.ToString(CultureInfo.InvariantCulture), hit.Label, hit.Route, hit.DisplayName());

    /// <summary>Проєкція одного посилання до перетворення на <see cref="UsageItemDto"/>.</summary>
    private sealed record Hit(int Id, string Label, string Route, LocalizedText? Title)
    {
        /// <summary>Назва для людини (ФВ-8.14); <c>null</c> — назви немає, клієнт покаже код.</summary>
        public string? DisplayName()
        {
            var name = Title?.Get("uk");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }
}
