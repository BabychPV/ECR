namespace Ecr.Scripting.Lexing;

/// <summary>Вид лексеми скрипта.</summary>
public enum ScriptTokenKind : byte
{
    Identifier, Keyword, NumberLiteral, StringLiteral,
    Plus, Minus, Star, Slash, Percent,
    EqualEqual, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
    AndAnd, OrOr, Bang, Question, Colon,
    LParen, RParen, LBracket, RBracket, LBrace, RBrace,
    Comma, Semicolon, Dot, Assign,
    EndOfInput,
}

/// <summary>Лексема з позицією.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Text">Вихідний текст (для рядка — з лапками).</param>
/// <param name="Start">Зсув від початку тексту.</param>
/// <param name="Length">Довжина.</param>
/// <param name="Line">Рядок з 1.</param>
/// <param name="Column">Колонка з 1.</param>
/// <param name="Value"><c>decimal</c> для числа, розкодований <c>string</c> для рядка.</param>
/// <param name="AfterInvalid">Перед лексемою лексер пропустив заборонений символ і вже дав діагностику
/// (парсер не повторює помилку-наслідок).</param>
/// <param name="Depth">Глибина розгортання <c>foreach</c>, що породив лексему (0 — вихідний текст).</param>
public readonly record struct ScriptToken(
    ScriptTokenKind Kind,
    string Text,
    int Start,
    int Length,
    int Line,
    int Column,
    object? Value = null,
    bool AfterInvalid = false,
    int Depth = 0)
{
    /// <summary>Позиція лексеми.</summary>
    public ScriptSpan Span => new(Start, Length, Line, Column);
}
