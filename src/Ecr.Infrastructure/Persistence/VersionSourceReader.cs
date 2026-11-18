using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Збирає <see cref="VersionSource"/> версії методології з БАЗИ — лише читання (план, розд. 8.1–8.2, 8.5). Спільне для
/// інструмента перенесення (<c>convert</c>/<c>pack</c>) і API (<c>script-migration/apply</c>): відбиток джерела рахується з ТИХ САМИХ даних.
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
public sealed class VersionSourceReader(EcrDbContext db, IFormulaEngine formulaEngine) : IVersionSourceReader
{
    private readonly MethodologyStore _store = new(db);
    private readonly RowActivityFilterSource _activity = new(db);

    /// <inheritdoc />
    /// <param name="versionId">Id версії.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<LoadedVersion> LoadAsync(int versionId, CancellationToken ct)
    {
        var version = await db.MethodologyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException($"Methodology version {versionId} not found.");
        var methodology = await db.Methodologies.AsNoTracking().FirstAsync(m => m.Id == version.MethodologyId, ct)
            .ConfigureAwait(false);
        var reference = ToRef(version, methodology);
        var issues = new List<ConversionIssue>();
        var effectiveFrom = version.EffectiveFrom ?? new DateOnly(2000, 1, 1);
        if (version.EffectiveFrom is null)
        {
            issues.Add(new ConversionIssue(
                IssueSeverity.Warning, "noEffectiveFrom",
                "The version has no EffectiveFrom (a draft); 2000-01-01 is used for the header and the constant windows."));
        }

        var isLibrary = methodology.Kind == MethodologyKind.Library;

        // ---- Формули ----
        var formulaEntities = await _store.GetFormulasAsync(versionId, ct).ConfigureAwait(false);
        IReadOnlyList<MethodologyFormula> orderedFormulas;
        var reordered = MethodologyFormulaOrder.IsUnordered(formulaEntities);
        if (reordered)
        {
            // Чернетка: порядку обчислення ще немає — той самий топологічний порядок, що й у GenericCalculationModule.
            orderedFormulas = MethodologyFormulaOrder.Topological(formulaEntities, formulaEngine);
            issues.Add(new ConversionIssue(
                IssueSeverity.Info, "formulaOrderDerived",
                "The version is not published (EvaluationOrder is not set): the topological order is used."));
        }
        else
        {
            orderedFormulas = [.. formulaEntities.OrderBy(f => f.EvaluationOrder).ThenBy(f => f.Id)];
        }

        var outputEntities = await _store.GetOutputsAsync(versionId, ct).ConfigureAwait(false);
        var constantEntities = await _store.GetConstantsAsync(versionId, ct).ConfigureAwait(false);

        // ---- Одиниці ----
        var unitIds = orderedFormulas.Where(f => f.OutputUnitId is not null).Select(f => f.OutputUnitId!.Value)
            .Concat(outputEntities.Select(o => o.UnitId))
            .Concat(constantEntities.Where(c => c.UnitId is > 0).Select(c => c.UnitId!.Value))
            .Distinct().ToList();
        var unitCodes = await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Code, ct).ConfigureAwait(false);

        string? UnitCode(int? id, string subject)
        {
            if (id is not { } value || value <= 0)
            {
                return null;
            }

            if (unitCodes.TryGetValue(value, out var code))
            {
                return code;
            }

            issues.Add(new ConversionIssue(
                IssueSeverity.Error, "unitNotFound", $"Unit {value.ToString(CultureInfo.InvariantCulture)} does not exist.", subject));
            return null;
        }

        // ---- Речовини ----
        var substanceEntities = await _store.GetSubstancesAsync(versionId, ct).ConfigureAwait(false);
        var entryIds = substanceEntities.Select(s => s.SubstanceEntryId)
            .Concat(constantEntities.Where(c => c.SubstanceEntryId is not null).Select(c => c.SubstanceEntryId!.Value))
            .Distinct().ToList();
        var substanceCodes = await SubstanceCodesAsync(entryIds, ct).ConfigureAwait(false);

        string? SubstanceCode(long? entryId, string subject)
        {
            if (entryId is not { } id)
            {
                return null;
            }

            if (substanceCodes.TryGetValue(id, out var code))
            {
                return code;
            }

            issues.Add(new ConversionIssue(
                IssueSeverity.Error, "substanceNotFound",
                $"Substance entry {id.ToString(CultureInfo.InvariantCulture)} does not exist.", subject));
            return null;
        }

        var formulas = new List<SourceFormula>();
        var position = 0;
        foreach (var f in orderedFormulas)
        {
            position++;
            formulas.Add(new SourceFormula(
                f.Code, f.Expression, reordered ? position : f.EvaluationOrder, f.Scope, f.IsVisible, f.ResultType,
                UnitCode(f.OutputUnitId, f.Code)));
        }

        var outputs = outputEntities
            .Select(o => new SourceOutput(o.Code, UnitCode(o.UnitId, o.Code) ?? string.Empty, o.IsPerSubstance, o.Ordinal))
            .ToList();
        var constants = constantEntities
            .Select(c => new SourceConstant(
                c.Code, c.Kind, c.Value, c.TextValue, UnitCode(c.UnitId, c.Code), c.Category,
                SubstanceCode(c.SubstanceEntryId, c.Code), c.ValidFrom, c.ValidTo))
            .ToList();
        var substances = substanceEntities
            .Select(s => SubstanceCode(s.SubstanceEntryId, "substances"))
            .Where(c => c is not null).Select(c => c!).ToList();

        var categories = await _store.GetCategoryCodesAsync(versionId, ct).ConfigureAwait(false);
        var categoryRule = await _store.GetCategoryRuleAsync(versionId, ct).ConfigureAwait(false);

        // ---- Імпорти: версія бібліотеки, чинна на дату чинності цієї версії ----
        var imports = new List<SourceImport>();
        foreach (var library in await _store.ResolveImportsAsync(versionId, effectiveFrom, ct).ConfigureAwait(false))
        {
            if (library.MethodologyVersionId is null)
            {
                issues.Add(new ConversionIssue(
                    IssueSeverity.Warning, "importWithoutVersion",
                    $"Library «{library.MethodologyCode}» has no version effective on {effectiveFrom:yyyy-MM-dd}: its members are unknown.",
                    library.MethodologyCode));
            }

            imports.Add(new SourceImport(library.MethodologyCode, [.. library.FormulaCodes]));
        }

        // ---- Життя версії ----
        DateOnly? next = null;
        if (version.EffectiveFrom is { } from)
        {
            var later = await db.MethodologyVersions.AsNoTracking()
                .Where(v => v.MethodologyId == version.MethodologyId && v.Id != versionId
                            && v.EffectiveFrom != null && v.EffectiveFrom > from
                            && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated))
                .Select(v => v.EffectiveFrom)
                .ToListAsync(ct).ConfigureAwait(false);
            next = later.Count == 0 ? null : later.Min();
        }

        // ---- Правила (речення rows будує інструмент: вони потрібні лише перетворювачеві) ----
        IReadOnlyList<SourceRule> rules = [];
        if (!isLibrary)
        {
            rules = await LoadRulesAsync(versionId, issues, ct).ConfigureAwait(false);
        }

        var source = new VersionSource
        {
            MethodologyCode = methodology.Code,
            VersionLabel = version.Version,
            IsLibrary = isLibrary,
            NumericMode = version.NumericMode,
            CalendarMode = version.CalendarMode,
            EffectiveFrom = effectiveFrom,
            NextEffectiveFrom = next,
            Formulas = formulas,
            Outputs = outputs,
            Constants = constants,
            Categories = [.. categories],
            CategoryRule = string.IsNullOrWhiteSpace(categoryRule) ? null : categoryRule,
            Substances = substances,
            Imports = imports,
            Rules = rules,
            RowActivityFilter = methodology.RowActivityFilter,
        };
        return new LoadedVersion(reference, source, issues);
    }

    private static VersionRef ToRef(MethodologyVersion v, Methodology m)
        => new(v.Id, m.Id, m.Code, v.Version, v.EffectiveFrom, v.Status, v.Level, v.Engine, m.Kind);

    /// <summary>
    /// Коди речовин за записами довідника — той самий вибір, що в <see cref="MethodologyStore.GetSubstanceCodesAsync"/>
    /// (поле <c>SUBSTANCE_CODE</c>, інакше код запису), але для довільного набору записів.
    /// </summary>
    private async Task<Dictionary<long, string>> SubstanceCodesAsync(List<long> entryIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (entryIds.Count == 0)
        {
            return result;
        }

        var entries = await db.RegistryEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Code, e.RegistryDefId }).ToListAsync(ct).ConfigureAwait(false);
        var registryIds = entries.Select(e => e.RegistryDefId).Distinct().ToList();
        var fieldIds = await db.RegistryFieldDefs.AsNoTracking()
            .Where(f => registryIds.Contains(f.RegistryDefId) && f.Code == "SUBSTANCE_CODE")
            .Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false);

        var fromField = new Dictionary<long, string>();
        if (fieldIds.Count > 0)
        {
            var values = await db.RegistryValues.AsNoTracking()
                .Where(v => entryIds.Contains(v.RegistryEntryId) && fieldIds.Contains(v.RegistryFieldDefId))
                .Select(v => new { v.RegistryEntryId, v.ValueString, v.ValueNumeric })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var value in values)
            {
                var text = !string.IsNullOrWhiteSpace(value.ValueString)
                    ? value.ValueString.Trim()
                    : value.ValueNumeric?.ToString("0.#########", CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                {
                    fromField[value.RegistryEntryId] = text;
                }
            }
        }

        foreach (var entry in entries)
        {
            result[entry.Id] = fromField.TryGetValue(entry.Id, out var field) ? field : entry.Code;
        }

        return result;
    }

    // ---- Правила відбору рядків ----

    private async Task<IReadOnlyList<SourceRule>> LoadRulesAsync(int versionId, List<ConversionIssue> issues, CancellationToken ct)
    {
        var ruleEntities = await _store.GetRulesAsync(versionId, ct).ConfigureAwait(false);
        var parsed = new List<(MethodologyRule Rule, IReadOnlyList<KeyValuePair<string, string?>> Pairs)>();
        foreach (var rule in ruleEntities)
        {
            if (MethodologyRuleMatcher.CanonicalPairs(rule.MatchJson) is not { } pairs)
            {
                issues.Add(new ConversionIssue(
                    IssueSeverity.Error, "ruleBroken",
                    $"Rule «{rule.Code}» has a broken predicate; the old engine reads it as matching nothing, which a script cannot express.",
                    rule.Code));
                continue;
            }

            parsed.Add((rule, pairs));
        }

        // Ключі — Id колонок, можливо ІНШОЇ версії шаблону: код колонки беремо прямо з cfg.ColumnDef за цим Id.
        var wanted = new HashSet<int>();
        foreach (var (_, pairs) in parsed)
        {
            foreach (var pair in pairs)
            {
                if (int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    wanted.Add(id);
                }
            }
        }

        var columns = wanted.Count == 0
            ? []
            : await db.ColumnDefs.AsNoTracking().Where(c => wanted.Contains(c.Id))
                .Select(c => new { c.Id, c.Code, c.LookupRegistryDefId }).ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);

        var registryIds = columns.Values.Where(c => c.LookupRegistryDefId is not null).Select(c => c.LookupRegistryDefId!.Value)
            .Distinct().ToList();
        var registryCodes = registryIds.Count == 0
            ? []
            : await db.RegistryDefs.AsNoTracking().Where(r => registryIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Code, ct).ConfigureAwait(false);

        var candidateEntryIds = new HashSet<long>();
        foreach (var (_, pairs) in parsed)
        {
            foreach (var pair in pairs)
            {
                if (int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    && columns.TryGetValue(id, out var column) && column.LookupRegistryDefId is not null
                    && long.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var entryId))
                {
                    candidateEntryIds.Add(entryId);
                }
            }
        }

        var entries = candidateEntryIds.Count == 0
            ? []
            : await db.RegistryEntries.AsNoTracking().Where(e => candidateEntryIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Code, e.RegistryDefId }).ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        var result = new List<SourceRule>();
        foreach (var (rule, pairs) in parsed)
        {
            var sourcePairs = new List<SourceMatchPair>();
            var ok = true;
            foreach (var pair in pairs)
            {
                if (!int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var columnId)
                    || !columns.TryGetValue(columnId, out var column))
                {
                    issues.Add(new ConversionIssue(
                        IssueSeverity.Error, "ruleColumnNotFound",
                        $"Rule «{rule.Code}»: the key «{pair.Key}» is not the Id of an existing column.", rule.Code));
                    ok = false;
                    continue;
                }

                var text = pair.Value ?? string.Empty;
                if (column.LookupRegistryDefId is { } registryId
                    && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var entryId)
                    && entries.TryGetValue(entryId, out var entry) && entry.RegistryDefId == registryId
                    && registryCodes.TryGetValue(registryId, out var registryCode))
                {
                    sourcePairs.Add(new SourceMatchPair(column.Code, SourceMatchValue.OfEntry(registryCode, entry.Code)));
                }
                else
                {
                    sourcePairs.Add(new SourceMatchPair(column.Code, SourceMatchValue.OfText(text)));
                }
            }

            if (ok)
            {
                result.Add(new SourceRule(sourcePairs));
            }
        }

        return result;
    }
}
