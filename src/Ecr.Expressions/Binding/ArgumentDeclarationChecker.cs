// src/Ecr.Expressions/Binding/ArgumentDeclarationChecker.cs
using System.Runtime.CompilerServices;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Звіряє токени <c>@Arg</c> у тексті виразу з <b>оголошеним</b> списком
/// аргументів формули (<c>FormulaDef.Arguments</c>, перенос <c>FInfo_Arguments</c>).
/// </summary>
/// <remarks>
/// ⛔ Джерело істини про аргументи — СПИСОК, а не текст виразу (директива ПК-1
/// №05 §7, пастка 2). Збірка підставляє рівно те, що перелічено в
/// <c>;</c>-списку; токен, якого в списку немає, у вираз <b>не потрапляє</b>.
/// Наслідок у чинній системі не аварія, а число: формула рахується з
/// невизначеним параметром і повертає правдоподібний результат, який помітять
/// на звірці через місяць — якщо помітять.
/// <para>
/// ⚠ Два боки розбіжності мовчазні по-різному, тому й реакція різна:
/// «у тексті, немає в списку» — помилка публікації (<c>ECR-CALC-0432</c>),
/// «у списку, немає в тексті» — лише попередження. Друге чинна система
/// допускала масово (замір: 359 таких аргументів у 186 формулах проти 38
/// токенів першого роду у двох формулах <c>Flert</c>), і ламати міграцію
/// корпусу через це не можна.
/// </para>
/// </remarks>
public static class ArgumentDeclarationChecker
{
    /// <summary>Розділювач <c>;</c>-списку <c>FInfo_Arguments</c>.</summary>
    private const char DeclarationSeparator = ';';

    /// <summary>Розбирає оголошений список аргументів.</summary>
    /// <param name="declaration">
    /// Значення <c>FormulaDef.Arguments</c> — <c>;</c>-список; <c>null</c> і
    /// порожній рядок дають порожній перелік.
    /// </param>
    /// <returns>Імена як їх записано в джерелі, без порожніх елементів.</returns>
    /// <remarks>
    /// ⚠ Розділювач рівно один — <c>;</c>. Прийняти ще й кому означало б, що
    /// ім'я з комою (а такі в корпусі не заборонені) мовчки розпадається на
    /// два неіснуючих аргументи, і обидва потраплять у перелік «оголошено, не
    /// вжито» замість того, щоб збігтися з токеном виразу.
    /// </remarks>
    public static IReadOnlyList<string> ParseDeclaration(string? declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration))
        {
            return [];
        }

        return declaration
            .Split(DeclarationSeparator)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Оголошений список або <c>null</c>, якщо його не оголошено.
    /// </summary>
    /// <param name="argumentsCsv">Значення <c>FInfo_Arguments</c>; <c>null</c> — колонка порожня.</param>
    /// <returns><c>null</c> — списку немає; інакше — розібраний список.</returns>
    /// <remarks>
    /// ⛔ Місце, де розрізняються «списку немає» і «список порожній», і воно
    /// одне. <see cref="ParseDeclaration"/> на <c>null</c> віддає ПОРОЖНІЙ список — це
    /// правильно для розбору рядка, але смертельно для виклику: порожній список
    /// означає «оголошено нуль аргументів», тож будь-який токен у виразі стає
    /// порушенням.
    ///
    /// ⚠ Колонка <c>ArgumentsCsv</c> з'явилася порожньою в УСІХ наявних рядків.
    /// Якби викликач передав сюди <c>ParseDeclaration(null)</c>, публікація
    /// відхилила б **кожну** формулу корпусу з першого ж дня.
    /// </remarks>
    public static IReadOnlyList<string>? Declared(string? argumentsCsv)
        => argumentsCsv is null ? null : ParseDeclaration(argumentsCsv);

    /// <summary>Усі вживання <c>@Arg</c> у дереві, у порядку обходу.</summary>
    /// <param name="root">Корінь виразу.</param>
    /// <returns>Вживання з позиціями; одне ім'я може трапитися кілька разів.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> — <c>null</c>.</exception>
    public static IReadOnlyList<ArgumentUsage> Used(AstNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<ArgumentUsage>();
        Walk(root, found);
        return found;
    }

    /// <summary>Звіряє вжите з оголошеним.</summary>
    /// <param name="root">Корінь розібраного виразу.</param>
    /// <param name="declared">Оголошений список (див. <see cref="ParseDeclaration"/>).</param>
    /// <param name="contextual">
    /// Контекстні аргументи методології — ті, що збірка передає <b>кожній</b>
    /// формулі незалежно від списку; <c>null</c> — жодного.
    /// </param>
    /// <returns>Перше — помилки публікації, друге — попередження.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root"/> або <paramref name="declared"/> — <c>null</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ Контекстний аргумент глушить <b>обидва</b> боки, і причина в обох та
    /// сама: його зв'язує збірка, а не список. Тому він не є ані невизначеним
    /// параметром (бік перший), ані переданим даремно (бік другий).
    /// <para>
    /// ⚠ Без глушника другий бік дає 186 попереджень на кожну публікацію
    /// (замір корпусу), і майже всі — про <c>@CalculationDate</c> та
    /// <c>@Location</c>, які передаються всім. Перелік такого розміру
    /// перестають читати за тиждень, і разом із ним перестають бачити ті
    /// кілька, що означають описку в імені токена.
    /// </para>
    /// </remarks>
    public static ArgumentAudit Audit(
        AstNode root,
        IEnumerable<string> declared,
        IEnumerable<string>? contextual = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(declared);

        var declaredKeys = new HashSet<string>(declared.Select(NormalizeName), StringComparer.OrdinalIgnoreCase);
        var contextualKeys = new HashSet<string>(
            (contextual ?? []).Select(NormalizeName), StringComparer.OrdinalIgnoreCase);

        var used = Used(root);
        var usedKeys = new HashSet<string>(used.Select(u => NormalizeName(u.Name)), StringComparer.OrdinalIgnoreCase);

        // ⚠ T2-04: у списку (`@A;CST.K`) константу методології можна оголосити як `CST.K`; у виразі
        // вона — `CST.K`-посилання, а не `@`-аргумент. Без цього вона завжди лишалась «невжитою», хоч
        // рахується (4×2=8): хибне попередження публікації.
        var constants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSymbols(root, SymbolKind.Constant, constants);
        foreach (var constant in constants)
        {
            usedKeys.Add("CST_" + constant);
        }

        // Похідні аргументи (2026-11-02): похідний список несе й `!Formula` (як `FInfo_Arguments`), тож
        // посилання на формулу — теж «вжите», а не хибне «оголошено, не вжито».
        var formulas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSymbols(root, SymbolKind.Formula, formulas);
        foreach (var formula in formulas)
        {
            usedKeys.Add("!" + formula);
        }

        // ⚠ Перше вживання, а не всі: методолог виправляє СПИСОК, і другий
        // рядок про той самий токен не додає йому нічого, крім довжини.
        var undeclared = used
            .Where(u => !declaredKeys.Contains(NormalizeName(u.Name))
                        && !contextualKeys.Contains(NormalizeName(u.Name)))
            .GroupBy(u => NormalizeName(u.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var unused = declared
            .Where(name => !usedKeys.Contains(NormalizeName(name))
                           && !contextualKeys.Contains(NormalizeName(name)))
            .Select(StripMarker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ArgumentAudit(undeclared, unused);
    }

    /// <summary>Ключ порівняння імені аргументу.</summary>
    /// <param name="name">Ім'я з тексту виразу або зі списку.</param>
    /// <returns>Нормалізоване ім'я; регістр не знімається — його знімає компаратор.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> — <c>null</c>.</exception>
    /// <remarks>
    /// ⛔ Крапки зводяться до підкреслень, бо саме це робить чинна система —
    /// і робить <b>симетрично</b>: <c>Utilities.cs:220</c> застосовує
    /// <c>Regex.Replace(expr, @"(?&lt;!\d)\.(?!\d)", "_")</c> і до тексту
    /// формули, і до <c>FInfo_Arguments</c>. Порівнювати сирі рядки означало б
    /// оголосити невизначеними 2534 дотових посилання корпусу лише тому, що в
    /// списку вони записані іншою половиною тієї самої пари.
    /// <para>
    /// ⚠ Ціна названа: <c>A.B</c> і <c>A_B</c> тут той самий аргумент. У
    /// чинній системі вони теж той самий аргумент, тож нова розбіжність не
    /// з'являється — але два РІЗНІ імена, що відрізняються лише крапкою,
    /// перевірка не розрізнить.
    /// </para>
    /// <para>
    /// ⚠ Регістр: імена параметрів чинна система порівнює без урахування
    /// регістру (<c>Utilities.cs:186</c>), і перенесено це дослівно.
    /// </para>
    /// </remarks>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        if (trimmed.StartsWith('@'))
        {
            trimmed = trimmed[1..];
        }

        // D-OA: кінцевий `?` — маркер «необов'язковий», а не частина імені.
        if (trimmed.EndsWith(OptionalMarker))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.Replace('.', '_');
    }

    /// <summary>Маркер необов'язкового аргументу в <c>;</c>-списку: <c>Name?</c>.</summary>
    public const char OptionalMarker = '?';

    /// <summary>Чи елемент списку позначено як необов'язковий (<c>Name?</c>).</summary>
    /// <param name="declaredItem">Сирий елемент списку.</param>
    /// <returns><c>true</c> — один кінцевий <c>?</c> після trim і непорожнє ім'я перед ним.</returns>
    public static bool IsOptionalMarker(string? declaredItem)
    {
        if (declaredItem is null)
        {
            return false;
        }

        var trimmed = declaredItem.Trim();
        return trimmed.Length > 1 && trimmed.EndsWith(OptionalMarker) && trimmed[..^1].Trim().Length > 0;
    }

    /// <summary>
    /// Нормалізовані імена необов'язкових аргументів (елементи з кінцевим <c>?</c>).
    /// </summary>
    /// <param name="declaredItems">Сирі елементи списку (можуть бути <c>null</c>).</param>
    /// <returns>Множина без урахування регістру; порожня, якщо маркерів немає.</returns>
    public static IReadOnlySet<string> OptionalNames(IEnumerable<string>? declaredItems)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (declaredItems is null)
        {
            return result;
        }

        foreach (var item in declaredItems)
        {
            if (IsOptionalMarker(item))
            {
                var name = NormalizeName(item);
                if (name.Length > 0)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Необов'язкові імена з одного сирого <c>;</c>-списку (<c>ArgumentsCsv</c>); <c>null</c> — порожня множина.
    /// </summary>
    /// <param name="argumentsCsv">Значення <c>ArgumentsCsv</c> формули.</param>
    /// <returns>Нормалізовані імена без урахування регістру.</returns>
    public static IReadOnlySet<string> OptionalNamesFromCsv(string? argumentsCsv)
        => OptionalNames(ParseDeclaration(argumentsCsv));

    private static string StripMarker(string item)
    {
        var trimmed = item.Trim();
        return IsOptionalMarker(trimmed) ? trimmed[..^1].TrimEnd() : item;
    }

    /// <summary>Імена символів одного виду (<c>CST.X</c>, <c>!F</c>), на які посилається вираз (без префікса).</summary>
    private static void CollectSymbols(AstNode node, SymbolKind kind, HashSet<string> found)
    {
        // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
        RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (node)
        {
            case SymbolReferenceNode symbol when symbol.Kind == kind:
                found.Add(symbol.Name);
                return;
            case UnaryNode unary:
                CollectSymbols(unary.Operand, kind, found);
                return;
            case BinaryNode binary:
                CollectSymbols(binary.Left, kind, found);
                CollectSymbols(binary.Right, kind, found);
                return;
            case ConditionalNode conditional:
                CollectSymbols(conditional.Condition, kind, found);
                CollectSymbols(conditional.WhenTrue, kind, found);
                CollectSymbols(conditional.WhenFalse, kind, found);
                return;
            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    CollectSymbols(argument, kind, found);
                }

                return;
            case CellReferenceNode { Row: RowSelector.Predicate predicate }:
                CollectSymbols(predicate.Condition, kind, found);
                return;
            default:
                return;
        }
    }

    // ——— Похідні аргументи (2026-11-02): список виводиться з тексту виразу, а не вводиться руками ———

    /// <summary>Лексер діалекту методологій — ті самі правила, що й у розборі публікації.</summary>
    private static readonly Lexing.Lexer MethodologyLexer = new(DialectSyntax.Of(Ecr.Domain.Enums.ExpressionDialect.Methodology));

    /// <summary>
    /// Похідний список аргументів формули: <c>@Arg</c> (з маркером <c>?</c>), <c>CST.X</c> і <c>!Formula</c> у порядку
    /// першої появи в тексті, кожне ім'я — раз.
    /// </summary>
    /// <param name="expression">Вираз діалекту методологій; <c>null</c> — порожній список.</param>
    /// <returns>Похідні елементи.</returns>
    /// <remarks>
    /// ⛔ Список більше НЕ вводиться (похідні аргументи, рішення 2026-11-02): методолог пише вираз, а
    /// <c>ArgumentsCsv</c> зберігається як похідна копія — лише тому, що рантайм (<c>OptionalArguments</c>)
    /// і публікація читають маркери <c>?</c> звідти, і їхня семантика (об'єднання по ВСІЙ версії) не змінюється.
    /// <para>
    /// ⚠ Читає ЛЕКСЕМИ, а не дерево: чернетка зберігає недописаний вираз, і список має бути похідним і від нього.
    /// Розбіжність лексем із деревом публікація ловить сама: <see cref="Audit"/> порівнює похідний список із
    /// <c>@</c>-вузлами дерева (<c>ECR-CALC-0432</c> — тепер твердження, а не перевірка вводу).
    /// </para>
    /// <para>
    /// ⚠ Те саме ім'я з маркером і без — один елемент, НЕОБОВ'ЯЗКОВИЙ: рантайм об'єднує маркери по версії, і
    /// «обов'язковий» там, де його в іншому місці тієї ж формули позначено <c>?</c>, все одно не існував би.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DeclaredArgument> Derive(string? expression)
    {
        var tokens = MethodologyLexer.TokenizePrefix(expression);
        var result = new List<DeclaredArgument>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(DeclaredArgumentKind kind, string name, bool optional)
        {
            var key = DeclaredArgument.KeyOf(kind, name);
            if (index.TryGetValue(key, out var at))
            {
                if (optional && !result[at].IsOptional)
                {
                    result[at] = result[at] with { IsOptional = true };
                }

                return;
            }

            index[key] = result.Count;
            result.Add(new DeclaredArgument(kind, name, optional));
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Type == Lexing.TokenType.At
                && i + 1 < tokens.Count && tokens[i + 1].Type == Lexing.TokenType.Identifier)
            {
                var optional = i + 2 < tokens.Count && tokens[i + 2].Type == Lexing.TokenType.OptionalMarker;
                Add(DeclaredArgumentKind.Argument, tokens[i + 1].Text, optional);
                i += optional ? 2 : 1;
                continue;
            }

            // `CST.X` — так само, як його впізнає парсер (`ParseIdentifier`: Identifier, Dot, Identifier).
            if (token.Type == Lexing.TokenType.Identifier
                && string.Equals(token.Text, "CST", StringComparison.OrdinalIgnoreCase)
                && i + 2 < tokens.Count
                && tokens[i + 1].Type == Lexing.TokenType.Dot
                && tokens[i + 2].Type == Lexing.TokenType.Identifier)
            {
                Add(DeclaredArgumentKind.Constant, tokens[i + 2].Text, optional: false);
                i += 2;
                continue;
            }

            // `!Formula` — як `IsFormulaRef` парсера: ім'я, за яким не відкривається виклик функції.
            if (token.Type == Lexing.TokenType.Bang
                && i + 1 < tokens.Count && tokens[i + 1].Type == Lexing.TokenType.Identifier
                && (i + 2 >= tokens.Count || tokens[i + 2].Type != Lexing.TokenType.LParen))
            {
                Add(DeclaredArgumentKind.Formula, tokens[i + 1].Text, optional: false);
                i += 1;
            }
        }

        return result;
    }

    /// <summary>Похідний <c>;</c>-список для <c>ArgumentsCsv</c>: <c>@A?;CST.K;!F</c>; без елементів — порожній рядок.</summary>
    /// <param name="arguments">Похідні елементи (<see cref="Derive"/>).</param>
    /// <returns>Текст колонки.</returns>
    public static string Format(IEnumerable<DeclaredArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return string.Join(DeclarationSeparator, arguments.Select(a => a.Text));
    }

    /// <summary>Похідний <c>ArgumentsCsv</c> виразу — <see cref="Format"/> від <see cref="Derive"/>.</summary>
    /// <param name="expression">Вираз.</param>
    /// <returns>Текст колонки.</returns>
    public static string DeriveCsv(string? expression) => Format(Derive(expression));

    /// <summary>Елементи збереженого (зокрема старого, введеного руками) <c>;</c>-списку.</summary>
    /// <param name="argumentsCsv">Значення <c>ArgumentsCsv</c>.</param>
    /// <returns>Елементи; невпізнаний запис — аргумент з іменем як є.</returns>
    public static IReadOnlyList<DeclaredArgument> FromCsv(string? argumentsCsv)
    {
        var result = new List<DeclaredArgument>();
        foreach (var item in ParseDeclaration(argumentsCsv))
        {
            if (item.StartsWith('!'))
            {
                result.Add(new DeclaredArgument(DeclaredArgumentKind.Formula, item[1..].Trim(), false));
            }
            else if (item.StartsWith("CST.", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new DeclaredArgument(DeclaredArgumentKind.Constant, item[4..].Trim(), false));
            }
            else
            {
                var optional = IsOptionalMarker(item);
                var name = StripMarker(item).Trim();
                if (name.StartsWith('@'))
                {
                    name = name[1..];
                }

                result.Add(new DeclaredArgument(DeclaredArgumentKind.Argument, name, optional));
            }
        }

        return result;
    }

    /// <summary>
    /// Ставить маркер <c>?</c> за кожним <c>@Arg</c> виразу, чиє ім'я у множині необов'язкових і ще без маркера.
    /// </summary>
    /// <param name="expression">Вираз.</param>
    /// <param name="optionalNames">Нормалізовані імена (<see cref="NormalizeName"/>), без регістру.</param>
    /// <returns>Вираз із маркерами; той самий рядок, якщо ставити нічого.</returns>
    /// <remarks>
    /// ⚠ Позиції беруться з ЛЕКСЕМ: <c>@A</c> усередині рядкового літерала (<c>'@A'</c>) і довше ім'я з тим самим
    /// початком (<c>@AB</c>) маркера не отримують. Вживається для переносу старих маркерів зі списку у вираз
    /// (міграція, пакет AF, застаріле поле запиту).
    /// </remarks>
    public static string ApplyOptionalMarkers(string expression, IReadOnlySet<string> optionalNames)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(optionalNames);

        if (optionalNames.Count == 0)
        {
            return expression;
        }

        var tokens = MethodologyLexer.TokenizePrefix(expression);
        var inserts = new List<int>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Type != Lexing.TokenType.At || tokens[i + 1].Type != Lexing.TokenType.Identifier)
            {
                continue;
            }

            var name = tokens[i + 1];
            var marked = i + 2 < tokens.Count && tokens[i + 2].Type == Lexing.TokenType.OptionalMarker;
            if (!marked && optionalNames.Contains(NormalizeName(name.Text)))
            {
                inserts.Add(name.Position + name.Length);
            }
        }

        if (inserts.Count == 0)
        {
            return expression;
        }

        var text = new System.Text.StringBuilder(expression);
        for (var k = inserts.Count - 1; k >= 0; k--)
        {
            text.Insert(inserts[k], OptionalMarker);
        }

        return text.ToString();
    }

    /// <summary>Обхід дерева зі збиранням <c>@Arg</c>.</summary>
    private static void Walk(AstNode node, List<ArgumentUsage> found)
    {
        // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
        RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (node)
        {
            case SymbolReferenceNode { Kind: SymbolKind.Argument } symbol:
                found.Add(new ArgumentUsage(symbol.Name, symbol.Position));
                return;

            case UnaryNode unary:
                Walk(unary.Operand, found);
                return;

            case BinaryNode binary:
                Walk(binary.Left, found);
                Walk(binary.Right, found);
                return;

            case ConditionalNode conditional:
                Walk(conditional.Condition, found);
                Walk(conditional.WhenTrue, found);
                Walk(conditional.WhenFalse, found);
                return;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    Walk(argument, found);
                }

                return;

            // ⚠ Посилання на комірку в діалекті методологій заборонене (02b
            // §3.4), але предикат рядка — це піддерево, і пропустити його
            // означало б не побачити токен, який туди сховався.
            case CellReferenceNode { Row: RowSelector.Predicate predicate }:
                Walk(predicate.Condition, found);
                return;

            default:
                return;
        }
    }
}

/// <summary>Вид елемента похідного списку аргументів.</summary>
public enum DeclaredArgumentKind : byte
{
    /// <summary><c>@Arg</c> — колонка рядка.</summary>
    Argument = 0,

    /// <summary><c>CST.X</c> — константа версії.</summary>
    Constant = 1,

    /// <summary><c>!Formula</c> — інша формула.</summary>
    Formula = 2,
}

/// <summary>Елемент похідного списку аргументів формули.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Name">Ім'я як у тексті, без префікса й маркера.</param>
/// <param name="IsOptional">Маркер <c>?</c> (лише для <see cref="DeclaredArgumentKind.Argument"/>).</param>
public sealed record DeclaredArgument(DeclaredArgumentKind Kind, string Name, bool IsOptional)
{
    /// <summary>Запис у <c>;</c>-списку: <c>@A?</c>, <c>CST.K</c>, <c>!F</c>.</summary>
    public string Text => Kind switch
    {
        DeclaredArgumentKind.Argument => "@" + Name + (IsOptional ? "?" : string.Empty),
        DeclaredArgumentKind.Constant => "CST." + Name,
        _ => "!" + Name,
    };

    /// <summary>Ключ порівняння (вид + нормалізоване ім'я); регістр знімає компаратор.</summary>
    public string Key => KeyOf(Kind, Name);

    /// <summary>Ключ порівняння для виду й імені.</summary>
    /// <param name="kind">Вид.</param>
    /// <param name="name">Ім'я.</param>
    /// <returns>Ключ.</returns>
    public static string KeyOf(DeclaredArgumentKind kind, string name)
        => ((byte)kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
           + ArgumentDeclarationChecker.NormalizeName(name);
}

/// <summary>Одне вживання аргументу у виразі.</summary>
/// <param name="Name">Ім'я як його написано в тексті, без <c>@</c>.</param>
/// <param name="Position">Позиція в тексті виразу — щоб редактор підсвітив саме її.</param>
public sealed record ArgumentUsage(string Name, int Position);

/// <summary>Результат звірки тексту виразу з оголошеним списком аргументів.</summary>
/// <param name="Undeclared">
/// Токени, що є у виразі й відсутні в списку, — <b>помилка</b> публікації
/// (<c>ECR-CALC-0432</c>): збірка їх не підставить, і формула порахується з
/// невизначеним параметром.
/// </param>
/// <param name="UnusedDeclared">
/// Імена, оголошені в списку й відсутні у виразі, — <b>попередження</b>: чинна
/// система це допускала, і ламати міграцію корпусу через це не можна.
/// </param>
public sealed record ArgumentAudit(
    IReadOnlyList<ArgumentUsage> Undeclared,
    IReadOnlyList<string> UnusedDeclared);
