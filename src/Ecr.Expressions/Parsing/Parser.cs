using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Lexing;

namespace Ecr.Expressions.Parsing;

/// <summary>
/// Парсер рекурсивного спуску. Один парсер на обидва діалекти.
/// </summary>
/// <remarks>
/// ⚠ <c>D-19</c> казав, що різниця між <c>Template</c> і <c>Methodology</c> —
/// **лише** в наборі дозволених посилань і функцій, а не в синтаксисі. Замір
/// NCalc 1.3.8 це спростував двічі: у діалекті методологій <c>^</c> — XOR, а
/// не степінь, і параметр там можна писати без <c>@</c>. Тому синтаксична
/// різниця тепер є, і вся вона зібрана в <see cref="DialectSyntax"/> — щоб її
/// не довелося шукати по гілках парсера.
/// </remarks>
public sealed class Parser
{
    private static readonly FunctionRegistry Functions = new();

    /// <summary>Розбирає вираз.</summary>
    /// <param name="expression">Текст.</param>
    /// <param name="dialect">Діалект — визначає, які посилання дозволені.</param>
    /// <param name="mode">
    /// Хто читає текст. <see cref="ExpressionParseMode.Import"/> приймає ще й
    /// голе ім'я параметра (директива №05 §4, пункт 5) і нормалізує його до
    /// <c>@Name</c> у дереві. За замовчуванням — <c>Editor</c>: послаблення
    /// граматики мусить бути явним проханням, інакше воно тихо стає правилом.
    /// </param>
    /// <returns>
    /// Результат із AST або з діагностиками. Помилка синтаксису — **результат**,
    /// а не виняток: конфігуратор має показати проблему, а не впасти.
    /// </returns>
    public ParseResult Parse(
        string expression,
        ExpressionDialect dialect,
        ExpressionParseMode mode = ExpressionParseMode.Editor)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var diagnostics = new List<ExpressionDiagnostic>();
        var syntax = DialectSyntax.Of(dialect, mode);

        IReadOnlyList<Token> tokens;
        try
        {
            tokens = new Lexer(syntax).Tokenize(expression);
        }
        catch (LexicalException ex)
        {
            diagnostics.Add(new ExpressionDiagnostic(
                ExpressionErrors.Syntax, ex.Message, ex.Position, 1));
            return new ParseResult(false, null, diagnostics);
        }

        var state = new State(tokens, dialect, syntax, diagnostics);
        AstNode root;
        try
        {
            root = ParseExpression(state);
            if (state.Current.Type != TokenType.EndOfInput)
            {
                state.Error($"Зайвий текст після кінця виразу: '{state.Current.Text}'.");
                // ⚠ Лексеми до кінця поглинаються навмисно: інакше цикл
                // розбору піде по колу на тій самій позиції.
                state.SkipToEnd();
            }
        }
        catch (ParseAbort)
        {
            return new ParseResult(false, null, diagnostics);
        }

        var parsed = new ParsedExpression(expression, dialect, root, InferShape(root));
        return new ParseResult(diagnostics.Count == 0, parsed, diagnostics);
    }

    // ——— рівні пріоритету, від найслабшого до найсильнішого (02b §2) ———

    private static AstNode ParseExpression(State s) => ParseTernary(s);

    private static AstNode ParseTernary(State s)
    {
        var condition = ParseOr(s);
        if (!s.Match(TokenType.Question))
        {
            return condition;
        }

        var whenTrue = ParseExpression(s);
        s.Expect(TokenType.Colon, "Очікувалася ':' у тернарному операторі.");
        var whenFalse = ParseExpression(s);
        return new ConditionalNode(condition, whenTrue, whenFalse) { Position = condition.Position };
    }

    private static AstNode ParseOr(State s)
    {
        var left = ParseAnd(s);
        while (s.Match(TokenType.Or))
        {
            var right = ParseAnd(s);
            left = new BinaryNode(BinaryOperator.Or, left, right) { Position = left.Position };
        }

        return left;
    }

    private static AstNode ParseAnd(State s)
    {
        var left = ParseNot(s);
        while (s.Match(TokenType.And))
        {
            var right = ParseNot(s);
            left = new BinaryNode(BinaryOperator.And, left, right) { Position = left.Position };
        }

        return left;
    }

    /// <summary>
    /// <c>NOT</c> стоїть МІЖ <c>AND</c> і порівнянням (EBNF 02b §1), тобто
    /// <c>NOT a = b</c> — це <c>NOT (a = b)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Таблиця пріоритетів §2 ставить <c>!</c>/<c>NOT</c> у рядок 2 разом із
    /// унарним мінусом, що суперечить її ж EBNF. Реалізовано за EBNF: він
    /// детальніший і розводить два різні унарні рівні.
    /// </remarks>
    private static AstNode ParseNot(State s)
    {
        if (s.Current.Type == TokenType.Not || (s.Current.Type == TokenType.Bang && !s.IsFormulaRef))
        {
            var position = s.Current.Position;
            s.Advance();
            var operand = ParseNot(s);
            return new UnaryNode(UnaryOperator.Not, operand) { Position = position };
        }

        return ParseComparison(s);
    }

    private static AstNode ParseComparison(State s)
    {
        var left = ParseConcat(s);
        var op = s.Current.Type switch
        {
            TokenType.Equal => BinaryOperator.Equal,
            TokenType.NotEqual => BinaryOperator.NotEqual,
            TokenType.Less => BinaryOperator.Less,
            TokenType.LessOrEqual => BinaryOperator.LessOrEqual,
            TokenType.Greater => BinaryOperator.Greater,
            TokenType.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
            _ => (BinaryOperator?)null,
        };

        if (op is null)
        {
            return left;
        }

        s.Advance();
        var right = ParseConcat(s);
        return new BinaryNode(op.Value, left, right) { Position = left.Position };
    }

    private static AstNode ParseConcat(State s)
    {
        var left = ParseAdditive(s);
        while (s.Match(TokenType.Ampersand))
        {
            var right = ParseAdditive(s);
            left = new BinaryNode(BinaryOperator.Concat, left, right) { Position = left.Position };
        }

        return left;
    }

    private static AstNode ParseAdditive(State s)
    {
        var left = ParseMultiplicative(s);
        while (true)
        {
            var op = s.Current.Type switch
            {
                TokenType.Plus => BinaryOperator.Add,
                TokenType.Minus => BinaryOperator.Subtract,
                _ => (BinaryOperator?)null,
            };

            if (op is null)
            {
                return left;
            }

            s.Advance();
            var right = ParseMultiplicative(s);
            left = new BinaryNode(op.Value, left, right) { Position = left.Position };
        }
    }

    private static AstNode ParseMultiplicative(State s)
    {
        var left = ParseUnary(s);
        while (true)
        {
            var op = s.Current.Type switch
            {
                TokenType.Star => BinaryOperator.Multiply,
                TokenType.Slash => BinaryOperator.Divide,
                TokenType.Percent => BinaryOperator.Modulo,
                _ => (BinaryOperator?)null,
            };

            if (op is null)
            {
                return left;
            }

            s.Advance();
            var right = ParseUnary(s);
            left = new BinaryNode(op.Value, left, right) { Position = left.Position };
        }
    }

    /// <summary>
    /// Унарний знак СЛАБШИЙ за степінь: <c>-2 ^ 2 = -(2^2) = -4</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ EBNF 02b §1 (<c>power = unary [ "^" power ]</c>) і таблиця §2 дають
    /// протилежне — <c>(-2)^2 = 4</c>, як в Excel. Реалізовано за тестом
    /// <c>Арифметика_обчислюється_за_пріоритетами(-2 ^ 2, -4)</c>: він
    /// виконуваний, входить у Definition of Done і збігається з VBA, мовою
    /// чинної системи. Розбіжність винесена в <c>Q-065</c>.
    /// </remarks>
    private static AstNode ParseUnary(State s)
    {
        if (s.Current.Type is TokenType.Minus or TokenType.Plus)
        {
            var position = s.Current.Position;
            var op = s.Current.Type == TokenType.Minus ? UnaryOperator.Negate : UnaryOperator.Plus;
            s.Advance();
            var operand = ParseUnary(s);
            return new UnaryNode(op, operand) { Position = position };
        }

        return ParsePower(s);
    }

    /// <summary>
    /// Степінь правоасоціативний: <c>2^3^2 = 2^(3^2) = 512</c> — і лише в
    /// діалекті шаблонів.
    /// </summary>
    /// <remarks>
    /// ⛔ У діалекті методологій <c>^</c> заборонений (<c>ECR-CALC-0431</c>,
    /// директива №05 §4). Причина не в чистоті мови: у NCalc 1.3.8 це
    /// **XOR**, і `2^3` там дорівнює 1. Прийняти вираз означало б порахувати
    /// його інакше, ніж чинна система, — тихо і без жодної ознаки.
    ///
    /// ⚠ Розбір після діагностики ПРОДОВЖУЄТЬСЯ, а не обривається: інакше
    /// формула з <c>^</c> посередині дала б ще й «зайвий текст після кінця
    /// виразу», і методолог шукав би другу помилку, якої немає.
    /// </remarks>
    private static AstNode ParsePower(State s)
    {
        var left = ParsePrimary(s);
        if (s.Current.Type != TokenType.Caret)
        {
            return left;
        }

        if (!s.Syntax.CaretIsPower)
        {
            // ⚠ `Pow(a, b)` — ЄДИНИЙ степінь діалекту B: оператора `**` у
            // NCalc 1.3.8 теж немає (замір скасував критерій `2**3 = 8` з
            // кроку 2 директиви №05), тож альтернативи в пораді бути не може.
            //
            // ⛔ Порада поки що випереджає код: `ParseFunctionCall` звіряється
            // з `FunctionRegistry` (вигаданий набір `02b` §8, де степінь —
            // `POWER`), а не з виміряним `DialectCatalog`, де він `Pow`.
            // Тому сьогодні `Pow(2,3)` відхиляється як невідома функція.
            // Каталоги зводить крок `I.14` (`E-7`) — і саме `Pow` має лишитися
            // в тексті: назвати тут `POWER` означало б порадити функцію, якої
            // чинний рушій не знає.
            s.Error(
                ExpressionErrors.CaretNotPower,
                "'^' у діалекті методологій не означає степінь: це побітовий XOR, "
                + "і '2^3' дорівнює 1, а не 8. Використайте Pow(a, b).",
                s.Current.Position,
                1);
        }

        s.Advance();
        var right = ParseUnary(s);
        return new BinaryNode(BinaryOperator.Power, left, right) { Position = left.Position };
    }

    private static AstNode ParsePrimary(State s)
    {
        var token = s.Current;

        switch (token.Type)
        {
            case TokenType.Number:
                s.Advance();
                return new LiteralNode(
                    decimal.Parse(token.Text, CultureInfo.InvariantCulture),
                    ExpressionValueType.Number) { Position = token.Position };

            case TokenType.String:
                s.Advance();
                return new LiteralNode(token.Text, ExpressionValueType.Text) { Position = token.Position };

            case TokenType.Boolean:
                s.Advance();
                return new LiteralNode(
                    token.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    ExpressionValueType.Boolean) { Position = token.Position };

            case TokenType.Null:
                s.Advance();
                return new LiteralNode(null, ExpressionValueType.Null) { Position = token.Position };

            case TokenType.LParen:
            {
                s.Advance();
                var inner = ParseExpression(s);
                s.Expect(TokenType.RParen, "Очікувалася ')'.");
                return inner;
            }

            case TokenType.LBracket:
                return ParseBracketReference(s);

            case TokenType.At:
                s.Advance();
                return Symbol(s, SymbolKind.Argument, token.Position, "@");

            case TokenType.Bang:
                s.Advance();
                return Symbol(s, SymbolKind.Formula, token.Position, "!");

            case TokenType.Identifier:
                return ParseIdentifier(s);
        }

        s.Error($"Неочікувана лексема '{token.Text}'.");
        throw new ParseAbort();
    }

    private static SymbolReferenceNode Symbol(State s, SymbolKind kind, int position, string prefix)
    {
        if (s.Current.Type != TokenType.Identifier)
        {
            s.Error($"Після '{prefix}' очікувалося ім'я.");
            throw new ParseAbort();
        }

        var name = s.Current.Text;
        s.Advance();

        if (s.Dialect == ExpressionDialect.Template)
        {
            s.Error(
                $"Конструкція '{prefix}{name}' належить діалекту методологій і заборонена у формулах шаблону.",
                position, prefix.Length + name.Length);
        }

        return new SymbolReferenceNode(kind, name) { Position = position };
    }

    private static AstNode ParseIdentifier(State s)
    {
        var token = s.Current;

        // CST.X і HDR.X — префікси символів, а не функції.
        if (s.Peek(1).Type == TokenType.Dot && s.Peek(2).Type == TokenType.Identifier)
        {
            var kind = token.Text.ToUpperInvariant() switch
            {
                "CST" => SymbolKind.Constant,
                "HDR" => SymbolKind.Header,
                _ => (SymbolKind?)null,
            };

            if (kind is not null)
            {
                s.Advance();
                s.Advance();
                var name = s.Current.Text;
                s.Advance();

                if (kind == SymbolKind.Constant && s.Dialect == ExpressionDialect.Template)
                {
                    s.Error(
                        $"Конструкція 'CST.{name}' належить діалекту методологій і заборонена у формулах шаблону.",
                        token.Position, token.Length + 1 + name.Length);
                }

                return new SymbolReferenceNode(kind.Value, name) { Position = token.Position };
            }
        }

        if (s.Peek(1).Type == TokenType.LParen)
        {
            return ParseFunctionCall(s);
        }

        // ⛔ Голе ім'я — це параметр, і приймає його ЛИШЕ імпортер (директива
        // №05 §4, пункт 5). У корпусі `Total` і `@Total` стоять в одній
        // формулі, тобто `@` там необов'язковий; відмовити означало б не
        // імпортувати профільні модулі взагалі. Але в дереві лишається одна
        // форма — `SymbolKind.Argument`, — тому друк повертає вже `@Total`, і
        // далі по системі голого імені не існує.
        if (s.Syntax.BareNameIsArgument)
        {
            s.Advance();
            return new SymbolReferenceNode(SymbolKind.Argument, token.Text) { Position = token.Position };
        }

        // ⚠ Повідомлення різні, бо різні й помилки. У діалекті методологій
        // комірок немає за побудовою (02b §3.4), і порада «пишіть у квадратних
        // дужках» відправила б методолога робити те, що заборонено; там єдина
        // правильна поправка — дописати `@`.
        s.Error(s.Dialect == ExpressionDialect.Methodology
            ? $"'{token.Text}' — голе ім'я: у діалекті методологій параметр пишеться з '@' ('@{token.Text}'). "
              + "Без префікса ім'я не відрізнити від описки в назві функції."
            : $"Невідомий ідентифікатор '{token.Text}'. Посилання на комірку пишеться у квадратних дужках.");
        throw new ParseAbort();
    }

    private static FunctionNode ParseFunctionCall(State s)
    {
        var token = s.Current;
        var name = token.Text;
        s.Advance();
        s.Advance();                                     // '('

        var args = new List<AstNode>();
        if (s.Current.Type != TokenType.RParen)
        {
            args.Add(ParseExpression(s));
            while (s.Match(TokenType.ArgumentSeparator))
            {
                args.Add(ParseExpression(s));
            }
        }

        s.Expect(TokenType.RParen, $"Очікувалася ')' у виклику {name}.");

        // Набір функцій ЗАКРИТИЙ (02b §7–8). Невідома функція — це не «поки що
        // не реалізовано», а помилка публікації: інакше друкарська помилка в
        // імені тихо дає порожнє значення.
        if (!Functions.IsAllowed(name, s.Dialect))
        {
            s.Error(
                $"Функція '{name}' недоступна в діалекті {s.Dialect}.",
                token.Position, token.Length);
        }
        else if (Functions.GetSignature(name) is { } signature
                 && (args.Count < signature.MinArgs
                     || (signature.MaxArgs is { } max && args.Count > max)))
        {
            s.Error(
                $"Функція '{name}' приймає {Describe(signature)}, а отримала {args.Count}.",
                token.Position, token.Length);
        }

        return new FunctionNode(name, args) { Position = token.Position };
    }

    private static string Describe(FunctionSignature signature)
        => signature.MaxArgs is null
            ? $"щонайменше {signature.MinArgs} аргументів"
            : signature.MinArgs == signature.MaxArgs
                ? $"{signature.MinArgs} аргументів"
                : $"від {signature.MinArgs} до {signature.MaxArgs} аргументів";

    // ——— посилання ———

    private static AstNode ParseBracketReference(State s)
    {
        var position = s.Current.Position;
        var segments = new List<Segment>();

        while (s.Current.Type == TokenType.LBracket)
        {
            segments.Add(ParseSegment(s));

            if (s.Current.Type == TokenType.Dot && s.Peek(1).Type == TokenType.LBracket)
            {
                s.Advance();
                continue;
            }

            break;
        }

        // `[Period].Days` — календарний контекст (02b §10).
        if (s.Current.Type == TokenType.Dot && s.Peek(1).Type == TokenType.Identifier)
        {
            s.Advance();
            var property = s.Current.Text;
            s.Advance();

            var offset = segments.Count == 1 && segments[0].Period is { } p ? p : 0;
            if (segments.Count != 1 || segments[0].Period is null)
            {
                s.Error("Календарний контекст пишеться як '[Period].Property'.", position, 1);
            }

            return new PeriodPropertyNode(property, offset) { Position = position };
        }

        var periodOffset = 0;
        if (segments.Count > 0 && segments[0].Period is { } offsetValue)
        {
            periodOffset = offsetValue;
            segments.RemoveAt(0);
        }

        if (s.Dialect == ExpressionDialect.Methodology)
        {
            // Методологія працює з підготовленими аргументами, а не лізе в
            // документ сама. Це межа, яка робить її переносною між шаблонами.
            s.Error(
                "Посилання на комірки документа заборонені в діалекті методологій.",
                position, 1);
        }

        if (segments.Count is < 1 or > 4)
        {
            s.Error("Посилання має від однієї до чотирьох ланок.", position, 1);
            throw new ParseAbort();
        }

        var column = segments[^1];
        if (column.Range is not null || column.Predicate is not null)
        {
            s.Error("Остання ланка посилання — колонка, діапазон тут неприпустимий.", position, 1);
            throw new ParseAbort();
        }

        RowSelector row = new RowSelector.Current();
        string? tableCode = null;
        string? sheetCode = null;

        if (segments.Count >= 2)
        {
            row = ToRowSelector(segments[^2]);
        }

        if (segments.Count >= 3)
        {
            tableCode = segments[^3].Text;
        }

        if (segments.Count == 4)
        {
            sheetCode = segments[0].Text;
        }

        return new CellReferenceNode(sheetCode, tableCode, row, column.Text!, periodOffset)
        {
            Position = position,
        };
    }

    private static RowSelector ToRowSelector(Segment segment)
        => segment switch
        {
            { Range: { } r } => new RowSelector.Range(r.From, r.To),
            { Predicate: { } p } => new RowSelector.Predicate(p),
            _ => new RowSelector.Single(segment.Text!),
        };

    private static Segment ParseSegment(State s)
    {
        var open = s.Current.Position;
        s.Advance();                                     // '['

        // Предикат динамічного діапазону: [WHERE …]
        if (s.Current.Type == TokenType.Identifier
            && s.Current.Text.Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            s.Advance();
            var condition = ParseExpression(s);
            s.Expect(TokenType.RBracket, "Очікувалася ']' після предиката.");
            return new Segment(null, null, condition, null);
        }

        if (s.Current.Type != TokenType.Identifier)
        {
            s.Error("Порожня або некоректна ланка посилання.", open, 1);
            throw new ParseAbort();
        }

        var text = s.Current.Text;
        s.Advance();

        // [Period:±N]
        if (text.Equals("Period", StringComparison.OrdinalIgnoreCase))
        {
            var offset = 0;
            if (s.Match(TokenType.Colon))
            {
                var sign = 1;
                if (s.Match(TokenType.Minus))
                {
                    sign = -1;
                }
                else
                {
                    s.Match(TokenType.Plus);
                }

                // ⚠ Усередині дужок цифри лексуються як Identifier — там вони
                // означають RowKey (`7001001`), і лексер не знає, що цього разу
                // це зсув періоду. Тому перевіряється текст, а не тип лексеми.
                if (!s.Current.Text.All(char.IsAsciiDigit) || s.Current.Text.Length == 0)
                {
                    s.Error("Після '[Period:' очікувалося число.", open, 1);
                    throw new ParseAbort();
                }

                offset = sign * int.Parse(s.Current.Text, CultureInfo.InvariantCulture);
                s.Advance();
            }

            s.Expect(TokenType.RBracket, "Очікувалася ']'.");
            return new Segment(text, null, null, offset);
        }

        // [from:to] — діапазон рядків
        if (s.Match(TokenType.Colon))
        {
            if (s.Current.Type != TokenType.Identifier)
            {
                s.Error("Після ':' очікувався ключ рядка.", open, 1);
                throw new ParseAbort();
            }

            var to = s.Current.Text;
            s.Advance();
            s.Expect(TokenType.RBracket, "Очікувалася ']' після діапазону.");
            return new Segment(null, (text, to), null, null);
        }

        s.Expect(TokenType.RBracket, "Очікувалася ']'.");
        return new Segment(text, null, null, null);
    }

    /// <summary>Груба оцінка типу за формою виразу; точний тип дає <c>TypeChecker</c>.</summary>
    private static ExpressionValueType InferShape(AstNode node)
        => node switch
        {
            LiteralNode literal => literal.Type,
            UnaryNode { Operator: UnaryOperator.Not } => ExpressionValueType.Boolean,
            UnaryNode unary => InferShape(unary.Operand),
            BinaryNode binary => binary.Operator switch
            {
                BinaryOperator.Concat => ExpressionValueType.Text,
                >= BinaryOperator.Equal and <= BinaryOperator.Or => ExpressionValueType.Boolean,
                _ => ExpressionValueType.Number,
            },
            ConditionalNode conditional => InferShape(conditional.WhenTrue),
            FunctionNode function => Functions.GetSignature(function.Name)?.ResultType
                                     ?? ExpressionValueType.Null,
            PeriodPropertyNode => ExpressionValueType.Number,
            _ => ExpressionValueType.Null,
        };

    /// <summary>Ланка посилання: код, діапазон, предикат або зсув періоду.</summary>
    private sealed record Segment(
        string? Text, (string From, string To)? Range, AstNode? Predicate, int? Period);

    /// <summary>Розбір далі неможливий; діагностики вже зібрані.</summary>
    private sealed class ParseAbort : Exception;

    private sealed class State(
        IReadOnlyList<Token> tokens,
        ExpressionDialect dialect,
        DialectSyntax syntax,
        List<ExpressionDiagnostic> diagnostics)
    {
        private int _index;

        public ExpressionDialect Dialect => dialect;

        /// <summary>
        /// Синтаксичні відмінності діалекту і режиму розбору: значення <c>^</c>,
        /// роздільник аргументів, допустимість голого імені параметра.
        /// </summary>
        public DialectSyntax Syntax => syntax;

        public Token Current => tokens[Math.Min(_index, tokens.Count - 1)];

        /// <summary>Чи є поточний <c>!</c> посиланням на формулу, а не запереченням.</summary>
        /// <remarks>
        /// ⚠ Єдина справжня неоднозначність граматики: <c>!X</c> — це або
        /// <c>NOT X</c>, або посилання на формулу методології. Розрізняється
        /// діалектом і тим, що після імені немає '(' (виклик функції).
        /// </remarks>
        public bool IsFormulaRef
            => dialect == ExpressionDialect.Methodology
               && Peek(1).Type == TokenType.Identifier
               && Peek(2).Type != TokenType.LParen;

        public Token Peek(int ahead) => tokens[Math.Min(_index + ahead, tokens.Count - 1)];

        public void Advance() => _index++;

        public void SkipToEnd() => _index = tokens.Count - 1;

        public bool Match(TokenType type)
        {
            if (Current.Type != type)
            {
                return false;
            }

            Advance();
            return true;
        }

        public void Expect(TokenType type, string message)
        {
            if (!Match(type))
            {
                Error(message);
                throw new ParseAbort();
            }
        }

        public void Error(string message) => Error(message, Current.Position, Math.Max(Current.Length, 1));

        public void Error(string message, int position, int length)
            => Error(ExpressionErrors.Syntax, message, position, length);

        /// <summary>
        /// Діагностика з ВЛАСНИМ кодом — для випадків, які не є опискою.
        /// </summary>
        /// <remarks>
        /// ⛔ Код тут не косметика: за ним конфігуратор відрізняє «поправте
        /// синтаксис» від «ця конструкція означає в цьому діалекті інше».
        /// Загальний <c>ECR-TMPL-0422</c> звів би обидва до одного рядка в
        /// журналі публікації.
        /// </remarks>
        public void Error(string code, string message, int position, int length)
            => diagnostics.Add(new ExpressionDiagnostic(code, message, position, length));
    }
}
