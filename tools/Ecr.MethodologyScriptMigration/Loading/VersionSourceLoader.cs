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
    private readonly RowActivityFilterSource _activity = new(db);

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

    /// <summary>Читає одну версію: спільне читання (<see cref="IVersionSourceReader"/>) плюс речення <c>rows</c>.</summary>
    /// <param name="versionId">Id версії.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<LoadedVersion> LoadAsync(int versionId, CancellationToken ct)
    {
        var loaded = await _reader.LoadAsync(versionId, ct).ConfigureAwait(false);
        if (loaded.Ref.Kind == MethodologyKind.Library)
        {
            return loaded;
        }

        var issues = loaded.Issues.ToList();
        var methodology = await db.Methodologies.AsNoTracking().FirstAsync(m => m.Id == loaded.Ref.MethodologyId, ct)
            .ConfigureAwait(false);
        var clauses = await BuildRowsClausesAsync(versionId, methodology, loaded.Source.Rules, issues, ct).ConfigureAwait(false);
        return new LoadedVersion(loaded.Ref, loaded.Source with { RowsClauses = clauses }, issues);
    }

    private static VersionRef ToRef(MethodologyVersion v, Methodology m)
        => new(v.Id, m.Id, m.Code, v.Version, v.EffectiveFrom, v.Status, v.Level, v.Engine, m.Kind);

    // ---- Речення rows ----

    private async Task<IReadOnlyList<SourceRowsClause>> BuildRowsClausesAsync(
        int versionId, Methodology methodology, IReadOnlyList<SourceRule> rules, List<ConversionIssue> issues, CancellationToken ct)
    {
        // Усі прив'язки методології (активні й ні) → таблиці через ColumnDef → TableDef.
        var tables = await (
                from b in db.CalculationBindings.AsNoTracking()
                join c in db.ColumnDefs.AsNoTracking() on b.ColumnDefId equals c.Id
                join t in db.TableDefs.AsNoTracking() on c.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                join tv in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals tv.Id
                join tpl in db.Templates.AsNoTracking() on tv.TemplateId equals tpl.Id
                where b.MethodologyId == methodology.Id
                select new { TableDefId = t.Id, TableCode = t.Code, TemplateCode = tpl.Code })
            .Distinct().ToListAsync(ct).ConfigureAwait(false);
        if (tables.Count == 0)
        {
            return [];
        }

        var tableDefIds = tables.Select(t => t.TableDefId).Distinct().ToList();

        // Код таблиці однозначний лише в межах шаблону: якщо той самий код у кількох шаблонах — пишемо Шаблон.Таблиця.
        var templatesByTable = tables.GroupBy(t => t.TableCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.TemplateCode).Distinct(StringComparer.Ordinal).ToList());
        var tableNames = new Dictionary<int, string>();
        foreach (var t in tables)
        {
            tableNames[t.TableDefId] = templatesByTable[t.TableCode].Count > 1 ? t.TemplateCode + "." + t.TableCode : t.TableCode;
        }

        var allColumns = await db.ColumnDefs.AsNoTracking().Where(c => tableDefIds.Contains(c.TableDefId) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Code, c.TableDefId }).ToListAsync(ct).ConfigureAwait(false);
        var columnCodesByTableDef = allColumns.GroupBy(c => c.TableDefId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Code).ToHashSet(StringComparer.Ordinal));

        // ---- Фільтр активності: колонки статусу — так, як їх знаходить рантайм ----
        var activeByTableDef = new Dictionary<int, List<SourceActiveColumn>>();
        if (methodology.RowActivityFilter is not null)
        {
            var resolution = await _activity.ResolveForVersionAsync(versionId, tableDefIds, ct).ConfigureAwait(false);
            if (resolution?.ActiveEntryIdText is null)
            {
                issues.Add(new ConversionIssue(
                    IssueSeverity.Warning, "activityFilterInactive",
                    $"Methodology.RowActivityFilter «{methodology.RowActivityFilter}» does not resolve to a registry entry: "
                    + "the old engine ignores it, so no 'active when' is generated."));
            }
            else
            {
                var entryId = long.Parse(resolution.ActiveEntryIdText, CultureInfo.InvariantCulture);
                var entry = await db.RegistryEntries.AsNoTracking().FirstAsync(e => e.Id == entryId, ct).ConfigureAwait(false);
                var registryCode = await db.RegistryDefs.AsNoTracking().Where(r => r.Id == entry.RegistryDefId)
                    .Select(r => r.Code).FirstAsync(ct).ConfigureAwait(false);
                var codeById = allColumns.ToDictionary(c => c.Id, c => c.Code);
                foreach (var tableDefId in tableDefIds)
                {
                    var statusColumns = resolution.ColumnsOf(tableDefId);
                    if (statusColumns.Count > 0)
                    {
                        activeByTableDef[tableDefId] =
                        [
                            .. statusColumns.Select(id => codeById[id]).Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)
                                .Select(code => new SourceActiveColumn(code, registryCode, entry.Code)),
                        ];
                    }
                }
            }
        }

        // ---- По коду таблиці: зведення за всіма її описами (версіями шаблону) ----
        var ruleColumns = rules.SelectMany(r => r.Pairs).Select(p => p.ColumnCode).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var perTable = new List<(string Name, string Key, List<SourceActiveColumn> Active)>();
        foreach (var group in tableDefIds.GroupBy(id => tableNames[id]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var activeSets = group
                .Select(id => activeByTableDef.GetValueOrDefault(id) ?? [])
                .Select(list => string.Join("|", list.Select(a => a.ColumnCode)))
                .Distinct(StringComparer.Ordinal).ToList();
            if (activeSets.Count > 1)
            {
                issues.Add(new ConversionIssue(
                    IssueSeverity.Error, "statusColumnsVary",
                    $"Table «{group.Key}»: its descriptions in different template versions have different status columns, "
                    + "so one 'active when' cannot reproduce the old filter.",
                    group.Key));
            }

            var active = activeByTableDef.GetValueOrDefault(group.First()) ?? [];
            var availability = string.Concat(ruleColumns.Select(code =>
                group.All(id => columnCodesByTableDef.GetValueOrDefault(id)?.Contains(code) == true) ? '1' : '0'));
            var key = string.Join("|", active.Select(a => a.ColumnCode + "=" + a.RegistryCode + ":" + a.EntryCode)) + "#" + availability;
            perTable.Add((group.Key, key, active));
        }

        // ---- Таблиці з однаковими статус-колонками й однаковою наявністю колонок правил — одне речення ----
        var clauses = new List<SourceRowsClause>();
        foreach (var group in perTable.GroupBy(t => t.Key, StringComparer.Ordinal).OrderBy(g => g.First().Name, StringComparer.Ordinal))
        {
            clauses.Add(new SourceRowsClause([.. group.Select(t => t.Name)], rules, group.First().Active));
        }

        return clauses;
    }
}
