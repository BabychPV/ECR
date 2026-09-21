using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Матриця покриття «рядки реальних даних × правила» (ФВ-13.4, ФВ-13.9).
/// </summary>
/// <remarks>
/// Осі — distinct-комбінації значень колонок, які згадують правила, по живих рядках
/// таблиць прив'язок методології за вікно періодів. Класифікація — та сама, що в
/// прогоні (<see cref="MethodologyRuleMatcher.Classify"/>). Назовні лише коди значень
/// і лічильники, без чисел звітності.
/// </remarks>
public sealed class RuleCoverageHandler(
    IMethodologyDraftStore drafts,
    IMethodologyStore methodologies,
    ICalculationBindingStore bindings,
    IRuleCoverageReader reader,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право — як у симуляції: перегляд, не публікація.</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Стеля комбінацій у відповіді.</summary>
    public const int MaxCombinations = 5000;

    /// <summary>Будує матрицю.</summary>
    /// <param name="methodologyId">Методологія з адреси.</param>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="tableDefId">Лише одна з таблиць прив'язок; <c>null</c> — усі.</param>
    /// <param name="periodFrom">Нижня межа вікна; <c>null</c> — початок минулого року.</param>
    /// <param name="periodTo">Верхня межа; <c>null</c> — кінець поточного року.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RuleCoverageDto> HandleAsync(
        int methodologyId,
        int methodologyVersionId,
        int? tableDefId,
        int? periodFrom,
        int? periodTo,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await MethodologyVersionGuard
            .RequireOwnAsync(drafts, methodologyId, [methodologyVersionId], ct)
            .ConfigureAwait(false);

        // Дефолт — останній рік (B18/B19): минулий і поточний календарні роки.
        var year = clock.UtcNow.Year;
        var from = PeriodKey.Parse(periodFrom ?? ((year - 1) * 100) + 1).Value;
        var to = PeriodKey.Parse(periodTo ?? (year * 100) + 99).Value;
        if (from > to)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Вікно періодів порожнє: periodFrom {from} більший за periodTo {to}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.coverageWindow",
                    ["periodFrom"] = from.ToString(CultureInfo.InvariantCulture),
                    ["periodTo"] = to.ToString(CultureInfo.InvariantCulture),
                });
        }

        var rules = await methodologies.GetRulesAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var compiled = MethodologyRuleMatcher.Compile(rules);

        // Колонки осі — union ключів предикатів. Нечисловий ключ ні з чим не збігається
        // (значення рядка ключуються ColumnDefId), тож осі не додає.
        var columns = compiled
            .SelectMany(r => r.Pairs ?? [])
            .Select(p => int.TryParse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToList();

        var tables = (await bindings.ListAsync(methodologyId, ct).ConfigureAwait(false))
            .Where(b => b.IsActive && (tableDefId is null || b.TableDefId == tableDefId))
            .Select(b => b.TableDefId)
            .Distinct()
            .Order()
            .ToList();

        var read = await reader.ReadAsync(tables, columns, from, to, MaxCombinations, ct).ConfigureAwait(false);

        var combinations = read
            .Take(MaxCombinations)
            .Select(c => Classify(compiled, columns, c))
            .OrderBy(c => c.State)
            .ThenByDescending(c => c.Rows)
            .ToList();

        return new RuleCoverageDto(
            methodologyVersionId,
            from,
            to,
            tables,
            columns,
            [.. rules.Select(r => new RuleCoverageRuleDto(r.Code, r.Priority))],
            combinations,
            read.Count > MaxCombinations);
    }

    private static RuleCoverageCombinationDto Classify(
        IReadOnlyList<CompiledMethodologyRule> rules, List<int> columns, RuleCoverageCombination combination)
    {
        var texts = combination.Values.Select(v => v is null ? null : MethodologyRuleMatcher.Text(v)).ToList();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            if (texts[i] is not null)
            {
                values[columns[i].ToString(CultureInfo.InvariantCulture)] = texts[i];
            }
        }

        var result = MethodologyRuleMatcher.Classify(rules, values);
        var state = result.Winner is null ? RuleCoverageState.Gap
            : result.IsTie ? RuleCoverageState.Conflict
            : RuleCoverageState.Covered;

        return new RuleCoverageCombinationDto(
            texts,
            state,
            result.Winner?.Code,
            [.. result.Matches.Select(m => m.Code)],
            combination.Rows,
            combination.Documents);
    }
}
