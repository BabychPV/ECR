// src/Ecr.Application/Calculations/BindingPredicate.cs
using System.Text.Json;

namespace Ecr.Application.Calculations;

/// <summary>
/// Порівняння предикатів прив'язок виходів до колонки (<c>cfg.CalculationBinding.MatchJson</c>).
/// </summary>
/// <remarks>
/// ⛔ P2-1: дві АКТИВНІ прив'язки на одну колонку законні, поки їхні предикати звужують різні
/// рядки (різні виходи різних методологій для різних рядків таблиці). Дубль — коли предикати
/// ОДНАКОВІ (зокрема обидва «уся таблиця» <c>{}</c>): тоді в кожному рядку претендентів двоє, і
/// «хто виграє» вирішує лише правило зрізу (найбільший <c>Id</c>), а не конфігуратор.
/// ⚠ Часткове перетинання різних предикатів тут НЕ ловиться: довести перетин довільних предикатів
/// без даних неможливо, тож відмови на нього немає — зріз усе одно детермінований.
/// </remarks>
public static class BindingPredicate
{
    /// <summary>Чи два предикати вибирають однакові рядки (за вмістом, а не за пробілами й порядком ключів).</summary>
    /// <param name="left">Перший предикат.</param>
    /// <param name="right">Другий предикат.</param>
    /// <returns><c>true</c> — предикати збігаються.</returns>
    public static bool AreEquivalent(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return json.Trim();
            }

            return string.Join(
                ';',
                document.RootElement.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => p.Name + "=" + p.Value.GetRawText()));
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }
}
