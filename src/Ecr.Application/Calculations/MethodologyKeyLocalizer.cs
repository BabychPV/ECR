// src/Ecr.Application/Calculations/MethodologyKeyLocalizer.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Локалізує правила (<c>MatchJson</c>) й обов'язкові входи версії методології до версії шаблону ДОКУМЕНТА (C1).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Єдиний вхід для кожного, хто ЧИТАЄ <c>MethodologyRule.MatchJson</c> або
/// <c>MethodologyRequiredInput.ColumnDefId</c> з метою зіставити їх із комірками документа (прогін, gate запису,
/// зріз, матриця покриття). У сховищі лежать Id колонок тієї версії шаблону, у якій правило писали; документ на
/// клон-версії має інші Id тих самих колонок. Без перекладу правило на клоні не збігається ні з чим, а
/// <c>Block</c>-вимога блокує збереження назавжди. Сторож <c>MethodologyKeyLocalizerGuardTests</c> тримає цей інваріант.
/// </para>
/// <para>
/// ⚠ Безпека: переклад іде лише В колонки версії документа за шляхом і нічого не читає з даних; колонок поза версією
/// документа він не розкриває. Ключ без відповідника НЕ мовчить: у правилах він стає ключем, якого немає в рядку
/// (правило явно не збігається, Id — у <see cref="LocalizedRule.UnmappedColumnIds"/>), у вимогах —
/// <see cref="LocalizedRequiredInput.IsMapped"/> = <c>false</c>.
/// </para>
/// </remarks>
public static class MethodologyKeyLocalizer
{
    /// <summary>Локалізує правила й вимоги до версії шаблону.</summary>
    /// <param name="mapper">Переклад Id; <c>null</c> — Id вважаються локальними (прямий конструктор у тестах).</param>
    /// <param name="targetTemplateVersionId">Версія шаблону документа (<c>TableInstanceRef.TemplateVersionId</c>).</param>
    /// <param name="rules">Правила версії методології.</param>
    /// <param name="requiredInputs">Обов'язкові входи тієї самої версії; <c>null</c> — вони не потрібні.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<LocalizedMethodology> LocalizeAsync(
        IColumnPathMapper? mapper,
        int targetTemplateVersionId,
        IReadOnlyList<MethodologyRule> rules,
        IReadOnlyList<MethodologyRequiredInput>? requiredInputs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var inputs = requiredInputs ?? [];

        IReadOnlyDictionary<int, int>? map = null;
        if (mapper is not null)
        {
            var ids = rules.SelectMany(r => MethodologyRuleMatcher.ColumnIds(r.MatchJson))
                .Concat(inputs.Select(i => i.ColumnDefId))
                .Distinct()
                .ToList();

            map = ids.Count == 0
                ? new Dictionary<int, int>()
                : await mapper.MapToVersionAsync(ids, targetTemplateVersionId, ct).ConfigureAwait(false);
        }

        int? Local(int id) => map is null ? id : map.TryGetValue(id, out var local) ? local : null;

        var localizedRules = new List<LocalizedRule>(rules.Count);
        foreach (var rule in rules)
        {
            var unmapped = new List<int>();
            var json = map is null ? rule.MatchJson : MethodologyRuleMatcher.RewriteKeys(rule.MatchJson, Local, unmapped);
            localizedRules.Add(new LocalizedRule(rule.Code, rule.Priority, json, unmapped));
        }

        var localizedInputs = inputs
            .Select(i => Local(i.ColumnDefId) is { } local
                ? new LocalizedRequiredInput(local, i.ColumnDefId, true, i.Severity, i.HintL10n)
                : new LocalizedRequiredInput(i.ColumnDefId, i.ColumnDefId, false, i.Severity, i.HintL10n))
            .ToList();

        return new LocalizedMethodology(localizedRules, localizedInputs);
    }

    /// <summary>
    /// Id колонок версії шаблону, на які перекладаються входи: те, що показує зірочку «обов'язкова» у зрізі (C1).
    /// </summary>
    /// <param name="mapper">Переклад Id; <c>null</c> — Id вважаються локальними.</param>
    /// <param name="targetTemplateVersionId">Версія шаблону документа.</param>
    /// <param name="columnIds">Id зі сховища.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Локальні Id; колонки без відповідника відкидаються (зірочки на неіснуючій колонці немає).</returns>
    public static async Task<IReadOnlySet<int>> LocalizeColumnIdsAsync(
        IColumnPathMapper? mapper, int targetTemplateVersionId, IReadOnlyCollection<int> columnIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnIds);

        if (mapper is null || columnIds.Count == 0)
        {
            return columnIds.ToHashSet();
        }

        var map = await mapper.MapToVersionAsync(columnIds, targetTemplateVersionId, ct).ConfigureAwait(false);
        return columnIds.Where(map.ContainsKey).Select(id => map[id]).ToHashSet();
    }
}

/// <summary>Правила й вимоги, переведені на колонки версії шаблону документа.</summary>
/// <param name="Rules">Правила в порядку <c>Priority</c>, як їх віддало сховище.</param>
/// <param name="RequiredInputs">Обов'язкові входи.</param>
public sealed record LocalizedMethodology(
    IReadOnlyList<LocalizedRule> Rules, IReadOnlyList<LocalizedRequiredInput> RequiredInputs)
{
    /// <summary>Предикати для <see cref="MethodologyRuleMatcher.Compile(IEnumerable{RulePredicate})"/>.</summary>
    public IEnumerable<RulePredicate> Predicates => Rules.Select(r => new RulePredicate(r.Code, r.Priority, r.MatchJson));
}

/// <summary>Правило з локалізованим предикатом.</summary>
/// <param name="Code">Код правила.</param>
/// <param name="Priority">Пріоритет.</param>
/// <param name="MatchJson">Предикат з Id колонок версії документа.</param>
/// <param name="UnmappedColumnIds">Id зі сховища без відповідника: правило через них не збігається.</param>
public sealed record LocalizedRule(string Code, int Priority, string MatchJson, IReadOnlyList<int> UnmappedColumnIds);

/// <summary>Обов'язковий вхід, переведений на колонку версії документа.</summary>
/// <param name="ColumnDefId">Id колонки версії документа; для <c>IsMapped = false</c> — Id зі сховища (не читати).</param>
/// <param name="SourceColumnDefId">Id зі сховища.</param>
/// <param name="IsMapped">Чи знайшлась колонка з тим самим шляхом у версії документа.</param>
/// <param name="Severity">Блокує чи лише попереджає.</param>
/// <param name="HintL10n">Текст поверх типового шаблону.</param>
public sealed record LocalizedRequiredInput(
    int ColumnDefId, int SourceColumnDefId, bool IsMapped, RequiredInputSeverity Severity, LocalizedText? HintL10n);
