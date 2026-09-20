using System.Globalization;
using System.Text.Json;

namespace Ecr.TestKit;

/// <summary>
/// Читання числа з відповіді API, де <c>decimal</c> їде <b>рядком</b>.
/// </summary>
/// <remarks>
/// ⚠ Потрібне саме тому, що форма на дроті змінилася, а зміст — ні
/// (<c>D-30</c>, <c>Ecr.Api.Startup.DecimalAsStringJsonConverter</c>).
/// Тест, який перевіряє ЗНАЧЕННЯ, не повинен знати, рядком воно приїхало
/// чи числом; тест, який перевіряє саме ФОРМУ, читає
/// <see cref="JsonElement.ValueKind"/> напряму і сюди не звертається.
/// </remarks>
public static class JsonNumber
{
    /// <summary>Значення як <c>decimal</c>; приймає і рядок, і число.</summary>
    /// <param name="element">Елемент JSON.</param>
    public static decimal AsDecimal(JsonElement element)
        => AsDecimalOrNull(element)
           ?? throw new InvalidOperationException(
               $"Очікувалося число, отримано {element.ValueKind}.");

    /// <summary>
    /// Те саме, але <c>null</c>, <c>undefined</c> і нечисловий текст дають
    /// <c>null</c> — «комірки немає».
    /// </summary>
    /// <param name="element">Елемент JSON.</param>
    public static decimal? AsDecimalOrNull(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String =>
                decimal.TryParse(
                    element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null,
            _ => null,
        };
}
