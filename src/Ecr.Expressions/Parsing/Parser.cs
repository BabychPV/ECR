using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Lexing;

namespace Ecr.Expressions.Parsing;

/// <summary>
/// Де живе вираз: у формулі (шаблону, методології, звіту) чи в правилі
/// довідника (FEATURE-REGISTRY-TABLES §6).
/// </summary>
/// <remarks>
/// ⚠ Окрема вісь, а не третій <see cref="ExpressionParseMode"/> і не новий
/// <c>ExpressionDialect</c>. Режим відповідає на «хто читає текст» (редактор чи
/// імпортер), діалект — на «чия граматика й функції»; правило довідника пише
/// людина в редакторі, граматикою шаблону (§6), і відрізняється рівно одним —
/// у ньому є запис, що перевіряється (<c>THIS</c>), а верхньорівневий
/// <c>ROW.x</c> — його поле.
///
/// ⛔ Значення за замовчуванням — <see cref="Formula"/>: послаблення граматики
/// мусить бути явним проханням (той самий принцип, що в
/// <see cref="ExpressionParseMode.Editor"/>), інакше <c>THIS</c> тихо став би
/// допустимим у формулі, де йому нема чого означати.
/// </remarks>
public enum ExpressionHost : byte
{
    /// <summary>Формула шаблону, методології чи звіту: <c>THIS</c> немає.</summary>
    Formula = 0,

    /// <summary>Правило довідника: <c>THIS</c> — запис, що перевіряється.</summary>
    RegistryRule = 1,
}

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
    /// <summary>
    /// Бюджет рекурсії розбору: скільки вкладених спусків парсер робить,
    /// перш ніж відмовити.
    /// </summary>
    /// <remarks>
    /// ⛔ Це межа ДОСТУПНОСТІ, а не чистоти мови. Парсер — рекурсивного спуску,
    /// і один рівень вкладеності дужок коштує 12 кадрів стека (увесь ланцюг
    /// <c>ParseExpression → … → ParsePrimary</c>). Замір на цій самій збірці
    /// (окремий процес, стандартний стек 1 МБ): <c>"("×N + "1" + ")"×N</c>
    /// кладе процес уже на <c>N ≈ 606</c> — не «на 10^5», як здавалося із
    /// заявки. <c>StackOverflowException</c> у .NET **не перехоплюється**
    /// (<c>try/catch</c> його не бачить, процес завершує CLR): один запит
    /// <c>POST /api/v1/expressions/validate</c> від будь-якого автентифікованого
    /// користувача вбивав сеанси ВСІХ інших разом зі своїм.
    ///
    /// ⚠ Одиниця тут — СПУСК, а не «рівень дужок», і це не педантизм. Межу
    /// довелося зробити такою, щоб її можна було перевірити механічно:
    /// <c>ParserRecursionCoverageTests</c> викидає з графа викликів кожен
    /// метод, який заходить у <c>EnterNesting</c> ПЕРЕД своїм першим
    /// рекурсивним викликом, і вимагає ациклічного залишку. Сторож усередині
    /// <c>if</c> зробив би таку перевірку брехливою: метод виглядав би
    /// обмеженим, а гілка повз <c>if</c> — ні (саме так перша редакція цього
    /// фіксу пропустила <c>2^2^2…</c>, і саме на цьому мутаційний прогін її
    /// спіймав). Тому чотири сторожі стоять беззастережно, першими рядками
    /// своїх методів.
    ///
    /// ⚠ Сторожів рівно три — <c>ParseExpression</c>, <c>ParseNot</c>,
    /// <c>ParseUnary</c>, — і цей набір ПЕРЕВІРЕНИЙ на мінімальність, а не
    /// обраний: четвертий (у <c>ParsePower</c>) чернетка мала й прибрала, бо
    /// мутаційний прогін показав, що без нього не ламається нічого.
    ///
    /// ⚠ Перерахунок в одиниці користувача: один рівень дужок коштує 3 спуски
    /// (по одному на кожного сторожа), тож 192 — це рівно 63 рівні вкладеності.
    /// Обидва запаси виміряні, а не вгадані:
    /// <list type="bullet">
    /// <item>вниз — найглибша справжня формула корпусу коштує 9 спусків
    /// (<c>CONVERT(([Jan] + [Feb] + [Mar]) * [Density], 'kg', 't')</c>), тобто
    /// запас понад двадцятикратний; <c>ExpressionDepthGuardTests</c> міряє це
    /// поведінкою самого сторожа, а не оком;</item>
    /// <item>вгору — 63 рівні × 12 кадрів ≈ 756 кадрів ≈ 110 КБ стека:
    /// приблизно дев'ятикратний запас до стандартного 1 МБ і безпечно навіть
    /// на потоці зі стеком 256 КБ (це теж тест, а не припущення).</item>
    /// </list>
    /// </remarks>
    public const int MaxRecursionDepth = 192;

    /// <summary>
    /// Скільки ланок ланцюгів бінарних операторів дозволено на одному шляху від
    /// кореня дерева виразу.
    /// </summary>
    /// <remarks>
    /// ⚠ Межа згори — стек потоку для рекурсивних обходів дерева після розбору
    /// (L7-01). Ланцюг — лівий гребінь, тож глибина дерева дорівнює числу ланок.
    /// Найважчі обходи (<c>RegistryRuleContext.BindNode</c>,
    /// <c>RuleExpressionChecks.CheckSupportedReferences</c>) коштують ~0,5–0,6 КБ
    /// стека на рівень: за 2048 ланок це ~1,2 МБ, тобто більше за стек потоку 1 МБ
    /// (Windows) — і законно збережена формула стабільно відмовляла б уже ПІСЛЯ
    /// збереження (у розрахунку, у фоновій задачі). За 1024 — ~0,6 МБ плюс запас
    /// сторожа 128 КБ: вміщається в 1 МБ разом із кадрами хоста
    /// (<c>ExpressionTraversalStackTests</c> міряє це на потоці 768 КБ).
    /// <para>
    /// ⚠ Межа знизу — справжні формули: найглибша формула корпусу має 9 рівнів, а
    /// 4000 символів (<c>MethodologyFormula.MaxExpressionLength</c>) з посиланнями
    /// на комірки (<c>[A]+</c>, ≥ 4 символи на ланку) — це до 1000 ланок. Понад
    /// 1024 ланки дає лише вироджений ланцюг однолітерних операндів
    /// (<c>1+1+…</c>); він відхиляється одразу при збереженні — <c>expr.chainTooLong</c>,
    /// 422, а не пізніше в задачі.
    /// </para>
    /// </remarks>
    public const int MaxChainLinks = 1024;

    /// <summary>
    /// Скільки різних виразів парсер тримає розібраними, перш ніж скинути кеш.
    /// </summary>
    /// <remarks>
    /// ⚠ Стеля, а не витіснення за давністю (`CAL-05`). Корпус — 44 методології
    /// і шаблони; різних ТЕКСТІВ виразів там на порядок менше за цю межу, тож
    /// переповнення означає не «кеш замалий», а щось несподіване — наприклад,
    /// вирази, що склеюються з даних. У такому разі найдешевша правильна
    /// відповідь — почати з чистого аркуша: LRU коштував би блокування або
    /// другої структури на кожному влучанні, тобто плати за випадок, якого в
    /// корпусі немає.
    /// </remarks>
    public const int MaxCachedExpressions = 10_000;

    private static readonly FunctionRegistry Functions = new();

    /// <summary>
    /// Функції, аргументи яких (з другого) — область рядка <c>ROW</c>
    /// (FEATURE-REGISTRY-TABLES §5.4: <c>f</c> і <c>e</c> «над <c>ROW.*</c>»).
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік тут, а не в каталозі функцій, бо область — властивість
    /// ГРАМАТИКИ: парсер мусить знати її ще до того, як функцію взагалі
    /// впізнано. Самих функцій до RT-20a/b немає, і виклик будь-якої з них
    /// сьогодні — «невідома функція»; але <c>ROW.</c> усередині такого виклику
    /// вже не отримує другої, хибної відмови про область.
    ///
    /// ⚠ <c>REGFIND</c> і <c>REGFIELD</c> тут НЕМАЄ навмисно: їхні аргументи —
    /// значення ЗОВНІШНЬОГО виразу (частини ключа, запис), а не умова над рядком.
    /// Регістр не значущий: у діалекті методологій неправильне написання однаково
    /// дасть «невідома функція» з порадою, і друга відмова про область там зайва.
    /// </remarks>
    private static readonly FrozenSet<string> RowScopeFunctions = new[]
    {
        "REGONE", "REGSUM", "REGAVG", "REGMIN", "REGMAX", "REGCOUNT",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Розібрані вирази: <c>(текст, діалект, режим, господар) → результат</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Ключ складений із ЧОТИРЬОХ частин, і жодна з них не зайва. Діалект:
    /// <c>"2 ^ 3"</c> у шаблоні — степінь, у методології — відмова
    /// <c>ECR-CALC-0431</c> (там <c>^</c> це XOR); спільний запис означав би,
    /// що формула методології порахувалася за правилами шаблонів. Режим:
    /// <c>Import</c> приймає голе ім'я параметра, <c>Editor</c> — ні
    /// (директива №05 §4, пункт 5), тож той самий текст там або дерево, або
    /// діагностика. Господар (<see cref="ExpressionHost"/>): <c>THIS</c> у
    /// правилі довідника — запис, що перевіряється, у формулі — відмова
    /// <c>expr.thisOutsideRule</c>; спільний запис дав би правилу чужу відмову
    /// або формулі — чужий дозвіл, залежно від того, хто розібрав текст першим.
    ///
    /// ⛔ Кешувати можна ЛИШЕ тому, що <see cref="ParseResult"/> і дерево
    /// незмінні: вузли — <c>record</c> з <c>init</c>-властивостями, а обидві
    /// колекції (<c>Diagnostics</c> і <c>FunctionNode.Arguments</c>) тепер
    /// заморожені в <see cref="Frozen{T}"/>. Доти за інтерфейсом
    /// <c>IReadOnlyList</c> стояв живий <c>List</c>, і один <c>(List&lt;…&gt;)</c>
    /// у будь-якому споживачі зіпсував би вираз усім наступним викликачам.
    ///
    /// ⚠ Поле екземпляра, а не статичне: у DI <see cref="Parser"/> —
    /// <c>Singleton</c> (<c>Infrastructure/DependencyInjection.cs:154</c>), тож
    /// на застосунок він однаково один, зате тести не успадковують чужий кеш.
    /// </remarks>
    private readonly ConcurrentDictionary<CacheKey, ParseResult> _cache = new();

    /// <summary>Скільки виразів зараз лежить у кеші — для перевірки стелі.</summary>
    public int CachedExpressionCount => _cache.Count;

    /// <summary>Один параметр підстановки для ключа каталогу (`Q-303`).</summary>
    private static Dictionary<string, string> Param(string name, string value)
        => new(1) { [name] = value };

    /// <summary>Довільна кількість параметрів підстановки для ключа каталогу (`Q-303`).</summary>
    private static Dictionary<string, string> Params(params (string Name, string Value)[] pairs)
    {
        var result = new Dictionary<string, string>(pairs.Length);
        foreach (var (name, value) in pairs)
        {
            result[name] = value;
        }

        return result;
    }

    /// <summary>Розбирає вираз.</summary>
    /// <param name="expression">Текст.</param>
    /// <param name="dialect">Діалект — визначає, які посилання дозволені.</param>
    /// <param name="mode">
    /// Хто читає текст. <see cref="ExpressionParseMode.Import"/> приймає ще й
    /// голе ім'я параметра (директива №05 §4, пункт 5) і нормалізує його до
    /// <c>@Name</c> у дереві. За замовчуванням — <c>Editor</c>: послаблення
    /// граматики мусить бути явним проханням, інакше воно тихо стає правилом.
    /// </param>
    /// <param name="host">
    /// Де живе вираз. <see cref="ExpressionHost.RegistryRule"/> дозволяє
    /// <c>THIS</c> і <c>ROW.</c> на верхньому рівні (FEATURE-REGISTRY-TABLES §6) і
    /// має сенс лише з діалектом <c>Template</c>: граматика правил — граматика
    /// шаблону.
    /// </param>
    /// <returns>
    /// Результат із AST або з діагностиками. Помилка синтаксису — **результат**,
    /// а не виняток: конфігуратор має показати проблему, а не впасти.
    /// </returns>
    /// <remarks>
    /// ⚠ Однаковий виклик повертає ТОЙ САМИЙ екземпляр (`CAL-05`). Розбір
    /// ішов на кожну формулу кожного прогону —
    /// <c>GenericCalculationModule</c> робив його двічі (обчислення і збір
    /// констант), <c>RecalculationService</c> — на кожну формулу, — при тому
    /// що текст формули в межах опублікованої версії не змінюється за
    /// побудовою.
    ///
    /// ⛔ Спільний екземпляр безпечний рівно тому, що дерево незмінне; див.
    /// <see cref="_cache"/>. Поява бодай одного <c>set</c> у вузлі AST робить
    /// цей кеш неправильним, а не лише «трохи ризикованим».
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="ExpressionHost.RegistryRule"/> з діалектом, відмінним від <c>Template</c>.
    /// </exception>
    public ParseResult Parse(
        string expression,
        ExpressionDialect dialect,
        ExpressionParseMode mode = ExpressionParseMode.Editor,
        ExpressionHost host = ExpressionHost.Formula)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // ⛔ Відмова викликачеві, а не діагностика: комбінацію обирає код, а не
        // автор виразу. Правило довідника в діалекті методологій означало б
        // `@Arg`/`CST.` у правилі, яких §6 не має, — і мовчазну згоду з цим.
        if (host == ExpressionHost.RegistryRule && dialect != ExpressionDialect.Template)
        {
            throw new ArgumentException(
                $"Правило довідника розбирається лише діалектом Template, а не {dialect}.", nameof(host));
        }

        var key = new CacheKey(expression, dialect, mode, host);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_cache.Count >= MaxCachedExpressions)
        {
            _cache.Clear();
        }

        // ⚠ `GetOrAdd`, а не індексатор: за гонки два потоки можуть розібрати
        // той самий текст двічі, але назовні обидва мусять отримати ОДИН
        // екземпляр — інакше обіцянка «той самий вираз — той самий об'єкт»
        // була б правдою лише в один потік.
        return _cache.GetOrAdd(key, static k => ParseUncached(k.Text, k.Dialect, k.Mode, k.Host));
    }

    /// <summary>Власне розбір — без кешу.</summary>
    private static ParseResult ParseUncached(
        string expression,
        ExpressionDialect dialect,
        ExpressionParseMode mode,
        ExpressionHost host)
    {
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
                ExpressionErrors.Syntax, ex.Message, ex.Position, 1, ex.MessageKey, ex.MessageParams));
            return new ParseResult(false, null, Frozen(diagnostics));
        }

        var state = new State(tokens, dialect, syntax, host, diagnostics);
        AstNode root;
        try
        {
            root = ParseExpression(state);
            if (state.Current.Type != TokenType.EndOfInput)
            {
                state.Error(
                    "expr.trailingText", Param("text", state.Current.Text),
                    $"Unexpected text after the end of the expression: \"{state.Current.Text}\".");
                // ⚠ Лексеми до кінця поглинаються навмисно: інакше цикл
                // розбору піде по колу на тій самій позиції.
                state.SkipToEnd();
            }
        }
        catch (ParseAbort)
        {
            return new ParseResult(false, null, Frozen(diagnostics));
        }

        var parsed = new ParsedExpression(expression, dialect, root, InferShape(root, dialect));
        return new ParseResult(diagnostics.Count == 0, parsed, Frozen(diagnostics));
    }

    /// <summary>Копія, яку не можна змінити навіть приведенням типу.</summary>
    /// <remarks>
    /// ⛔ Саме КОПІЯ в <c>ReadOnlyCollection</c>, а не <c>List.AsReadOnly()</c>
    /// над живим списком і не сам список під інтерфейсом
    /// <c>IReadOnlyList</c>. Результат розбору тепер спільний для всіх
    /// викликачів (`CAL-05`): якби за інтерфейсом лишався <c>List</c>, один
    /// <c>((List&lt;…&gt;)result.Diagnostics).Add(…)</c> дописував би
    /// діагностику всім наступним — і побачити це було б ніяк, бо відбувається
    /// воно в іншому прогоні.
    /// </remarks>
    private static ReadOnlyCollection<T> Frozen<T>(List<T> items)
        => new([.. items]);

    /// <summary>Ключ кеша розбору: текст, діалект, режим і господар разом.</summary>
    private readonly record struct CacheKey(
        string Text, ExpressionDialect Dialect, ExpressionParseMode Mode, ExpressionHost Host);

    // ——— рівні пріоритету, від найслабшого до найсильнішого (02b §2) ———

    /// <summary>
    /// Вхід у ВКЛАДЕНИЙ вираз — і єдине місце, крізь яке проходять чотири з
    /// семи рекурсивних ребер граматики (дужки, аргументи функції, гілки
    /// тернарного оператора, предикат <c>[WHERE …]</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Решта трьох ребер (<c>NOT NOT …</c>, <c>- - …</c>, <c>a^b^c</c>) сюди
    /// НЕ заходять — вони замикаються нижче за <see cref="ParseExpression"/>, —
    /// і тому кожне з них стереже себе саме. Сторож на одному шляху — це не
    /// сторож: перевірка <c>ParserRecursionCoverageTests</c> прибирає з графа
    /// викликів усі методи з <c>EnterNesting</c> і вимагає, щоб залишок був
    /// ациклічним.
    /// </remarks>
    private static AstNode ParseExpression(State s)
    {
        s.EnterNesting();
        var node = ParseTernary(s);
        s.LeaveNesting();
        return node;
    }

    private static AstNode ParseTernary(State s)
    {
        var condition = ParseOr(s);
        if (!s.Match(TokenType.Question))
        {
            return condition;
        }

        var whenTrue = ParseExpression(s);
        s.Expect(TokenType.Colon, "expr.expectedColonInTernary", "Expected \":\" in the ternary operator.");
        var whenFalse = ParseExpression(s);
        return new ConditionalNode(condition, whenTrue, whenFalse) { Position = condition.Position };
    }

    private static AstNode ParseOr(State s)
    {
        var left = ParseAnd(s);
        while (s.Match(TokenType.Or))
        {
            s.CountChainLink();
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
            s.CountChainLink();
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
    /// <remarks>
    /// ⚠ Власний <c>EnterNesting</c>, а не покладання на
    /// <see cref="ParseExpression"/>: ланцюг <c>NOT NOT NOT …</c> замикається
    /// ТУТ і до <see cref="ParseExpression"/> не доходить жодного разу.
    /// </remarks>
    private static AstNode ParseNot(State s)
    {
        s.EnterNesting();
        AstNode node;

        if (s.Current.Type == TokenType.Not || (s.Current.Type == TokenType.Bang && !s.IsFormulaRef))
        {
            var position = s.Current.Position;
            s.Advance();
            node = new UnaryNode(UnaryOperator.Not, ParseNot(s)) { Position = position };
        }
        else
        {
            node = ParseComparison(s);
        }

        s.LeaveNesting();
        return node;
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
        while (s.Current.Type == TokenType.Ampersand)
        {
            if (!s.Syntax.AmpersandIsConcat)
            {
                // ⛔ Аудит L7-04: у NCalc 1.3.8 одинарний `&` — побітове AND (`6 & 3` = 2).
                // Прийняти його конкатенацією означало б тихо порахувати інакше, ніж
                // чинна система. Розбір продовжується, як і для `^`.
                s.Error(
                    "expr.ampersandNotConcat",
                    null,
                    "\"&\" in the methodology dialect does not join text: it is bitwise AND, "
                    + "and \"6 & 3\" equals 2. Use \"and\" for a logical AND.",
                    s.Current.Position,
                    1);
            }

            s.Advance();
            s.CountChainLink();
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

            s.CountChainLink();
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

            s.CountChainLink();
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
        // ⚠ Те саме, що в ParseNot: `- - - …` — окреме рекурсивне ребро, яке
        // не проходить крізь ParseExpression.
        s.EnterNesting();
        AstNode node;

        if (s.Current.Type is TokenType.Minus or TokenType.Plus)
        {
            var position = s.Current.Position;
            var op = s.Current.Type == TokenType.Minus ? UnaryOperator.Negate : UnaryOperator.Plus;
            s.Advance();
            node = new UnaryNode(op, ParseUnary(s)) { Position = position };
        }
        else
        {
            node = ParsePower(s);
        }

        s.LeaveNesting();
        return node;
    }

    /// <summary>
    /// Степінь правоасоціативний: <c>2^3^2 = 2^(3^2) = 512</c> — і лише в
    /// діалекті шаблонів.
    /// </summary>
    /// <remarks>
    /// ⚠ Власного <c>EnterNesting</c> тут НЕМАЄ, і це перевірений факт, а не
    /// недогляд. Степінь правоасоціативний, тому <c>2^2^2^…</c> — не цикл
    /// <c>while</c>, а рекурсія <c>ParsePower → ParseUnary → ParsePower</c>;
    /// вона обов'язково проходить крізь <see cref="ParseUnary"/>, який стереже
    /// себе беззастережно. Четвертий сторож тут стояв у чернетці цього фіксу і
    /// був прибраний саме тому, що мутаційний прогін не зміг довести його
    /// потрібність: він нічого не ловив, лише додавав ще одне число в
    /// арифметику межі. Обмеженість цього шляху доводить
    /// <c>ExpressionDepthGuardTests</c> (ребро <c>power</c>), а те, що жодного
    /// шляху не забуто взагалі, — <c>ParserRecursionCoverageTests</c>.
    ///
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
            s.Error(
                ExpressionErrors.CaretNotPower,
                "expr.caretNotPower",
                null,
                "\"^\" in the methodology dialect does not mean exponentiation: it is bitwise XOR, "
                + "and \"2^3\" equals 1, not 8. Use Pow(a, b).",
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
                s.Expect(TokenType.RParen, "expr.expectedCloseParen", "Expected \")\".");
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

        s.Error("expr.unexpectedToken", Param("token", token.Text), $"Unexpected token \"{token.Text}\".");
        throw new ParseAbort();
    }

    private static SymbolReferenceNode Symbol(State s, SymbolKind kind, int position, string prefix)
    {
        if (s.Current.Type != TokenType.Identifier)
        {
            s.Error(
                "expr.expectedNameAfterPrefix", Param("prefix", prefix), $"Expected a name after \"{prefix}\".");
            throw new ParseAbort();
        }

        var name = s.Current.Text;
        s.Advance();

        if (s.Dialect == ExpressionDialect.Template)
        {
            var construct = $"{prefix}{name}";
            s.Error(
                "expr.methodologyConstructInTemplate", Param("construct", construct),
                $"The construct \"{construct}\" belongs to the methodology dialect "
                + "and is not allowed in template formulas.",
                position, prefix.Length + name.Length);
        }

        // `@Name` у діалекті звітів — параметр звіту; `!Formula` там немає.
        if (kind != SymbolKind.Argument)
        {
            ForbidInReport(s, $"{prefix}{name}", position, prefix.Length + name.Length);
        }

        return new SymbolReferenceNode(kind, name) { Position = position };
    }

    /// <summary>Відмова для посилання, якого діалект звітів не має (<c>02b</c> §8a).</summary>
    /// <remarks>
    /// ⚠ Код — <see cref="ExpressionErrors.Unresolved"/>, не <c>Syntax</c>: текст
    /// розібрався, але послатися з правила звіту на це НЕМАЄ на що.
    /// </remarks>
    private static void ForbidInReport(State s, string construct, int position, int length)
    {
        if (s.Dialect != ExpressionDialect.Report)
        {
            return;
        }

        s.Error(
            ExpressionErrors.Unresolved,
            "expr.referenceForbiddenInReport", Param("construct", construct),
            $"The reference \"{construct}\" is not allowed in the report dialect: a report rule sees only "
            + "the columns of its own row (\"[Code]\") and the report parameters (\"@Name\").",
            position, length);
    }

    private static AstNode ParseIdentifier(State s)
    {
        var token = s.Current;

        // ROW.a.b — поле рядка довідника (FEATURE-REGISTRY-TABLES §5.2).
        // ⚠ Лише з крапкою й кодом після неї, як `CST.`/`HDR.`: голе `ROW` лишається
        // тим, чим було, — невідомим ідентифікатором (або параметром в імпорті).
        if (token.Text.Equals("ROW", StringComparison.OrdinalIgnoreCase)
            && s.Peek(1).Type == TokenType.Dot
            && s.Peek(2).Type == TokenType.Identifier)
        {
            return ParseRowField(s);
        }

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
                    var construct = $"CST.{name}";
                    s.Error(
                        "expr.methodologyConstructInTemplate", Param("construct", construct),
                        $"The construct \"{construct}\" belongs to the methodology dialect "
                        + "and is not allowed in template formulas.",
                        token.Position, token.Length + 1 + name.Length);
                }

                ForbidInReport(s, $"{token.Text}.{name}", token.Position, token.Length + 1 + name.Length);

                return new SymbolReferenceNode(kind.Value, name) { Position = token.Position };
            }
        }

        if (s.Peek(1).Type == TokenType.LParen)
        {
            return ParseFunctionCall(s);
        }

        // THIS — запис, що перевіряє правило довідника (§5.2).
        // ⛔ Крім імпорту: там голе ім'я — ПАРАМЕТР чинної системи (нижче), і
        // параметр `This` у корпусі мусить лишитися параметром. Саме це
        // FEATURE-REGISTRY-TABLES §5.2 має на увазі словами «поза імпортом»:
        // правила довідника імпортер не читає ніколи.
        if (token.Text.Equals("THIS", StringComparison.OrdinalIgnoreCase) && !s.Syntax.BareNameIsArgument)
        {
            return ParseThis(s);
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
        if (s.Dialect == ExpressionDialect.Methodology)
        {
            s.Error(
                "expr.bareNameNeedsAt", Param("name", token.Text),
                $"\"{token.Text}\" is a bare name: in the methodology dialect a parameter is written "
                + $"with \"@\" (\"@{token.Text}\"). Without the prefix, a name cannot be told apart "
                + "from a typo in a function name.");
        }
        else
        {
            s.Error(
                "expr.unknownIdentifier", Param("name", token.Text),
                $"Unknown identifier \"{token.Text}\". A cell reference is written in square brackets.");
        }

        throw new ParseAbort();
    }

    /// <summary>
    /// <c>ROW.code {.code}</c> — поле рядка довідника (FEATURE-REGISTRY-TABLES §5.2).
    /// </summary>
    /// <remarks>
    /// ⛔ Поза областю рядка — діагностика з позицією, а не мовчазне дерево
    /// (перевірка 18 §5.5). Досі <c>ROW.X</c> був синтаксичною помилкою
    /// («невідомий ідентифікатор»); без цієї перевірки він став би ДЕРЕВОМ, яке
    /// жоден зв'язувач не впізнає (<c>TypeChecker</c> дає вузлу тип <c>Null</c>,
    /// сумісний з усім), тобто формула з ним публікувалася б і в рантаймі давала
    /// <c>#VALUE</c> — розбір став би поблажливішим, ніж був. Область рядка —
    /// лексична (аргументи агрегатів, правило довідника), тож перевіряє її той,
    /// хто бачить вкладеність, — парсер.
    ///
    /// ⚠ Розбір після діагностики ПРОДОВЖУЄТЬСЯ, як у <see cref="ForbidInReport"/>:
    /// редактор підсвічує саме посилання, а не «зайвий текст» за ним.
    /// </remarks>
    private static RowFieldNode ParseRowField(State s)
    {
        var token = s.Current;
        s.Advance();                                     // ROW

        var path = new List<string>();
        var end = token.Position + token.Length;
        while (s.Current.Type == TokenType.Dot && s.Peek(1).Type == TokenType.Identifier)
        {
            s.Advance();                                 // '.'
            path.Add(s.Current.Text);
            end = s.Current.Position + s.Current.Length;
            s.Advance();
        }

        var construct = "ROW." + string.Join('.', path);
        var length = end - token.Position;

        ForbidInReport(s, construct, token.Position, length);

        if (s.Dialect != ExpressionDialect.Report && s.Host != ExpressionHost.RegistryRule && s.RowScopeDepth == 0)
        {
            s.Error(
                ExpressionErrors.Unresolved,
                "expr.rowReferenceOutsideScope", Param("construct", construct),
                $"\"{construct}\" refers to a registry row and is allowed only inside a registry aggregate "
                + "(REGSUM, REGAVG, REGMIN, REGMAX, REGCOUNT), REGONE or a registry rule.",
                token.Position, length);
        }

        return new RowFieldNode(path) { Position = token.Position };
    }

    /// <summary><c>THIS</c> — запис, що перевіряє правило довідника (FEATURE-REGISTRY-TABLES §5.2).</summary>
    /// <remarks>
    /// ⛔ Поза правилом — діагностика <c>expr.thisOutsideRule</c> з позицією самого
    /// слова (перевірка 18 §5.5), і розбір триває: у формулі шаблону чи методології
    /// «запису, що перевіряється», немає, і мовчазне дерево тут дало б
    /// <c>#VALUE</c> у рантаймі замість помилки в редакторі.
    /// </remarks>
    private static ThisNode ParseThis(State s)
    {
        var token = s.Current;
        s.Advance();

        ForbidInReport(s, token.Text, token.Position, token.Length);

        if (s.Dialect != ExpressionDialect.Report && s.Host != ExpressionHost.RegistryRule)
        {
            s.Error(
                ExpressionErrors.Unresolved,
                "expr.thisOutsideRule", null,
                "\"THIS\" is allowed only in a registry rule, where it stands for the entry being checked.",
                token.Position, token.Length);
        }

        return new ThisNode { Position = token.Position };
    }

    private static FunctionNode ParseFunctionCall(State s)
    {
        var token = s.Current;
        var name = token.Text;
        s.Advance();
        s.Advance();                                     // '('

        // ⚠ Перший аргумент агрегату — код довідника, і він стоїть ПОЗА областю
        // рядка; решта (`f`, `e`) — над `ROW.*` (FEATURE-REGISTRY-TABLES §5.4).
        // Вкладений агрегат лише поглиблює ту саму область: внутрішній `ROW`
        // затіняє зовнішній, і це вже питання обчислення, а не граматики.
        var opensRowScope = RowScopeFunctions.Contains(name);

        var args = new List<AstNode>();
        if (s.Current.Type != TokenType.RParen)
        {
            args.Add(ParseExpression(s));

            if (opensRowScope)
            {
                s.RowScopeDepth++;
            }

            while (s.Match(TokenType.ArgumentSeparator))
            {
                args.Add(ParseExpression(s));
            }

            if (opensRowScope)
            {
                s.RowScopeDepth--;
            }
        }

        s.Expect(
            TokenType.RParen, "expr.expectedCloseParenInCall", Param("name", name),
            $"Expected \")\" in the call to {name}.");

        // Набір функцій ЗАКРИТИЙ (02b §7–8). Невідома функція — це не «поки що
        // не реалізовано», а помилка публікації: інакше друкарська помилка в
        // імені тихо дає порожнє значення.
        var signature = SignatureOf(name, s.Dialect);
        if (signature is null)
        {
            ReportUnknownFunction(s, name, token);
        }
        else if (args.Count < signature.MinArgs
                 || (signature.MaxArgs is { } max && args.Count > max))
        {
            ReportArgCountMismatch(s, name, signature, args.Count, token);
        }

        // ⛔ Заморожений список аргументів: вузол потрапляє в кеш розбору
        // (`CAL-05`) і звідти — до всіх наступних прогонів. `List` під
        // `IReadOnlyList` означав би, що будь-який споживач може дописати
        // аргумент у ЧУЖИЙ вираз.
        return new FunctionNode(name, Frozen(args)) { Position = token.Position };
    }

    /// <summary>
    /// Сигнатура функції в діалекті; <c>null</c> — такої функції там немає.
    /// </summary>
    /// <remarks>
    /// ⛔ Два діалекти — два КАТАЛОГИ, і це не симетрія заради симетрії.
    /// Діалект шаблонів описує наш власний рушій над таблицею документа
    /// (<c>02b</c> §7): імена там ексельні й регістронезалежні, бо переносяться
    /// з аркуша. Діалект методологій описує ЧУЖИЙ рушій — NCalc 1.3.8, — і
    /// його склад виміряний, а не обраний (<c>DialectCatalog</c>).
    ///
    /// ⛔ До кроку <c>I.14</c> обидва діалекти звірялися з
    /// <see cref="FunctionRegistry"/>, тобто діалект B розбирався вигаданим
    /// набором: <c>POWER(2,3)</c> і <c>SWITCH(…)</c> проходили публікацію, хоча
    /// чинний рушій обох не знає (<c>Q-082</c>). Це не «зайва суворість
    /// тепер» — це різні ЧИСЛА тоді, звірені ні з чим.
    /// </remarks>
    private static FunctionSignature? SignatureOf(string name, ExpressionDialect dialect)
        => dialect switch
        {
            ExpressionDialect.Methodology => DialectCatalog.Find(name),
            ExpressionDialect.Report => ReportFunctions.Find(name),
            _ => Functions.IsAllowed(name) ? Functions.GetSignature(name) : null,
        };

    /// <summary>Відмова для імені, якого в діалекті немає.</summary>
    /// <remarks>
    /// ⚠ Порада — не ввічливість. У діалекті методологій більшість промахів
    /// має рівно одну правильну поправку: <c>POW</c> це описка регістру,
    /// <c>POWER</c> і <c>SWITCH</c> — наш власний вигаданий набір, який ці
    /// формули приймав. «Невідома функція» відправила б методолога шукати те,
    /// чого нема, замість переписати одне слово.
    ///
    /// ⛔ `Q-303`: базове речення тепер локалізоване (ключ каталогу), а порада
    /// з <see cref="DialectCatalog.Advice"/> — НІ, і це свідоме, назване
    /// рішення про межу картки, не пропуск. Порада — вільний текст (шість
    /// записів <c>DialectCatalog.Replacements</c>, кожен — власне речення, не
    /// шаблон із параметрами), і локалізувати її означало б завести окрему
    /// картку для окремого файлу з інакшою формою тексту. Замість двомовного
    /// речення (локалізована основа + сирий український суфікс) діагностика
    /// з порадою лишається ПОВНІСТЮ без ключа — клієнт показує <c>Message</c>
    /// як є, той самий шлях, яким сьогодні йдуть усі діагностики зв'язування
    /// (<c>TypeChecker</c>, <c>UnitChecker</c> і сусіди), яких ця картка теж
    /// свідомо не торкається.
    /// <para>
    /// ⛔ V-20 (третій раунд UX): рішення вище переглянуто. Діагностика з
    /// порадою йшла ПОВНІСТЮ українською (<c>IF(1 &gt; 0, 'a', 'b')</c> →
    /// «Функція 'IF' недоступна в діалекті Methodology…») за будь-якої мови
    /// інтерфейсу. Тепер порада — окремий ключ на кожен вид: регістр
    /// (<c>expr.unknownFunctionCase</c>, з правильним написанням параметром) і
    /// заміна (<c>expr.unknownFunctionReplacement.*</c>, по ключу на ім'я — повне
    /// речення, а не вставка).
    /// </para>
    /// </remarks>
    private static void ReportUnknownFunction(State s, string name, Token token)
    {
        var methodology = s.Dialect == ExpressionDialect.Methodology;
        var dialect = s.Dialect.ToString();

        if (methodology && DialectCatalog.CaseCorrection(name) is { } exact)
        {
            s.Error(
                "expr.unknownFunctionCase", Params(("name", name), ("dialect", dialect), ("exact", exact)),
                $"Function \"{name}\" is not available in the {dialect} dialect. In the legacy engine "
                + $"(NCalc 1.3.8) names are case-sensitive: write \"{exact}\".",
                token.Position, token.Length);
            return;
        }

        if (methodology && DialectCatalog.Replacement(name) is { } replacement)
        {
            s.Error(
                replacement.MessageKey, Params(("name", name), ("dialect", dialect)),
                $"Function \"{name}\" is not available in the {dialect} dialect. In the legacy engine "
                + $"(NCalc 1.3.8) use {replacement.Text} instead.",
                token.Position, token.Length);
            return;
        }

        s.Error(
            "expr.unknownFunction", Params(("name", name), ("dialect", dialect)),
            $"Function \"{name}\" is not available in the {dialect} dialect.",
            token.Position, token.Length);
    }

    /// <summary>Відмова через кількість аргументів — три форми сигнатури, три ключі (`Q-303`).</summary>
    private static void ReportArgCountMismatch(State s, string name, FunctionSignature signature, int actual, Token token)
    {
        var min = signature.MinArgs.ToString(CultureInfo.InvariantCulture);
        var actualText = actual.ToString(CultureInfo.InvariantCulture);

        if (signature.MaxArgs is null)
        {
            s.Error(
                "expr.argCountAtLeast", Params(("name", name), ("min", min), ("actual", actualText)),
                $"Function \"{name}\" takes at least {signature.MinArgs} argument(s), but received {actual}.",
                token.Position, token.Length);
        }
        else if (signature.MinArgs == signature.MaxArgs)
        {
            s.Error(
                "expr.argCountExact", Params(("name", name), ("count", min), ("actual", actualText)),
                $"Function \"{name}\" takes {signature.MinArgs} argument(s), but received {actual}.",
                token.Position, token.Length);
        }
        else
        {
            var max = signature.MaxArgs.Value.ToString(CultureInfo.InvariantCulture);
            s.Error(
                "expr.argCountRange", Params(("name", name), ("min", min), ("max", max), ("actual", actualText)),
                $"Function \"{name}\" takes from {signature.MinArgs} to {signature.MaxArgs} argument(s), "
                + $"but received {actual}.",
                token.Position, token.Length);
        }
    }

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

        // ⛔ Діалект звітів знає рівно одну форму — `[Code]`, колонку СВОГО рядка.
        // Кілька ланок — це комірка документа, `[Period…]` — інший період або
        // календар: з ними правило звіту почало б рахувати показник (`ФВ-10.3`).
        if (segments.Count != 1 || segments[0].Period is not null)
        {
            ForbidInReport(
                s, string.Join('.', segments.Select(x => $"[{x.Text ?? "…"}]")), position, 1);
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
                s.Error(
                    "expr.calendarContextSyntax", null,
                    "The calendar context is written as \"[Period].Property\".", position, 1);
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
                "expr.cellReferencesForbiddenInMethodology", null,
                "Cell references to the document are not allowed in the methodology dialect.",
                position, 1);
        }

        if (segments.Count is < 1 or > 4)
        {
            s.Error(
                "expr.referenceLinkCount", null,
                "A reference has from one to four links.", position, 1);
            throw new ParseAbort();
        }

        var column = segments[^1];
        if (column.Range is not null || column.Predicate is not null)
        {
            s.Error(
                "expr.referenceLastLinkColumn", null,
                "The last link of a reference is a column; a range is not allowed there.", position, 1);
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
            s.Expect(
                TokenType.RBracket, "expr.expectedCloseBracketAfterPredicate",
                "Expected \"]\" after the predicate.");
            return new Segment(null, null, condition, null);
        }

        if (s.Current.Type != TokenType.Identifier)
        {
            s.Error(
                "expr.referenceLinkEmpty", null,
                "Empty or invalid reference link.", open, 1);
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
                    s.Error(
                        "expr.periodExpectedNumber", null,
                        "A number was expected after \"[Period:\".", open, 1);
                    throw new ParseAbort();
                }

                offset = sign * int.Parse(s.Current.Text, CultureInfo.InvariantCulture);
                s.Advance();
            }

            s.Expect(TokenType.RBracket, "expr.expectedCloseBracket", "Expected \"]\".");
            return new Segment(text, null, null, offset);
        }

        // [from:to] — діапазон рядків
        if (s.Match(TokenType.Colon))
        {
            if (s.Current.Type != TokenType.Identifier)
            {
                s.Error(
                    "expr.rowKeyExpected", null,
                    "A row key was expected after \":\".", open, 1);
                throw new ParseAbort();
            }

            var to = s.Current.Text;
            s.Advance();
            s.Expect(
                TokenType.RBracket, "expr.expectedCloseBracketAfterRange", "Expected \"]\" after the range.");
            return new Segment(null, (text, to), null, null);
        }

        s.Expect(TokenType.RBracket, "expr.expectedCloseBracket", "Expected \"]\".");
        return new Segment(text, null, null, null);
    }

    /// <summary>Груба оцінка типу за формою виразу; точний тип дає <c>TypeChecker</c>.</summary>
    /// <remarks>
    /// ⚠ Діалект тут потрібен рівно заради функцій: <c>if</c> діалекту B
    /// повертає тип обраної гілки (у корпусі це буває ТЕКСТ —
    /// <c>'Сверхнорматив'</c>), а ексельний <c>IF</c> діалекту A — свій. Один
    /// каталог на обидва давав би тип не тієї мови.
    /// </remarks>
    private static ExpressionValueType InferShape(AstNode node, ExpressionDialect dialect)
        => node switch
        {
            LiteralNode literal => literal.Type,
            UnaryNode { Operator: UnaryOperator.Not } => ExpressionValueType.Boolean,
            UnaryNode unary => InferShape(unary.Operand, dialect),
            BinaryNode binary => binary.Operator switch
            {
                BinaryOperator.Concat => ExpressionValueType.Text,
                >= BinaryOperator.Equal and <= BinaryOperator.Or => ExpressionValueType.Boolean,
                _ => ExpressionValueType.Number,
            },
            ConditionalNode conditional => InferShape(conditional.WhenTrue, dialect),
            FunctionNode function => SignatureOf(function.Name, dialect)?.ResultType
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
        ExpressionHost host,
        List<ExpressionDiagnostic> diagnostics)
    {
        private int _index;
        private int _depth;
        private int _chainLinks;
        private readonly int[] _chainLinksAtEntry = new int[MaxRecursionDepth + 1];

        public ExpressionDialect Dialect => dialect;

        /// <summary>Де живе вираз: формула чи правило довідника.</summary>
        public ExpressionHost Host => host;

        /// <summary>
        /// Скільки аргументів агрегатів довідника (<see cref="RowScopeFunctions"/>)
        /// охоплюють поточну позицію; <c>0</c> — поза областю рядка.
        /// </summary>
        /// <remarks>
        /// ⚠ Парного <c>finally</c> немає з тієї самої причини, що в
        /// <see cref="LeaveNesting"/>: єдиний вихід повз декремент —
        /// <see cref="ParseAbort"/>, після якого стан не вживається.
        /// </remarks>
        public int RowScopeDepth { get; set; }

        /// <summary>
        /// Заходить на рівень вкладеності глибше; вичерпаний бюджет — відмова.
        /// </summary>
        /// <remarks>
        /// ⛔ Відмова тут — ЗВИЧАЙНА діагностика плюс <see cref="ParseAbort"/>,
        /// той самий механізм, яким парсер уже відповідає на «зайвий текст» чи
        /// «невідома лексема». Інакше й бути не може: <c>Parse</c> зобов'язаний
        /// повернути результат, а не впасти (див. його ж XML-doc), і саме тому
        /// глибина мусить перевірятися ДО рекурсії. Перевірка «постфактум»
        /// неможлива в принципі: до неї вже не доходить черга —
        /// <c>StackOverflowException</c> у .NET не перехоплюється жодним
        /// <c>catch</c>, і процес завершує CLR.
        ///
        /// ⚠ <see cref="ParseAbort"/>, а не «записати діагностику й розбирати
        /// далі» (як робить <c>ParsePower</c> на <c>^</c>): продовження означало
        /// б наступний крок рекурсії, тобто рівно те, від чого ми боронимось.
        /// </remarks>
        public void EnterNesting()
        {
            if (++_depth <= MaxRecursionDepth)
            {
                _chainLinksAtEntry[_depth] = _chainLinks;
                return;
            }

            // ⚠ У повідомленні — рівні ДУЖОК, а не спуски: число межі має
            // означати те саме, що бачить автор формули. Ділення на 3 — той
            // самий перерахунок, що описаний у MaxRecursionDepth.
            var levels = (MaxRecursionDepth / 3) - 1;

            Error(
                "expr.nestingTooDeep",
                Param("max", levels.ToString(CultureInfo.InvariantCulture)),
                $"The expression is nested deeper than {levels} levels.");

            throw new ParseAbort();
        }

        /// <summary>
        /// Рахує ще одну ланку ланцюга бінарних операторів; вичерпаний бюджет — відмова.
        /// </summary>
        /// <remarks>
        /// ⛔ L7-01 (аудит 2026-10-03): ланцюг <c>1+1+…+1</c> розбирається ЦИКЛОМ,
        /// тож <see cref="EnterNesting"/> його не бачить, а дерево виходить лівим
        /// гребенем глибиною в кількість ланок. Ланцюг на 32 000 доданків (64 КіБ
        /// тіла запиту) парсер приймав, а рекурсивні обходи після розбору
        /// (<c>PredicateValidator</c>, <c>TypeChecker</c>, …) вичерпували стек —
        /// <c>StackOverflowException</c> і смерть процесу API.
        ///
        /// ⚠ Лічильник рахує ланки ВІДКРИТИХ ланцюгів на поточному шляху розбору,
        /// а не кожного ланцюга окремо і не всього виразу: ланцюг у дужках, що
        /// стоїть операндом іншого ланцюга, додає свою глибину до глибини
        /// зовнішнього (межа «на ланцюг» це пропустила б), а сусідні аргументи
        /// функції лежать на різних шляхах і не додають (межа «на вираз»
        /// відхиляла б широкі, але мілкі вирази). Повернення до значення на
        /// вході — у <see cref="LeaveNesting"/>. Отже глибина дерева не більша
        /// за <see cref="MaxChainLinks"/> плюс <see cref="MaxRecursionDepth"/>.
        /// </remarks>
        public void CountChainLink()
        {
            if (++_chainLinks <= MaxChainLinks)
            {
                return;
            }

            Error(
                "expr.chainTooLong",
                Param("max", MaxChainLinks.ToString(CultureInfo.InvariantCulture)),
                $"The expression has more than {MaxChainLinks} operators chained together.");

            throw new ParseAbort();
        }

        /// <summary>Повертається на рівень вище після успішного розбору вкладеного виразу.</summary>
        /// <remarks>
        /// ⚠ Парного <c>finally</c> свідомо немає: єдиний спосіб не дійти сюди —
        /// <see cref="ParseAbort"/>, після якого <c>State</c> більше не
        /// вживається взагалі (розбір завершено). <c>try/finally</c> на кожному
        /// рівні рекурсії коштував би більше, ніж дає.
        /// </remarks>
        /// <remarks>
        /// ⚠ Тут же лічильник ланок повертається до значення на вході (L7-01):
        /// ланки сусідніх аргументів <c>SUM(a+b, c+d, …)</c> лежать на РІЗНИХ
        /// шляхах дерева і глибини не складають — див. <see cref="CountChainLink"/>.
        /// </remarks>
        public void LeaveNesting()
        {
            _chainLinks = _chainLinksAtEntry[_depth];
            _depth--;
        }

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

        /// <summary>Те саме, що <see cref="Expect(TokenType, string)"/>, з ключем каталогу (`Q-303`).</summary>
        public void Expect(TokenType type, string messageKey, string message)
            => Expect(type, messageKey, null, message);

        /// <summary>Те саме, з підстановками для ключа.</summary>
        public void Expect(
            TokenType type, string messageKey, IReadOnlyDictionary<string, string>? messageParams, string message)
        {
            if (!Match(type))
            {
                Error(messageKey, messageParams, message);
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

        /// <summary>
        /// Діагностика з ключем каталогу — для локалізації клієнтом (`Q-303`).
        /// </summary>
        /// <remarks>
        /// ⚠ Код лишається <see cref="ExpressionErrors.Syntax"/>: ключ
        /// відповідає лише за ТЕКСТ, код — за те, як конфігуратор і клієнт
        /// класифікують діагностику (незмінно від цієї картки).
        /// </remarks>
        public void Error(string messageKey, IReadOnlyDictionary<string, string>? messageParams, string message)
            => Error(messageKey, messageParams, message, Current.Position, Math.Max(Current.Length, 1));

        /// <summary>Те саме, що вище, з явною позицією/довжиною фрагмента.</summary>
        public void Error(
            string messageKey, IReadOnlyDictionary<string, string>? messageParams, string message,
            int position, int length)
            => Error(ExpressionErrors.Syntax, messageKey, messageParams, message, position, length);

        /// <summary>Ключ каталогу РАЗОМ із власним кодом (`ExpressionErrors.CaretNotPower` і подібні).</summary>
        public void Error(
            string code, string messageKey, IReadOnlyDictionary<string, string>? messageParams, string message,
            int position, int length)
            => diagnostics.Add(new ExpressionDiagnostic(code, message, position, length, messageKey, messageParams));
    }
}
