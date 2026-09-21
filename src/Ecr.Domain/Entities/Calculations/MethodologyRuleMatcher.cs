// src/Ecr.Domain/Entities/Calculations/MethodologyRuleMatcher.cs
using System.Text.Json;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Предикат <see cref="MethodologyRule.MatchJson"/> над значеннями рядка
/// (ФВ-13.8, <c>Q-026</c>).
/// </summary>
/// <remarks>
/// ⛔ Живе в <c>Ecr.Domain</c>, а не в <c>Ecr.Calculations</c>, навмисно.
/// <c>Ecr.Calculations.MethodologyResolver</c> зіставляє рядки для ПРОГОНУ, а
/// gate обов'язкових вхідних колонок (директива «обов'язкові вхідні колонки
/// методології») має зіставити той самий рядок ПЕРЕД збереженням — з
/// <c>Ecr.Application.Documents.PatchCellsHandler</c>, який на
/// <c>Ecr.Calculations</c> послатися не може (залежність зворотна:
/// <c>Ecr.Calculations</c> сам залежить від <c>Ecr.Application</c>).
/// Дублювати предикат означало б два джерела істини про те, що таке «збіг
/// правила», здатні розійтися мовчки; спільний метод у <c>Ecr.Domain</c>, від
/// якого залежать обидва проєкти, — єдиний спосіб уникнути і циклу
/// посилань, і дублювання.
/// </remarks>
public static class MethodologyRuleMatcher
{
    /// <summary>Чи задовольняє рядок предикат правила.</summary>
    /// <param name="matchJson">
    /// Плаский JSON-об'єкт «колонка → очікуване значення»; <c>{}</c> —
    /// збігається з усіма рядками. Кон'юнкція: усі пари мусять збігтися.
    /// </param>
    /// <param name="values">Значення рядка: <c>ColumnDefId</c> (рядком) → текст.</param>
    /// <remarks>
    /// ⛔ Зламаний предикат не збігається ні з чим (спіймана
    /// <see cref="JsonException"/>): кинути звідси означало б зупинити
    /// зіставлення цілої таблиці через одне бите правило.
    /// </remarks>
    public static bool Matches(string matchJson, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(matchJson);
        ArgumentNullException.ThrowIfNull(values);

        return Matches(Parse(matchJson), values);
    }

    /// <summary>Розбирає предикати один раз на правило, а не на кожен рядок.</summary>
    /// <param name="ordered">Правила, уже впорядковані за <c>Priority</c> (так віддає сховище).</param>
    public static IReadOnlyList<CompiledMethodologyRule> Compile(IEnumerable<MethodologyRule> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        return [.. ordered.Select(r => new CompiledMethodologyRule(r.Code, r.Priority, Parse(r.MatchJson)))];
    }

    /// <summary>
    /// Усі правила, що збіглися з рядком, переможець (перший за порядком, ФВ-13.4) і
    /// чи є нічия — ще одне збіжне правило з ТИМ САМИМ пріоритетом, що й переможець.
    /// </summary>
    /// <remarks>
    /// Збіги з нижчим пріоритетом — «затінені», не конфлікт: саме так працює
    /// правило «вся таблиця» <c>{}</c> під точнішими правилами.
    /// </remarks>
    public static MethodologyRuleClassification Classify(
        IReadOnlyList<CompiledMethodologyRule> ordered, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(values);

        var matches = ordered.Where(r => Matches(r.Pairs, values)).ToList();
        var winner = matches.FirstOrDefault();
        var tie = winner is not null && matches.Skip(1).Any(m => winner.Priority == m.Priority);

        return new MethodologyRuleClassification(matches, winner, tie);
    }

    /// <summary>Значення комірки як текст для порівняння в предикаті.</summary>
    /// <remarks>
    /// Порівняння текстове навмисно — <c>MatchJson</c> описує коди довідників і
    /// ознаки, а не числові діапазони. Одне перетворення на прогін і на матрицю
    /// покриття: інакше вони розійшлися б на першому ж числовому ключі.
    /// </remarks>
    public static string? Text(CellValueData value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.ValueString
               ?? value.ValueNumeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)
               ?? value.ValueRegistryEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
               ?? value.ValueBool?.ToString();
    }

    /// <summary>Пари «колонка → очікуване»; <c>null</c> — бите правило, що не збігається ні з чим.</summary>
    private static IReadOnlyList<KeyValuePair<string, string?>>? Parse(string matchJson)
    {
        try
        {
            using var document = JsonDocument.Parse(matchJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return [.. document.RootElement.EnumerateObject().Select(p => new KeyValuePair<string, string?>(
                p.Name,
                p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString()))];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Matches(
        IReadOnlyList<KeyValuePair<string, string?>>? pairs, IReadOnlyDictionary<string, string?> values)
    {
        if (pairs is null)
        {
            return false;
        }

        // Порожній набір пар збігається з УСІМА рядками — легальне правило
        // «вся таблиця», найнижчий Priority (ФВ-13.4).
        return pairs.All(p => values.TryGetValue(p.Key, out var actual)
                              && string.Equals(actual, p.Value, StringComparison.Ordinal));
    }
}

/// <summary>Правило з уже розібраним предикатом.</summary>
/// <param name="Code">Код правила.</param>
/// <param name="Priority">Пріоритет; менше — вищий.</param>
/// <param name="Pairs">Пари «ColumnDefId → очікуване значення»; <c>null</c> — бите правило.</param>
public sealed record CompiledMethodologyRule(
    string Code, int Priority, IReadOnlyList<KeyValuePair<string, string?>>? Pairs);

/// <summary>Результат зіставлення рядка з правилами.</summary>
/// <param name="Matches">Усі збіжні правила в порядку пріоритету.</param>
/// <param name="Winner">Переможець або <c>null</c> — рядок не покритий.</param>
/// <param name="IsTie">Ще одне збіжне правило має той самий пріоритет, що й переможець.</param>
public sealed record MethodologyRuleClassification(
    IReadOnlyList<CompiledMethodologyRule> Matches, CompiledMethodologyRule? Winner, bool IsTie);
