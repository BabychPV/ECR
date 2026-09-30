// src/Ecr.Application/Calculations/MethodologyRuleChecks.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;

namespace Ecr.Application.Calculations;

/// <summary>
/// Перевірки правил відбору рядків методології (<c>MatchJson</c> +
/// <c>Priority</c>, ФВ-13.3, ФВ-13.4) — при збереженні правила і при
/// публікації версії (V-18, третій раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Доти правило приймало будь-що: <c>{not json</c>, <c>[1,2]</c>, порожній
/// рядок, два правила з однаковим пріоритетом, <c>{}</c> («уся таблиця») із
/// пріоритетом вищим за конкретні. Кожен випадок мовчазний:
/// <c>MethodologyRuleMatcher</c> вважає битий предикат таким, що не
/// збігається НІ З ЧИМ, «перший збіг» при рівних пріоритетах залежить від
/// порядку рядків у базі, а <c>{}</c> попереду конкретних правил забирає собі
/// всі рядки — і конкретні не рахують жодного. Побачити це можна було б лише
/// за числами у звіті.
/// <para>
/// ⚠ Предикат — плаский JSON-об'єкт «колонка → скалярне значення»
/// (<c>MethodologyRuleMatcher</c>): вкладений об'єкт чи масив він порівнює як
/// текст JSON, тобто такий предикат не збігається ні з чим так само, як битий.
/// </para>
/// </remarks>
public static class MethodologyRuleChecks
{
    /// <summary>Правило, як його бачать перевірки: код, предикат, пріоритет.</summary>
    /// <param name="Code">Код правила.</param>
    /// <param name="MatchJson">Предикат.</param>
    /// <param name="Priority">Пріоритет; менше — вищий.</param>
    public sealed record RuleSpec(string Code, string MatchJson, int Priority);

    /// <summary>Відмовляє, якщо предикат не є пласким JSON-об'єктом.</summary>
    /// <param name="code">Код правила — для повідомлення.</param>
    /// <param name="matchJson">Предикат.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c>.</exception>
    public static void RequireValidPredicate(string code, string? matchJson)
    {
        if (string.IsNullOrWhiteSpace(matchJson))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Правило «{code}» без предиката: порожній рядок не збігається з жодним рядком "
                + "документа, а «вся таблиця» записується як `{}`.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.ruleNoPredicate",
                    ["code"] = code,
                });
        }

        if (PairCount(matchJson) is null)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Предикат правила «{code}» не є пласким JSON-об'єктом «колонка → значення»: "
                + "такий предикат не збігається з жодним рядком.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.ruleMatchInvalid",
                    ["code"] = code,
                });
        }
    }

    /// <summary>
    /// Відмовляє, якщо набір АКТИВНИХ правил неоднозначний: однакові
    /// пріоритети або «вся таблиця» попереду конкретних правил.
    /// </summary>
    /// <param name="active">Активні правила версії.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c>.</exception>
    /// <remarks>
    /// ⚠ Правила з битим предикатом тут пропускаються: їх ловить
    /// <see cref="RequireValidPredicate"/>, а збереження ІНШОГО правила не має
    /// падати на чужій ваді.
    /// </remarks>
    public static void RequireConsistentSet(IReadOnlyCollection<RuleSpec> active)
    {
        ArgumentNullException.ThrowIfNull(active);

        // ⛔ Однаковий пріоритет — «перший збіг» залежить від порядку рядків у
        // базі, а не від рішення методолога (ФВ-13.4).
        var duplicate = active
            .GroupBy(r => r.Priority)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key)
            .FirstOrDefault();

        if (duplicate is not null)
        {
            var codes = string.Join(", ", duplicate.Select(r => r.Code).Order(StringComparer.Ordinal));

            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Правила {codes} мають однаковий пріоритет {duplicate.Key}: перший збіг тоді "
                + "залежить від порядку зберігання, а не від вашого рішення.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.rulePriorityDuplicate",
                    ["rules"] = codes,
                    ["priority"] = duplicate.Key.ToString(CultureInfo.InvariantCulture),
                });
        }

        var parsed = active
            .Select(r => (Rule: r, Pairs: PairCount(r.MatchJson)))
            .Where(x => x.Pairs is not null)
            .ToList();

        foreach (var (catchAll, _) in parsed.Where(x => x.Pairs == 0).OrderBy(x => x.Rule.Priority))
        {
            var shadowed = parsed
                .Where(x => x.Pairs > 0 && x.Rule.Priority > catchAll.Priority)
                .Select(x => x.Rule.Code)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (shadowed.Count == 0)
            {
                continue;
            }

            var codes = string.Join(", ", shadowed);

            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Правило «{catchAll.Code}» ({{}} — уся таблиця) має пріоритет {catchAll.Priority}, "
                + $"вищий за конкретні правила {codes}: вони не спрацюють ніколи. "
                + "Порожньому предикату — найнижчий пріоритет (найбільше число).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.ruleCatchAllNotLast",
                    ["rule"] = catchAll.Code,
                    ["priority"] = catchAll.Priority.ToString(CultureInfo.InvariantCulture),
                    ["shadowed"] = codes,
                });
        }
    }

    /// <summary>Кількість пар предиката; <c>null</c> — не плаский JSON-об'єкт.</summary>
    private static int? PairCount(string? matchJson)
    {
        if (string.IsNullOrWhiteSpace(matchJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(matchJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var count = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    || string.IsNullOrWhiteSpace(property.Name))
                {
                    return null;
                }

                count++;
            }

            return count;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
