// src/Ecr.Domain/Entities/Calculations/MethodologyRuleMatcher.cs
using System.Text.Json;

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

        try
        {
            using var document = JsonDocument.Parse(matchJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var expected = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();

                if (!values.TryGetValue(property.Name, out var actual)
                    || !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // Порожній об'єкт збігається з УСІМА рядками — легальне правило
            // «вся таблиця», найнижчий Priority (ФВ-13.4).
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
