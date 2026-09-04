namespace Ecr.Migration.PiAf;

/// <summary>
/// Відтворює legacy-формат <c>Attribute_XXXX = "1;7001001;ITEM;…"</c>.
/// </summary>
/// <remarks>
/// ⚠ Це **утиліта валідації міграції**, а не інтеграційний адаптер
/// (`R-A5`, `D-44`). Запису в PI AF немає взагалі, і цей клас не має
/// перетворитися на нього: єдиний його споживач — звірка перенесених даних.
///
/// Форматування значень має збігатися з VBA **побайтно**: інакше валідація
/// покаже розбіжності там, де даних не зіпсовано, і час піде на пошук
/// неіснуючої проблеми.
/// </remarks>
public sealed class LegacyRowSerializer
{
    /// <summary>Складає <c>;</c>-рядок із комірок рядка.</summary>
    /// <param name="cells">Значення за кодом колонки.</param>
    /// <param name="fieldOrder">Порядок полів із <c>ext.LegacyColumnMapping</c>.</param>
    public string Serialize(IReadOnlyDictionary<string, object?> cells, IReadOnlyList<string> fieldOrder)
        => throw new NotImplementedException(
            "TODO: скласти значення через ';' у порядку fieldOrder; форматувати ТАК САМО, " +
            "як VBA: числа — інваріантно з крапкою і без розділювачів тисяч, порожнє значення — " +
            "'NULL' (саме рядок, не порожньо), булеве — '1'/'0', дати — у чинному форматі. " +
            "Завершальний ';' зберігати, якщо він є в оригіналі.");

    /// <summary>Порівнює відтворений рядок з оригіналом.</summary>
    /// <returns>Опис розбіжності або <c>null</c>, якщо збігається.</returns>
    public string? Compare(string reconstructed, string original)
        => throw new NotImplementedException(
            "TODO: порівняти посимвольно; при розбіжності повернути позицію, очікуване і " +
            "фактичне значення поля — саме поля, а не символу: інакше звіт нечитабельний.");
}
