namespace Ecr.Expressions.Lexing;

/// <summary>Лексичний аналізатор виразів.</summary>
public sealed class Lexer
{
    /// <summary>Розбиває вираз на лексеми.</summary>
    /// <param name="expression">Текст виразу.</param>
    /// <returns>Послідовність лексем, остання — <see cref="TokenType.EndOfInput"/>.</returns>
    /// <exception cref="LexicalException">Недопустимий символ або незакритий рядок.</exception>
    public IReadOnlyList<Token> Tokenize(string expression)
        => throw new NotImplementedException(
            "TODO: розбити на лексеми за 02b §1. Особливості:\n" +
            "— ідентифікатори всередині [...] НЕ екрануються, тому лексер має розрізняти\n" +
            "  контекст: усередині дужок допустимі цифри на початку (RowKey '7001001'),\n" +
            "  зовні — ні (EcrCode);\n" +
            "— рядкові літерали в одинарних лапках, подвоєння '' для екранування;\n" +
            "— числа — інваріантний формат, роздільник тільки '.', без розділювачів тисяч;\n" +
            "— '=' і '==' — синоніми порівняння; присвоєння в мові немає;\n" +
            "— ключові слова AND/OR/NOT/TRUE/FALSE/NULL/WHERE — регістронезалежні;\n" +
            "— коментарів у мові немає (пояснення живуть в описі формули).");
}

/// <summary>Помилка лексичного аналізу.</summary>
public sealed class LexicalException(string message, int position) : Exception(message)
{
    /// <summary>Позиція проблемного символу.</summary>
    public int Position { get; } = position;
}
