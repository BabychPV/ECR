namespace Ecr.MethodologyScriptMigration;

/// <summary>Перемикачі перетворювача, що залежать від можливостей компілятора «ECR Script».</summary>
public static class ConverterOptions
{
    /// <summary>
    /// ПЕРЕМИКАЧ: чи знає компілятор мови конструкцію бібліотеки <c>param Ім'я;</c> (значення — змінна імпортера
    /// з тим самим ім'ям під час обчислення, <c>null</c>, якщо в імпортера її немає).
    /// </summary>
    /// <remarks>
    /// Компілятор уже знає <c>param</c>, тому типово <c>true</c>. Якщо <c>false</c> (компілятор без <c>param</c>), самоперевірка перетворювача пропускає скрипти, що містять
    /// <c>param</c>: інакше кожна бібліотека з константою «за категорією» мала б помилку компіляції. Єдине місце, що це перемикає.
    /// </remarks>
    public static bool LanguageHasParam { get; set; } = true;
}
