// src/Ecr.Infrastructure/DocumentDependencies/DependencyGraphProvider.cs
using System.Globalization;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.DocumentDependencies;

/// <summary>
/// B179 L1: <see cref="IDependencyGraphProvider"/> — читає ребра, які вже пише публікація, і кешує граф під ключем
/// ревізії (D-16, як <see cref="MetadataCache"/>).
/// </summary>
/// <remarks>
/// <para>
/// Версії шаблонів у графі — опубліковані плюс ті, що діють за незакритим періодом документа (<c>VersionOf</c> /
/// <c>TemplateVersionOf</c>, без <c>doc.TableInstance</c>). Формули версії: <c>cfg.FormulaDependency</c> вид 2 без
/// таблиці (RT-21, <c>RowKey</c> = код довідника) — лише вони; версія без жодного такого ребра (публікація до RT-21) —
/// запасний шлях: розбір УСІХ її формул у пам'яті (AST, <see cref="RegistryReadScopes"/>). Обсяг за періодом —
/// завжди з AST (рядки залежностей періоду не несуть).
/// </para>
/// <para>
/// ⛔ <c>cfg.DocumentSourceBindingConsumer</c> граф НЕ читає (дизайн §7: ручний перелік споживачів виводиться з ужитку).
/// Таблиці методологій — лише через <see cref="IMethodologyRegistryEdges"/> (сторож
/// <c>DependencyGraphMethodologyReadGuardTests</c>).
/// </para>
/// <para>
/// ⚠ Інвалідація — ключем: ревізія охоплює публікацію/виведення версій шаблонів, документи й незакриті періоди,
/// зв'язки джерел, визначення довідників і правила, публікацію версій методологій і прив'язки (адаптер). Читається
/// не частіше за вікно ревізії (<see cref="CacheLifetimes.Revision"/>). Стеля життя запису — <see cref="GraphLifetime"/>
/// (зміна шкали версій проєкту ревізію не зачіпає).
/// </para>
/// </remarks>
public sealed class DependencyGraphProvider(
    EcrDbContext db,
    IMemoryCache memory,
    IFormulaEngine formulaEngine,
    IMethodologyRegistryEdges methodologies,
    CacheLifetimes? lifetimes = null) : IDependencyGraphProvider
{
    /// <summary>Стеля життя збудованого графа.</summary>
    public static readonly TimeSpan GraphLifetime = TimeSpan.FromMinutes(30);

    private const string RevisionKey = "dependency-graph:rev";

    private const int MaxBindings = 1000;

    private static readonly SingleFlight<DependencyGraphSnapshot> Flight = new();

    /// <inheritdoc />
    public async Task<DependencyGraphSnapshot> GetAsync(CancellationToken ct)
    {
        var revision = await RevisionAsync(ct).ConfigureAwait(false);
        var key = "dependency-graph:" + revision;
        if (memory.TryGetValue(key, out DependencyGraphSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        return await Flight.RunAsync(
                key,
                async token =>
                {
                    var graph = DependencyGraphAssembler.Assemble(await ReadAsync(revision, token).ConfigureAwait(false));
                    memory.Set(key, graph, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = GraphLifetime });
                    return graph;
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> FillsRegistryAsync(int templateId, CancellationToken ct)
        => db.DocumentSourceBindings.AsNoTracking().AnyAsync(b => b.SourceTemplateId == templateId, ct);

    private async Task<string> RevisionAsync(CancellationToken ct)
    {
        var window = (lifetimes ?? CacheLifetimes.Default).Revision;
        if (window > TimeSpan.Zero && memory.TryGetValue(RevisionKey, out string? memoized) && memoized is not null)
        {
            return memoized;
        }

        var versions = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Status != TemplateVersionStatus.Draft)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Published = g.Max(v => v.PublishedAt), Deprecated = g.Max(v => v.DeprecatedAt) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var documents = await db.Documents.AsNoTracking()
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Max = g.Max(d => d.Id) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var openPeriods = await db.Periods.AsNoTracking().CountAsync(p => p.State != PeriodState.Closed, ct).ConfigureAwait(false);
        var bindings = await db.DocumentSourceBindings.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(b => (long)b.Id * (b.SourceTemplateId + b.RegistryDefId)), Updated = g.Max(b => b.UpdatedAt) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var registries = await db.RegistryDefs.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Definition = g.Sum(r => (long)r.DefinitionVersion), Updated = g.Max(r => r.DefinitionUpdatedAt) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var rules = await db.RegistryUses.AsNoTracking()
            .Where(u => u.SourceKind == RegistryUse.RegistryRuleSource)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Max = g.Max(u => u.Id) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var methodology = await methodologies.RevisionAsync(ct).ConfigureAwait(false);

        var revision = string.Create(
            CultureInfo.InvariantCulture,
            $"tv{versions?.Count}:{versions?.Published?.Ticks}:{versions?.Deprecated?.Ticks}|d{documents?.Count}:{documents?.Max}:{openPeriods}"
            + $"|sb{bindings?.Count}:{bindings?.Sum}:{bindings?.Updated?.Ticks}|r{registries?.Count}:{registries?.Definition}:{registries?.Updated?.Ticks}"
            + $"|rr{rules?.Count}:{rules?.Max}|{methodology}");

        if (window > TimeSpan.Zero)
        {
            memory.Set(RevisionKey, revision, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = window });
        }

        return revision;
    }

    private async Task<DependencyGraphInputs> ReadAsync(string revision, CancellationToken ct)
    {
        var diagnostics = new List<DependencyGraphDiagnostic>();

        var inUse = await (
                from document in db.Documents.AsNoTracking()
                join period in db.Periods.AsNoTracking() on document.ProjectId equals period.ProjectId
                where period.State != PeriodState.Closed
                select document.TemplateId == null
                    ? EcrDbContext.VersionOf(document.ProjectId, period.PeriodKeyValue)
                    : EcrDbContext.TemplateVersionOf(document.ProjectId, document.TemplateId.Value, period.PeriodKeyValue))
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var versions = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Status == TemplateVersionStatus.Published || inUse.Contains(v.Id))
            .Select(v => new { v.Id, v.TemplateId })
            .ToDictionaryAsync(v => v.Id, v => v.TemplateId, ct).ConfigureAwait(false);
        var versionIds = versions.Keys.ToList();

        var registries = await db.RegistryDefs.AsNoTracking()
            .Select(r => new { r.Id, r.Code })
            .ToDictionaryAsync(r => r.Id, r => r.Code, ct).ConfigureAwait(false);
        var registryByCode = registries.GroupBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(r => r.Key), StringComparer.OrdinalIgnoreCase);

        var templateReads = await TemplateReadsAsync(versions, versionIds, registryByCode, diagnostics, ct).ConfigureAwait(false);

        // Зв'язок одиниці (зараз одна); стеля — проти «віддати все» (правило 6), не обмеження продукту.
        var bindings = await db.DocumentSourceBindings.AsNoTracking()
            .OrderBy(b => b.Id)
            .Take(MaxBindings)
            .Select(b => new SourceBindingRow(b.Id, b.SourceTemplateId, b.RegistryDefId, b.SourceState))
            .ToListAsync(ct).ConfigureAwait(false);

        var rules = await (
                from use in db.RegistryUses.AsNoTracking()
                where use.SourceKind == RegistryUse.RegistryRuleSource
                join rule in db.RegistryRuleDefs.AsNoTracking() on use.SourceId equals rule.Id
                where rule.IsActive
                select new RegistryRuleRead(rule.Id, rule.RegistryDefId, use.RegistryDefId))
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var methodologyReads = new List<MethodologyScopedRead>();
        var methodologyCodes = new Dictionary<int, string>();
        var methodologySeen = new HashSet<(int Version, string Formula, int Registry)>();
        foreach (var row in await methodologies.ListReadsAsync(ct).ConfigureAwait(false))
        {
            // Шляхи полів (FieldPath) однієї формули — один доказ.
            if (!methodologySeen.Add((row.MethodologyVersionId, row.FormulaCode, row.RegistryDefId)))
            {
                continue;
            }

            methodologyCodes[row.MethodologyId] = row.MethodologyCode;
            var scope = PeriodScope.All;
            if (row.Expression is not null && registries.TryGetValue(row.RegistryDefId, out var code))
            {
                var parsed = formulaEngine.Parse(row.Expression, ExpressionDialect.Methodology);
                scope = parsed.Expression is null
                    ? Failed(diagnostics, $"methodology version {row.MethodologyVersionId} formula {row.FormulaCode}")
                    : RegistryReadScopes.Collect(parsed.Expression.Root, ExpressionDialect.Methodology).GetValueOrDefault(code, PeriodScope.All);
            }

            methodologyReads.Add(new MethodologyScopedRead(row.MethodologyId, row.MethodologyVersionId, row.RegistryDefId, row.FormulaCode, scope));
        }

        var calculationBindings = await methodologies.ListBindingsAsync(ct).ConfigureAwait(false);
        var bindingTables = calculationBindings.Select(b => b.TableDefId).Distinct().ToList();
        var tableTemplates = await (
                from table in db.TableDefs.AsNoTracking()
                where bindingTables.Contains(table.Id)
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where versionIds.Contains(sheet.TemplateVersionId)
                select new { table.Id, sheet.TemplateVersionId })
            .ToDictionaryAsync(t => t.Id, t => versions[t.TemplateVersionId], ct).ConfigureAwait(false);
        var writes = new List<MethodologyTemplateWrite>();
        foreach (var binding in calculationBindings.Where(b => tableTemplates.ContainsKey(b.TableDefId)))
        {
            methodologyCodes[binding.MethodologyId] = binding.MethodologyCode;
            writes.Add(new MethodologyTemplateWrite(
                binding.MethodologyId, tableTemplates[binding.TableDefId], binding.CalculationBindingId,
                $"table {binding.TableDefId} column {binding.ColumnDefId} output {binding.OutputCode}"));
        }

        var templateIds = versions.Values.Concat(bindings.Select(b => b.SourceTemplateId)).Distinct().ToList();
        var templateCodes = await db.Templates.AsNoTracking()
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code })
            .ToDictionaryAsync(t => t.Id, t => t.Code, ct).ConfigureAwait(false);

        return new DependencyGraphInputs(
            revision, templateCodes, registries, methodologyCodes, bindings, templateReads, methodologyReads, writes, rules, diagnostics);
    }

    private async Task<List<TemplateRegistryRead>> TemplateReadsAsync(
        Dictionary<int, int> versions,
        List<int> versionIds,
        Dictionary<string, int> registryByCode,
        List<DependencyGraphDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var formulas = from formula in db.FormulaDefs.AsNoTracking()
                       where !formula.IsDeleted
                       join table in db.TableDefs.AsNoTracking() on formula.TableDefId equals table.Id
                       join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                       where versionIds.Contains(sheet.TemplateVersionId)
                       select new { formula.Id, sheet.TemplateVersionId, formula.TableDefId, formula.ColumnDefId, formula.Expression, formula.Dialect };

        var rows = await (
                from dependency in db.FormulaDependencies.AsNoTracking()
                where dependency.DependsOnKind == DependencyExtractor.KindRegistry
                join formula in formulas on dependency.FormulaDefId equals (int?)formula.Id
                select new { Formula = formula, dependency.TableDefId, dependency.RowKey, dependency.ColumnDefId })
            .ToListAsync(ct).ConfigureAwait(false);

        var lookupColumns = rows.Where(r => r.TableDefId is not null && r.ColumnDefId is not null).Select(r => r.ColumnDefId!.Value).Distinct().ToList();
        var lookupRegistry = await db.ColumnDefs.AsNoTracking()
            .Where(c => lookupColumns.Contains(c.Id) && c.LookupRegistryDefId != null)
            .ToDictionaryAsync(c => c.Id, c => c.LookupRegistryDefId!.Value, ct).ConfigureAwait(false);

        var reads = new List<TemplateRegistryRead>();
        var scopes = new Dictionary<int, IReadOnlyDictionary<string, PeriodScope>?>();
        var seen = new HashSet<(int Formula, int Registry)>();
        foreach (var row in rows)
        {
            var f = row.Formula;
            var detail = $"table {f.TableDefId} column {f.ColumnDefId}";
            if (row.TableDefId is null && row.RowKey is not null)
            {
                if (!scopes.TryGetValue(f.Id, out var parsed))
                {
                    scopes[f.Id] = parsed = Scopes(f.Expression, f.Dialect, f.Id, diagnostics);
                }

                // Одна формула дає кілька ребер на той самий довідник (сам довідник і шляхи полів) — доказ один.
                if (Registry(row.RowKey, registryByCode, diagnostics) is { } registryId && seen.Add((f.Id, registryId)))
                {
                    reads.Add(new TemplateRegistryRead(versions[f.TemplateVersionId], f.TemplateVersionId, registryId,
                        parsed?.GetValueOrDefault(row.RowKey, PeriodScope.All) ?? PeriodScope.All, f.Id, "formulaDependency", detail));
                }
            }
            else if (row.ColumnDefId is { } column && lookupRegistry.TryGetValue(column, out var lookup) && seen.Add((f.Id, lookup)))
            {
                reads.Add(new TemplateRegistryRead(versions[f.TemplateVersionId], f.TemplateVersionId, lookup, PeriodScope.All, f.Id, "lookupColumn", detail));
            }
        }

        // Запасний шлях: версія без жодного ребра «довідник за кодом» (публікація до RT-21) — AST усіх її формул.
        var covered = rows.Where(r => r.TableDefId is null).Select(r => r.Formula.TemplateVersionId).ToHashSet();
        var fallbackIds = versionIds.Where(v => !covered.Contains(v)).ToList();
        var fallbackVersions = new SortedSet<int>();
        foreach (var f in await formulas.Where(f => fallbackIds.Contains(f.TemplateVersionId)).ToListAsync(ct).ConfigureAwait(false))
        {
            foreach (var (code, scope) in Scopes(f.Expression, f.Dialect, f.Id, diagnostics) ?? new Dictionary<string, PeriodScope>())
            {
                if (Registry(code, registryByCode, diagnostics) is { } registryId)
                {
                    reads.Add(new TemplateRegistryRead(versions[f.TemplateVersionId], f.TemplateVersionId, registryId, scope, f.Id, "ast",
                        $"table {f.TableDefId} column {f.ColumnDefId}"));
                    fallbackVersions.Add(f.TemplateVersionId);
                }
            }
        }

        diagnostics.AddRange(fallbackVersions.Select(v => new DependencyGraphDiagnostic("astFallback", $"template version {v}")));
        return reads;
    }

    private IReadOnlyDictionary<string, PeriodScope>? Scopes(
        string expression, ExpressionDialect dialect, int formulaId, List<DependencyGraphDiagnostic> diagnostics)
    {
        var parsed = formulaEngine.Parse(expression, dialect);
        if (parsed.Expression is null)
        {
            Failed(diagnostics, $"template formula {formulaId}");
            return null;
        }

        return RegistryReadScopes.Collect(parsed.Expression.Root, dialect);
    }

    private static int? Registry(string code, Dictionary<string, int> registryByCode, List<DependencyGraphDiagnostic> diagnostics)
    {
        if (registryByCode.TryGetValue(code, out var id))
        {
            return id;
        }

        diagnostics.Add(new DependencyGraphDiagnostic("unknownRegistry", code));
        return null;
    }

    private static PeriodScope Failed(List<DependencyGraphDiagnostic> diagnostics, string what)
    {
        diagnostics.Add(new DependencyGraphDiagnostic("parseFailed", what));
        return PeriodScope.All;
    }
}
