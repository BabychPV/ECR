// src/Ecr.Application/Registries/Rules/RegistryRuleEngine.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Registries.Rules;

/// <summary>
/// Рушій правил довідника (RT-17a, FEATURE-REGISTRY-TABLES §6; дефект Д-4, <c>ФВ-8.18</c>):
/// виконує правила <c>RequiredWhen</c>, <c>Expression</c>, <c>CrossRegistry</c> на записаних
/// записах і на їхніх батьках композиції.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Викликається ПІСЛЯ <c>SaveChanges</c>, але ДО коміту — у тій самій транзакції, що й запис
/// (upsert, пакет, CSV). Знімок довідників (<see cref="IRegistrySnapshotLoader"/>) читає той самий
/// контекст БД, тож бачить стан ПІСЛЯ запису; порушення рівня <c>Error</c> — виняток, який
/// відкочує транзакцію (<see cref="RegistryRuleCheck.ThrowIfErrors"/>). Так агрегатне правило
/// (Σ складу = 100) перевіряє стан після всього пакета, а не після кожного рядка (§6, ⚠).
/// </para>
/// <para>
/// ⛔ Батьки: зміна (чи видалення) дочірнього запису композиції перевіряє правила батька, які
/// читають дочірній довідник, — рекурсивно вгору ланцюжком композиції. Звідки відомо, які
/// правила: розібраний вираз (перші аргументи функцій довідників). Ребра <c>cfg.RegistryUse</c>
/// з <c>SourceKind = 2</c> — той самий факт, записаний для «Де використано»; рушій їх не читає,
/// тож правило, збережене до них, не лишається без перевірки.
/// </para>
/// <para>
/// ⚠ <c>UniqueWithin</c> не виконується: його замінюють ключі (<c>R-5</c>, <c>D-154</c>), нові
/// відхиляє збереження опису. Правило, яке не розбирається (старі вирази з посиланнями на
/// комірки), — не тиша, а порушення <see cref="InvalidKey"/> свого рівня: мовчки пропущене
/// правило — рівно той дефект Д-4, який тут закривається.
/// </para>
/// <para>
/// ⚠ Дата знімка — сьогодні, а для записів темпорального довідника, чинних не сьогодні, — дата,
/// на яку запис чинний (початок вікна або останній день). Правило перевіряє запис у тому вікні,
/// де він живе; §6 («усі живі записи незалежно від вікна») для нетемпоральних довідників
/// виконується буквально, для темпоральних — наближено датою запису.
/// </para>
/// <para>
/// ⚠ Довідник без правил (і без батьків із правилами) коштує один запит на правила власного
/// довідника плюс по три на кожен рівень композиції; знімок не вантажиться.
/// </para>
/// </remarks>
public sealed class RegistryRuleEngine(
    IRegistryStore registries,
    IRegistryKeyStore keys,
    IRegistrySnapshotLoader snapshots,
    RegistryRuleCompiler compiler,
    Evaluator evaluator,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Ключ порушення: правило не виконалося (§7.1).</summary>
    public const string ViolatedKey = "registries.rules.violated";

    /// <summary>Ключ порушення: правило не вдалося виконати — вираз чи параметри неправильні.</summary>
    public const string InvalidKey = "registries.rules.invalid";

    /// <summary>Ключ відмови <c>422 ECR-REG-4221</c>.</summary>
    public const string RuleViolatedErrorKey = "err.ECR-REG-4221.ruleViolated";

    /// <summary>Скільки рівнів композиції вгору перевіряється — запобіжник від кола в даних.</summary>
    private const int MaxDepth = 16;

    /// <summary>Перевіряє правила записаних записів і їхніх батьків композиції.</summary>
    /// <param name="definition">Довідник, записи якого змінено.</param>
    /// <param name="changed">Записані (створені чи змінені) записи довідника.</param>
    /// <param name="removed">Видалені записи — їхні власні правила не перевіряються, батьківські — так.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Усі порушення; відмову за рівнем <c>Error</c> робить викликач.</returns>
    public async Task<RegistryRuleCheck> CheckAsync(
        RegistryDef definition,
        IReadOnlyCollection<long> changed,
        IReadOnlyCollection<long> removed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(removed);

        var plan = await PlanAsync(definition, changed, removed, ct).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            return RegistryRuleCheck.None;
        }

        var all = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
        var idsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var registry in all)
        {
            idsByCode[registry.Code] = registry.Id;
        }

        var needed = new HashSet<int>(plan.Select(p => p.Registry.Id));
        foreach (var rule in plan.SelectMany(p => p.Rules))
        {
            foreach (var code in rule.Reads)
            {
                if (idsByCode.TryGetValue(code, out var id))
                {
                    needed.Add(id);
                }
            }
        }

        var standings = (await registries
                .FindEntryStandingsAsync([.. plan.SelectMany(p => p.Entries).Distinct()], ct)
                .ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        var today = DateOnly.FromDateTime(clock.UtcNow);
        var loaded = new Dictionary<DateOnly, (IRegistrySnapshot Snapshot, RegistryRuleContext Context, Dictionary<string, HashSet<long>> Visible)>();
        var found = new List<(CompiledRegistryRule Rule, long EntryId, Dictionary<string, string?> Params, string MessageKey)>();

        foreach (var item in plan)
        {
            foreach (var entryId in item.Entries.Order())
            {
                if (!standings.TryGetValue(entryId, out var standing) || standing.IsDeleted || !standing.IsActive)
                {
                    continue;
                }

                var date = DateOf(standing, today);
                if (!loaded.TryGetValue(date, out var view))
                {
                    var snapshot = await snapshots.LoadAsync(needed, date, registryAsOfUtc: null, ct).ConfigureAwait(false);
                    view = (snapshot, new RegistryRuleContext(snapshot, date), new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase));
                    loaded[date] = view;
                }

                if (!Visible(view.Snapshot, view.Visible, item.Registry.Code).Contains(entryId))
                {
                    // Запис невидимий на дату (частина невидимого батька): правило про живі записи.
                    continue;
                }

                foreach (var rule in item.Rules)
                {
                    if (Evaluate(rule, entryId, view.Snapshot, view.Context) is { } violation)
                    {
                        found.Add((rule, entryId, violation.Params, violation.MessageKey));
                    }
                }
            }
        }

        if (found.Count == 0)
        {
            return RegistryRuleCheck.None;
        }

        var codes = await keys.FindEntryCodesAsync([.. found.Select(f => f.EntryId).Distinct()], ct).ConfigureAwait(false);
        var violations = found
            .Select(f =>
            {
                var entryCode = codes.GetValueOrDefault(f.EntryId) ?? f.EntryId.ToString(CultureInfo.InvariantCulture);
                var rule = f.Rule.Rule;
                f.Params["rule"] = rule.Code;
                f.Params["entryCode"] = entryCode;
                f.Params["message"] = rule.MessageL10n.Get(currentUser.Language) ?? rule.Code;
                return new RegistryRuleViolationDto(
                    f.EntryId, entryCode, rule.Code, rule.Severity.ToString(), f.MessageKey, f.Params);
            })
            .ToList();

        return new RegistryRuleCheck(violations);
    }

    /// <summary>
    /// Що перевіряти: власні правила — на записаних записах; правила батьків композиції, що читають
    /// довідник нижче, — на батьках записаних і видалених записів.
    /// </summary>
    private async Task<List<PlannedRule>> PlanAsync(
        RegistryDef definition, IReadOnlyCollection<long> changed, IReadOnlyCollection<long> removed, CancellationToken ct)
    {
        var plan = new List<PlannedRule>();

        var own = Executable(await registries.ListRulesAsync(definition.Id, ct).ConfigureAwait(false));
        if (own.Count > 0 && changed.Count > 0)
        {
            plan.Add(new PlannedRule(definition, [.. own.Select(compiler.Compile)], [.. changed]));
        }

        var below = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { definition.Code };
        var visited = new HashSet<int> { definition.Id };
        var current = definition;
        var ids = changed.Concat(removed).Distinct().ToList();

        for (var depth = 0; depth < MaxDepth && ids.Count > 0; depth++)
        {
            var composition = current.Fields.FirstOrDefault(
                f => f.RelationKind == RegistryRelationKind.Composition && f.RefRegistryDefId is not null);
            if (composition is null || !visited.Add(composition.RefRegistryDefId!.Value))
            {
                break;
            }

            var parent = await registries.FindDefinitionByIdAsync(composition.RefRegistryDefId.Value, ct).ConfigureAwait(false);
            if (parent is null)
            {
                break;
            }

            // Правила батька, що читають довідник нижче за ланцюжком: лише їхній результат могла
            // змінити правка дитини.
            var parentRules = Executable(await registries.ListRulesAsync(parent.Id, ct).ConfigureAwait(false))
                .Select(compiler.Compile)
                .Where(r => r.Reads.Overlaps(below))
                .ToList();
            var grandparent = parent.Fields.Any(f => f.RelationKind == RegistryRelationKind.Composition);
            if (parentRules.Count == 0 && !grandparent)
            {
                break;
            }

            var parentIds = (await registries.ListValuesForEntriesAsync(ids, ct).ConfigureAwait(false))
                .Where(v => v.RegistryFieldDefId == composition.Id && v.ValueRefEntryId is not null)
                .Select(v => v.ValueRefEntryId!.Value)
                .Distinct()
                .ToList();

            if (parentRules.Count > 0 && parentIds.Count > 0)
            {
                plan.Add(new PlannedRule(parent, parentRules, parentIds));
            }

            below.Add(parent.Code);
            current = parent;
            ids = parentIds;
        }

        return plan;
    }

    /// <summary>Порушення правила на записі; <c>null</c> — правило виконане або не застосовується.</summary>
    private (string MessageKey, Dictionary<string, string?> Params)? Evaluate(
        CompiledRegistryRule rule, long entryId, IRegistrySnapshot snapshot, RegistryRuleContext context)
    {
        if (!rule.IsValid)
        {
            return (InvalidKey, new Dictionary<string, string?>());
        }

        var parameters = new Dictionary<string, string?>();
        var condition = Run(rule.Condition!, entryId, context);

        switch (rule.Rule.RuleKind)
        {
            case RegistryRuleKind.Expression:
                if (Fails(condition, parameters))
                {
                    if (rule.Measure is not null && Run(rule.Measure, entryId, context).AsNumber() is { } measured)
                    {
                        parameters["value"] = measured.ToString("G29", CultureInfo.InvariantCulture);
                    }

                    return (ViolatedKey, parameters);
                }

                return null;

            case RegistryRuleKind.RequiredWhen:
                if (!Applies(condition, parameters, out var requiredFailed))
                {
                    return requiredFailed ? (ViolatedKey, parameters) : null;
                }

                parameters["field"] = rule.Field;
                var value = snapshot.GetField(entryId, rule.Field!);
                if (value.IsError)
                {
                    parameters["errorCode"] = value.ErrorCode;
                }

                return value.IsNull || value.IsError ? (ViolatedKey, parameters) : null;

            case RegistryRuleKind.CrossRegistry:
                if (!Applies(condition, parameters, out var crossFailed))
                {
                    return crossFailed ? (ViolatedKey, parameters) : null;
                }

                parameters["field"] = rule.Field;

                // `REGFIND(target, ROW.field)` ≠ #N/A (§6): порожнє поле — не порушення (null до пошуку).
                var lookup = new FunctionNode(
                    RegistryForms.Find,
                    [new LiteralNode(rule.TargetRegistry!, ExpressionValueType.Text), new RowFieldNode([rule.Field!])]);
                var match = Run(lookup, entryId, context);
                if (!match.IsError)
                {
                    return null;
                }

                parameters["errorCode"] = match.ErrorCode;
                return (ViolatedKey, parameters);

            default:
                return null;
        }
    }

    /// <summary>
    /// Предикат <c>Expression</c>: <c>TRUE</c> — виконано; <c>null</c> — не порушення (як <c>CHECK</c>);
    /// <c>FALSE</c>, помилка-значення чи не-булеве — порушення (§6).
    /// </summary>
    private static bool Fails(ExpressionValue value, Dictionary<string, string?> parameters)
    {
        if (value.IsError)
        {
            parameters["errorCode"] = value.ErrorCode;
            return true;
        }

        if (value.IsNull)
        {
            return false;
        }

        if (value.Type != ExpressionValueType.Boolean)
        {
            parameters["errorCode"] = Ecr.Expressions.ExpressionErrors.BadValue;
            return true;
        }

        return !(bool)value.Value!;
    }

    /// <summary>Умова застосування (<c>RequiredWhen</c>, <c>CrossRegistry</c>): лише <c>TRUE</c>.</summary>
    /// <param name="value">Значення умови.</param>
    /// <param name="parameters">Параметри порушення.</param>
    /// <param name="failed">Умова сама дала помилку — це порушення.</param>
    private static bool Applies(ExpressionValue value, Dictionary<string, string?> parameters, out bool failed)
    {
        failed = false;
        if (value.IsError)
        {
            parameters["errorCode"] = value.ErrorCode;
            failed = true;
            return false;
        }

        if (value.IsNull)
        {
            return false;
        }

        if (value.Type != ExpressionValueType.Boolean)
        {
            parameters["errorCode"] = Ecr.Expressions.ExpressionErrors.BadValue;
            failed = true;
            return false;
        }

        return (bool)value.Value!;
    }

    private ExpressionValue Run(AstNode node, long entryId, RegistryRuleContext context)
        => evaluator.Evaluate(RegistryRuleContext.Bind(node, entryId), context, ExpressionDialect.Template);

    private static List<RegistryRuleDef> Executable(IReadOnlyList<RegistryRuleDef> rules)
        => [.. rules.Where(r => r.IsActive && r.RuleKind != RegistryRuleKind.UniqueWithin).OrderBy(r => r.Code, StringComparer.Ordinal)];

    private static HashSet<long> Visible(
        IRegistrySnapshot snapshot, Dictionary<string, HashSet<long>> cache, string registryCode)
    {
        if (!cache.TryGetValue(registryCode, out var set))
        {
            set = [.. snapshot.GetEntries(registryCode) ?? []];
            cache[registryCode] = set;
        }

        return set;
    }

    /// <summary>Дата, на яку запис чинний: сьогодні, якщо чинний сьогодні, інакше найближча дата вікна.</summary>
    private static DateOnly DateOf(RegistryEntryStanding standing, DateOnly today)
    {
        if (standing.IsValidOn(today))
        {
            return today;
        }

        if (standing.ValidFrom is { } from && from > today)
        {
            return from;
        }

        return standing.ValidTo is { } to ? to.AddDays(-1) : today;
    }

    /// <summary>Правила одного довідника й записи, на яких їх перевірити.</summary>
    private sealed record PlannedRule(RegistryDef Registry, IReadOnlyList<CompiledRegistryRule> Rules, IReadOnlyList<long> Entries);
}
