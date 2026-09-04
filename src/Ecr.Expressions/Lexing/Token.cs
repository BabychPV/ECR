namespace Ecr.Expressions.Lexing;

/// <summary>Тип лексеми.</summary>
public enum TokenType : byte
{
    Number, String, Boolean, Null,
    Identifier,
    LBracket, RBracket, LParen, RParen,
    Dot, Comma, Colon, Question,
    At, Bang, Ampersand,
    Plus, Minus, Star, Slash, Percent, Caret,
    Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
    And, Or, Not,
    EndOfInput
}

/// <summary>
/// Лексема з позицією. Позиція потрібна для діагностики: користувач має
/// побачити, **де саме** помилка, а не «вираз некоректний».
/// </summary>
/// <param name="Type">Тип.</param>
/// <param name="Text">Вихідний текст лексеми.</param>
/// <param name="Position">Зсув від початку виразу.</param>
/// <param name="Length">Довжина.</param>
public readonly record struct Token(TokenType Type, string Text, int Position, int Length);
