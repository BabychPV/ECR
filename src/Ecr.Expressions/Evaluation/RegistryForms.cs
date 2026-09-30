using System.Collections.Frozen;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Функції довідників, що обчислюються як СПЕЦФОРМИ: <c>REGFIND</c>,
/// <c>REGONE</c>, агрегати <c>REGSUM</c>/<c>REGAVG</c>/<c>REGMIN</c>/<c>REGMAX</c>/<c>REGCOUNT</c>
/// (RT-20b) і <c>REGFIELD</c> у методологіях (FEATURE-REGISTRY-TABLES §5.4,
/// <c>02b</c> «Функції довідників»; <c>D-159</c>…<c>D-162</c>).
/// </summary>
/// <remarks>
/// ⛔ Спецформа, а не звичайна функція каталогу, і причина — <c>REGONE</c>: її
/// умова <c>f</c> обчислюється НАД КОЖНИМ рядком довідника у власній області
/// <c>ROW</c>, тобто ліниво, як гілки <c>IFERROR</c> і <c>if</c>. Функції
/// каталогу отримують уже обчислені значення, і до рядка довідника їм не
/// дотягнутися. <c>REGFIND</c> ліниво нічого не рахує, але живе тут же, щоб
/// семантика пошуку (<c>#N/A</c>/<c>#MULTI</c>/<c>#REF</c>) мала одне місце на
/// обидва діалекти.
///
/// ⚠ Обчислювач передає сюди делегат <c>evaluate</c>, який веде назад у
/// <c>Evaluator.EvaluateScalar</c> з ТИМ САМИМ бюджетом: кожен рекурсивний
/// спуск проходить крізь сторожа глибини (<c>EvaluatorRecursionCoverageTests</c>),
/// а кожен переглянутий рядок — крізь лічильник кроків.
///
/// ⛔ Жодного звернення до БД: дані — лише зі знімка
/// <see cref="IEvaluationContext.Registries"/>. Без знімка — <c>#REF</c>, а не
/// виняток: одна формула не сміє зірвати прогін.
/// </remarks>
public static class RegistryForms
{
    /// <summary>Пошук запису за первинним ключем.</summary>
    public const string Find = "REGFIND";

    /// <summary>Єдиний запис за умовою над <c>ROW.*</c>.</summary>
    public const string One = "REGONE";

    /// <summary>Поле запису, зокрема шляхом через <c>Lookup</c>-поля.</summary>
    public const string Field = "REGFIELD";

    /// <summary>Сума виразу по рядках, що пройшли фільтр; порожньо — <c>0</c>.</summary>
    public const string Sum = "REGSUM";

    /// <summary>Середнє не-<c>null</c>; порожньо — <c>null</c>.</summary>
    public const string Average = "REGAVG";

    /// <summary>Мінімум не-<c>null</c> (число або дата); порожньо — <c>null</c>.</summary>
    public const string Minimum = "REGMIN";

    /// <summary>Максимум не-<c>null</c> (число або дата); порожньо — <c>null</c>.</summary>
    public const string Maximum = "REGMAX";

    /// <summary>Кількість рядків, що пройшли фільтр; порожньо — <c>0</c>.</summary>
    public const string Count = "REGCOUNT";

    /// <summary>
    /// Функції, що відкривають область рядка: другий і наступні аргументи
    /// обчислюються над кожним рядком довідника.
    /// </summary>
    /// <remarks>
    /// ⚠ Той самий склад, що <c>Parser.RowScopeFunctions</c> (граматика
    /// області): парсер знає його ще ДО того, як ім'я впізнано, а тут він
    /// потрібен, щоб відрізнити <c>ROW.</c> вкладеної області від <c>ROW.</c>
    /// поточної (<see cref="ReadsRow"/>). Розбіжність двох переліків ловить
    /// <c>RegistryAggregateFunctionTests.Області_рядка_в_парсері_й_обчислювачі_збігаються</c>.
    /// </remarks>
    public static readonly FrozenSet<string> RowScopeNames =
        new[] { One, Sum, Average, Minimum, Maximum, Count }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> MethodologyNames =
        new[] { Find, One, Field, Sum, Average, Minimum, Maximum, Count }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TemplateNames =
        new[] { Find, One, Sum, Average, Minimum, Maximum, Count }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Чи обчислює цю функцію <see cref="RegistryForms"/>, а не каталог діалекту.
    /// </summary>
    /// <param name="name">Ім'я з виклику.</param>
    /// <param name="dialect">Діалект виразу.</param>
    /// <remarks>
    /// ⚠ У шаблонах <c>REGFIELD</c> лишається за <c>FunctionRegistry</c>: там
    /// аргументи йдуть ГРУПАМИ, і діапазон на місці запису вже дає <c>#VALUE</c>
    /// (<c>TemplateFunctions.RegistryField</c>) — міняти це заради шляху не
    /// потрібно, шлях розбирає спільний <see cref="FieldPath"/>. У методологіях
    /// <c>REGFIELD</c> — нова функція ярусу <c>Extension</c>, і каталог
    /// <c>MethodologyFunctions</c> про довідники не знає.
    ///
    /// ⛔ Регістр — за правилом діалекту: у методологіях значущий (як увесь
    /// <c>DialectCatalog</c>), у шаблонах — ні (як <c>FunctionRegistry</c>).
    /// Діалект звітів цих функцій не має (<c>02b</c> §8a).
    /// </remarks>
    public static bool Handles(string name, ExpressionDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(name);

        return dialect switch
        {
            ExpressionDialect.Methodology => MethodologyNames.Contains(name),
            ExpressionDialect.Template => TemplateNames.Contains(name),
            _ => false,
        };
    }

    /// <summary>Обчислює спецформу довідника.</summary>
    /// <param name="node">Виклик.</param>
    /// <param name="context">Контекст обчислення (можливо, область рядка).</param>
    /// <param name="budget">Бюджет обчислення — той самий, що в обчислювача.</param>
    /// <param name="evaluate">
    /// Обчислення підвиразу як скаляра в заданому контексті — делегат обчислювача.
    /// </param>
    internal static ExpressionValue Invoke(
        FunctionNode node,
        IEvaluationContext context,
        EvaluationBudget budget,
        Func<AstNode, IEvaluationContext, ExpressionValue> evaluate)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(evaluate);

        switch (node.Name.ToUpperInvariant())
        {
            case One:
                return RegOne(node.Arguments, context, budget, evaluate);

            case Field:
                if (node.Arguments.Count != 2)
                {
                    return ExpressionValue.Error(ExpressionErrors.BadValue);
                }

                var entry = evaluate(node.Arguments[0], context);
                var path = evaluate(node.Arguments[1], context);
                return FieldPath(entry, path, context);

            case Sum:
                return Aggregate(AggregateKind.Sum, node.Arguments, context, budget, evaluate);

            case Average:
                return Aggregate(AggregateKind.Average, node.Arguments, context, budget, evaluate);

            case Minimum:
                return Aggregate(AggregateKind.Minimum, node.Arguments, context, budget, evaluate);

            case Maximum:
                return Aggregate(AggregateKind.Maximum, node.Arguments, context, budget, evaluate);

            case Count:
                return Aggregate(AggregateKind.Count, node.Arguments, context, budget, evaluate);

            default:
                return RegFind(node.Arguments, context, evaluate);
        }
    }

    /// <summary>
    /// <c>REGFIND(R, k1 [, k2 …])</c> — запис за первинним ключем.
    /// </summary>
    /// <remarks>
    /// ⚠ Порядок відповідей не випадковий. Помилка частини — першою: вона
    /// поширюється, як усюди (§6.4). Далі <c>null</c>-частина — <c>null</c> ще
    /// ДО пошуку (контракт <see cref="IRegistrySnapshot.FindByPrimaryKey"/>):
    /// «ключ не заповнено» — легітимна порожнеча, а не розбіжність даних. Лише
    /// тоді — знімок.
    ///
    /// ⛔ Не знайдено — <c>#N/A</c>, а НЕ <c>#REF</c>: опис довідника цілий, у
    /// ДАНИХ немає запису, і виправляють довідник, а не формулу
    /// (<see cref="ExpressionErrors.NotAvailable"/>).
    /// </remarks>
    private static ExpressionValue RegFind(
        IReadOnlyList<AstNode> args,
        IEvaluationContext context,
        Func<AstNode, IEvaluationContext, ExpressionValue> evaluate)
    {
        if (args.Count < 2)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var code = RegistryCode(evaluate(args[0], context), out var codeFailure);
        if (code is null)
        {
            return codeFailure;
        }

        var parts = new List<ExpressionValue>(args.Count - 1);
        var anyNull = false;
        for (var i = 1; i < args.Count; i++)
        {
            var part = evaluate(args[i], context);
            if (part.IsError)
            {
                return part;
            }

            anyNull |= part.IsNull;
            parts.Add(part);
        }

        if (anyNull)
        {
            return ExpressionValue.Null;
        }

        if (context.Registries is not { } snapshot)
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        return Single(snapshot.FindByPrimaryKey(code, parts));
    }

    /// <summary>
    /// <c>REGONE(R, f)</c> — рівно один видимий запис, для якого <c>f</c> = <c>TRUE</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Переглядаються ВСІ рядки, навіть після другого збігу, і це не
    /// марнотратство: помилка умови на будь-якому рядку мусить перемогти
    /// <c>#MULTI</c>, інакше відповідь залежала б від того, де саме в порядку
    /// <c>(Ordinal, Id)</c> стоїть зіпсований рядок відносно збігів. Перша
    /// помилка за цим порядком — завжди та сама (§5.4).
    ///
    /// ⚠ Кожен рядок коштує крок бюджету ПОНАД вузли умови — той самий
    /// принцип, що в <c>Evaluator.EvaluateGroup</c>: повний перегляд довідника
    /// на 30 тис. записів мусить упертися в <c>#BUDGET</c>, а не тихо рахувати.
    ///
    /// ⚠ Рядки — з <see cref="Rows"/>: той самий індексний шлях, що в
    /// агрегатів (<c>ROW.&lt;Lookup&gt; = &lt;вираз без ROW&gt;</c>, RT-20b).
    /// Шляху «рівностями покриває довільний ключ» (§5.4) немає: знімок не
    /// відкриває складу ключів (<see cref="IRegistrySnapshot"/>), і без нового
    /// методу інтерфейсу перевірити покриття нічим. Відповіді це не змінює —
    /// лише ціну: такий фільтр іде повним переглядом у бюджет.
    ///
    /// ⚠ Умова, що дала <c>null</c>, рядок не бере — як в агрегатах (§5.4).
    /// Не-булева — <c>#VALUE</c>: публікацію обійшли.
    /// </remarks>
    private static ExpressionValue RegOne(
        IReadOnlyList<AstNode> args,
        IEvaluationContext context,
        EvaluationBudget budget,
        Func<AstNode, IEvaluationContext, ExpressionValue> evaluate)
    {
        if (args.Count != 2)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var code = RegistryCode(evaluate(args[0], context), out var codeFailure);
        if (code is null)
        {
            return codeFailure;
        }

        if (context.Registries is not { } snapshot || Rows(code, args[1], snapshot, context, evaluate) is not { } entries)
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        long? found = null;
        var many = false;
        foreach (var entryId in entries)
        {
            if (!budget.TryConsume())
            {
                return ExpressionValue.Error(ExpressionErrors.BudgetExceeded);
            }

            var verdict = evaluate(args[1], new RegistryRowScope(context, entryId));
            if (verdict.IsError)
            {
                return verdict;
            }

            if (verdict.IsNull)
            {
                continue;
            }

            if (verdict.Type != ExpressionValueType.Boolean)
            {
                return ExpressionValue.Error(ExpressionErrors.BadValue);
            }

            if (!(bool)verdict.Value!)
            {
                continue;
            }

            if (found is null)
            {
                found = entryId;
            }
            else
            {
                many = true;
            }
        }

        if (many)
        {
            return ExpressionValue.Error(ExpressionErrors.MultipleMatches);
        }

        return found is { } id
            ? ExpressionValue.Number(id)
            : ExpressionValue.Error(ExpressionErrors.NotAvailable);
    }

    /// <summary>
    /// Агрегат по рядках довідника: <c>REGSUM/REGAVG/REGMIN/REGMAX(R, f, e)</c>,
    /// <c>REGCOUNT(R, f)</c> (§5.4).
    /// </summary>
    /// <remarks>
    /// ⛔ Порожня множина — НЕ однаково для всіх, і це не недогляд, а правило
    /// <c>SUM</c>/<c>AVERAGE</c> (<c>02b</c> §6.1, <c>TemplateFunctions</c>):
    /// сума й кількість «ні з чого» — нуль, а середнє, мінімум і максимум «ні з
    /// чого» — <c>null</c>. Нуль там стверджував би виміряне значення, і
    /// відсутній у складі компонент дав би «середня молярна маса 0».
    ///
    /// ⚠ <c>null</c> поглинається двічі: умова <c>f</c> = <c>null</c> — рядок не
    /// входить (як у <c>REGONE</c>); вираз <c>e</c> = <c>null</c> — рядок входить
    /// у фільтр, але не в суму й не в дільник <c>REGAVG</c>. <c>REGCOUNT</c>
    /// рахує рядки за <c>f</c>, виразу не має.
    ///
    /// ⛔ Перша помилка — за порядком рядків <c>(Ordinal, Id)</c>, у межах рядка
    /// спершу <c>f</c>, потім <c>e</c>, — і повертається одразу: помилка
    /// поширюється, як скрізь (§6.4), а порядок рядків — частина контракту
    /// знімка, тож відповідь та сама в кожному прогоні.
    ///
    /// ⚠ Кожен рядок коштує крок бюджету ПОНАД вузли <c>f</c>/<c>e</c>, як у
    /// <c>REGONE</c>: повний перегляд 30 тис. рядків упирається в <c>#BUDGET</c>.
    /// Індексний шлях (<see cref="Rows"/>) перебирає лише дітей і тому в бюджет
    /// вкладається.
    ///
    /// ⚠ Арифметика — <see cref="decimal"/> без <c>IEvaluationArithmetic</c>:
    /// агрегати мають ярус <c>Extension</c> і в <c>Legacy</c>-версії не
    /// публікуються (<c>ECR-CALC-0433</c>), тож рахувати <c>double</c> їм нічого.
    /// Переповнення — <c>#VALUE</c>, а не виняток: одна формула не сміє зірвати
    /// прогін.
    /// </remarks>
    private static ExpressionValue Aggregate(
        AggregateKind kind,
        IReadOnlyList<AstNode> args,
        IEvaluationContext context,
        EvaluationBudget budget,
        Func<AstNode, IEvaluationContext, ExpressionValue> evaluate)
    {
        if (args.Count != (kind == AggregateKind.Count ? 2 : 3))
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var code = RegistryCode(evaluate(args[0], context), out var codeFailure);
        if (code is null)
        {
            return codeFailure;
        }

        if (context.Registries is not { } snapshot || Rows(code, args[1], snapshot, context, evaluate) is not { } entries)
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        var accumulator = new Accumulator(kind);
        foreach (var entryId in entries)
        {
            if (!budget.TryConsume())
            {
                return ExpressionValue.Error(ExpressionErrors.BudgetExceeded);
            }

            var scope = new RegistryRowScope(context, entryId);
            var verdict = evaluate(args[1], scope);
            if (verdict.IsError)
            {
                return verdict;
            }

            if (verdict.IsNull)
            {
                continue;
            }

            if (verdict.Type != ExpressionValueType.Boolean)
            {
                return ExpressionValue.Error(ExpressionErrors.BadValue);
            }

            if (!(bool)verdict.Value!)
            {
                continue;
            }

            if (kind == AggregateKind.Count)
            {
                accumulator.CountRow();
                continue;
            }

            var value = evaluate(args[2], scope);
            if (value.IsError)
            {
                return value;
            }

            if (value.IsNull)
            {
                continue;
            }

            if (!accumulator.TryAdd(value))
            {
                return ExpressionValue.Error(ExpressionErrors.BadValue);
            }
        }

        return accumulator.Outcome();
    }

    /// <summary>
    /// Рядки, які перебирає агрегат чи <c>REGONE</c>: за індексом, якщо фільтр
    /// це дозволяє, інакше — усі видимі.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="filter">Умова <c>f</c>.</param>
    /// <param name="snapshot">Знімок.</param>
    /// <param name="context">Зовнішній контекст (НЕ область рядка).</param>
    /// <param name="evaluate">Делегат обчислювача.</param>
    /// <returns>Рядки в порядку <c>(Ordinal, Id)</c>; <c>null</c> — довідника немає.</returns>
    /// <remarks>
    /// ⚠ **Індексний шлях (§5.4).** Верхній кон'юнкт <c>f</c> (гілки <c>AND</c>)
    /// виду <c>ROW.&lt;поле&gt; = &lt;вираз без ROW&gt;</c> (у будь-якому порядку
    /// сторін) з полем в ОДИН сегмент: вираз обчислюється ОДИН раз у зовнішньому
    /// контексті, і якщо дає id запису, а поле — <c>Lookup</c> цього довідника,
    /// кандидати — лише записи, що на нього посилаються
    /// (<see cref="IRegistrySnapshot.FindReferencing"/>). Усю умову <c>f</c> потім
    /// однаково рахують на кожному кандидаті — індекс звужує перегляд, а не
    /// замінює перевірку.
    ///
    /// ⚠ Будь-що інше — повний перегляд, і це завжди безпечно: вираз дав
    /// помилку, <c>null</c> (тоді <c>ROW.X = null</c> істинне саме для
    /// НЕзаповнених, а їх індекс не тримає) чи не id; поле не <c>Lookup</c>
    /// (знімок дає <c>null</c>); кон'юнкт під <c>OR</c>/<c>NOT</c>.
    ///
    /// ⚠ Єдина різниця з повним переглядом, названа, а не прихована: помилку
    /// ІНШОГО кон'юнкта на рядку, що не посилається на ціль, індексний шлях не
    /// бачить — рядка він не відкриває. Відповідь на рядках, які агрегат
    /// справді бере, та сама.
    /// </remarks>
    private static IReadOnlyList<long>? Rows(
        string code,
        AstNode filter,
        IRegistrySnapshot snapshot,
        IEvaluationContext context,
        Func<AstNode, IEvaluationContext, ExpressionValue> evaluate)
    {

        foreach (var conjunct in TopConjuncts(filter))
        {
            if (conjunct is not BinaryNode { Operator: BinaryOperator.Equal } equality)
            {
                continue;
            }

            var (field, target) = equality switch
            {
                { Left: RowFieldNode { Path.Count: 1 } left } when !ReadsRow(equality.Right) => (left.Path[0], equality.Right),
                { Right: RowFieldNode { Path.Count: 1 } right } when !ReadsRow(equality.Left) => (right.Path[0], equality.Left),
                _ => ((string?)null, (AstNode?)null),
            };

            if (field is null || target is null)
            {
                continue;
            }

            if (AsEntryId(evaluate(target, context)) is { } targetId
                && snapshot.FindReferencing(code, field, targetId) is { } children)
            {
                return children;
            }
        }

        return snapshot.GetEntries(code);
    }

    /// <summary>Верхні кон'юнкти умови: гілки <c>AND</c> зліва направо.</summary>
    /// <remarks>
    /// ⚠ Обхід — явним стеком, а не рекурсією: дерево довгого ланцюга
    /// <c>a AND b AND …</c> — лівий гребінь (<c>Evaluator.EvaluateScalar</c>,
    /// сторож глибини), і рекурсивний обхід мав би власну, ніким не обмежену
    /// глибину.
    /// </remarks>
    private static List<AstNode> TopConjuncts(AstNode filter)
    {
        var conjuncts = new List<AstNode>();
        var pending = new Stack<AstNode>();
        pending.Push(filter);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is BinaryNode { Operator: BinaryOperator.And } and)
            {
                pending.Push(and.Right);
                pending.Push(and.Left);
            }
            else
            {
                conjuncts.Add(node);
            }
        }

        return conjuncts;
    }

    /// <summary>
    /// Чи читає вираз рядок ПОТОЧНОЇ області (<c>ROW.</c> поза вкладеним агрегатом).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>ROW.</c> усередині умови чи виразу вкладеного <c>REGONE</c>/<c>REGSUM</c>
    /// належить ЙОГО області (§5.2) і від поточного рядка не залежить, тож
    /// <c>ROW.STREAM = REGONE('STREAM', ROW.NAME = '1D-2')</c> — індексний. Перший
    /// аргумент вкладеного агрегату (код) — у поточній області, як і будь-який
    /// інший вузол.
    ///
    /// ⛔ Невідомий вид вузла — «читає»: помилитися в бік повного перегляду
    /// коштує бюджету, в інший бік — хибної відповіді.
    /// </remarks>
    private static bool ReadsRow(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case RowFieldNode:
                    return true;

                case LiteralNode or SymbolReferenceNode or PeriodPropertyNode or ThisNode:
                    break;

                case CellReferenceNode { Row: not RowSelector.Predicate }:
                    break;

                case CellReferenceNode { Row: RowSelector.Predicate predicate }:
                    pending.Push(predicate.Condition);
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case BinaryNode binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.Condition);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.WhenFalse);
                    break;

                case FunctionNode function:
                    var own = RowScopeNames.Contains(function.Name) ? Math.Min(1, function.Arguments.Count) : function.Arguments.Count;
                    for (var i = 0; i < own; i++)
                    {
                        pending.Push(function.Arguments[i]);
                    }

                    break;

                default:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Значення <c>ROW.a.b</c> — поле рядка, який перебирає найближча область.
    /// </summary>
    /// <param name="node">Вузол поля рядка.</param>
    /// <param name="context">Контекст обчислення.</param>
    /// <remarks>
    /// ⚠ Найближча область — це сам <paramref name="context"/>: вкладений
    /// <c>REGONE</c> обгортає зовнішню область своєю, тож внутрішній <c>ROW</c>
    /// затіняє зовнішній (§5.2) без жодного окремого механізму.
    ///
    /// ⚠ Поза областю — <c>#REF</c>. Парсер таке відхиляє
    /// (<c>expr.rowReferenceOutsideScope</c>); тут друга межа на випадок, коли
    /// дерево зібрали в обхід розбору. Правила довідника (<c>THIS</c>, рядок
    /// правила) дають свою область кроком RT-17a.
    /// </remarks>
    internal static ExpressionValue RowField(RowFieldNode node, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        if (context is not RegistryRowScope scope || scope.Registries is not { } snapshot)
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        return Walk(scope.EntryId, node.Path, snapshot.GetField);
    }

    /// <summary>
    /// <c>REGFIELD(entry, p)</c> — значення поля; <c>p</c> з крапками проходить
    /// через <c>Lookup</c>-поля (<c>'COMPONENT.MW'</c>).
    /// </summary>
    /// <param name="entry">Значення першого аргументу — id запису.</param>
    /// <param name="path">Значення другого аргументу — код поля чи шлях.</param>
    /// <param name="context">Джерело даних довідника.</param>
    /// <remarks>
    /// ⚠ Однокрокова форма <c>REGFIELD([Lookup], 'Field')</c> працює як раніше:
    /// та сама перевірка аргументів і той самий <c>#REF</c> на відсутнє поле.
    ///
    /// ⛔ Джерело — знімок довідників (<see cref="IEvaluationContext.Registries"/>),
    /// якщо контекст його має, інакше — наявний
    /// <see cref="IEvaluationContext.GetRegistryField"/>. Порядок саме такий: знімок
    /// бачить видимість на дату (невидимий запис → <c>#REF</c>) і прийде в шаблони
    /// кроком RT-24, а доти жоден шаблонний контекст знімка не має — тобто
    /// поведінка наявних формул не змінюється ні на біт.
    ///
    /// ⚠ Сегмент порожній (<c>'A..B'</c>, <c>'.A'</c>) — <c>#REF</c>: поля з
    /// порожнім кодом не буває (<c>EcrCode.Pattern</c>), це описка, яку ловить
    /// публікація (перевірка 16, RT-21).
    /// </remarks>
    public static ExpressionValue FieldPath(ExpressionValue entry, ExpressionValue path, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (entry.IsError)
        {
            return entry;
        }

        if (path.IsError)
        {
            return path;
        }

        // Lookup ще не заповнено — легітимна порожнеча (02b §6.3, R-11).
        if (entry.IsNull)
        {
            return ExpressionValue.Null;
        }

        if (AsEntryId(entry) is not { } entryId || path.Type != ExpressionValueType.Text)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var segments = ((string)path.Value!).Split('.');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return ExpressionValue.Error(ExpressionErrors.BadReference);
            }
        }

        Func<long, string, ExpressionValue> step = context.Registries is { } snapshot
            ? snapshot.GetField
            : context.GetRegistryField;

        return Walk(entryId, segments, step);
    }

    /// <summary>
    /// Прохід шляху від запису: кожен сегмент, крім останнього, — <c>Lookup</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Проміжний <c>null</c> (<c>Lookup</c> не заповнено) — <c>null</c> усього
    /// шляху (<c>R-11</c>), помилка — вона сама. Проміжне значення, що не є id
    /// запису, — <c>#REF</c>: сегмент виявився не <c>Lookup</c>-полем, тобто опис
    /// змінили після публікації (перевірка 16). Невидимий запис на будь-якому
    /// кроці дає <c>#REF</c> сам знімок (<see cref="IRegistrySnapshot.GetField"/>).
    /// </remarks>
    private static ExpressionValue Walk(
        long entryId, IReadOnlyList<string> path, Func<long, string, ExpressionValue> step)
    {
        var value = step(entryId, path[0]);
        for (var i = 1; i < path.Count; i++)
        {
            if (value.IsError || value.IsNull)
            {
                return value;
            }

            if (AsEntryId(value) is not { } next)
            {
                return ExpressionValue.Error(ExpressionErrors.BadReference);
            }

            value = step(next, path[i]);
        }

        return value;
    }

    /// <summary>Код довідника з першого аргументу; <c>null</c> — з відмовою в <paramref name="failure"/>.</summary>
    /// <remarks>
    /// ⚠ У рантаймі — будь-який текст; що це ЛІТЕРАЛ, вимагає публікація
    /// (перевірка 15, <c>expr.registryCodeMustBeLiteral</c>, RT-21).
    /// </remarks>
    private static string? RegistryCode(ExpressionValue value, out ExpressionValue failure)
    {
        if (value.IsError)
        {
            failure = value;
            return null;
        }

        if (value.Type != ExpressionValueType.Text || string.IsNullOrWhiteSpace((string)value.Value!))
        {
            failure = ExpressionValue.Error(ExpressionErrors.BadValue);
            return null;
        }

        failure = default;
        return (string)value.Value!;
    }

    /// <summary>Відповідь пошуку: <c>null</c> → <c>#REF</c>, 0 → <c>#N/A</c>, 1 → id, більше → <c>#MULTI</c>.</summary>
    private static ExpressionValue Single(IReadOnlyList<long>? found)
        => found switch
        {
            null => ExpressionValue.Error(ExpressionErrors.BadReference),
            { Count: 0 } => ExpressionValue.Error(ExpressionErrors.NotAvailable),
            { Count: 1 } => ExpressionValue.Number(found[0]),
            _ => ExpressionValue.Error(ExpressionErrors.MultipleMatches),
        };

    /// <summary>id запису зі значення: ціле число в межах <see cref="long"/>, інакше <c>null</c>.</summary>
    private static long? AsEntryId(ExpressionValue value)
        => value.AsNumber() is { } number
           && number == decimal.Truncate(number)
           && number >= long.MinValue
           && number <= long.MaxValue
            ? (long)number
            : null;

    private enum AggregateKind : byte
    {
        Sum,
        Average,
        Minimum,
        Maximum,
        Count,
    }

    /// <summary>Накопичувач агрегату: одне значення на рядок, що пройшов фільтр.</summary>
    /// <remarks>
    /// ⚠ <c>REGSUM</c>/<c>REGAVG</c> приймають лише числа; <c>REGMIN</c>/<c>REGMAX</c>
    /// — числа АБО дати (§5.4), але не суміш: «менше» між числом і датою не
    /// визначене, і <c>#VALUE</c> тут чесніший за будь-яке впорядкування.
    /// </remarks>
    private sealed class Accumulator(AggregateKind kind)
    {
        private decimal _total;
        private int _count;
        private ExpressionValue? _best;

        public void CountRow() => _count++;

        public bool TryAdd(ExpressionValue value)
        {
            switch (kind)
            {
                case AggregateKind.Sum or AggregateKind.Average:
                    if (value.AsNumber() is not { } number)
                    {
                        return false;
                    }

                    try
                    {
                        _total += number;
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }

                    _count++;
                    return true;

                case AggregateKind.Minimum or AggregateKind.Maximum:
                    if (value.Type is not (ExpressionValueType.Number or ExpressionValueType.Date))
                    {
                        return false;
                    }

                    if (_best is not { } best)
                    {
                        _best = value;
                        return true;
                    }

                    if (best.Type != value.Type)
                    {
                        return false;
                    }

                    var order = value.Type == ExpressionValueType.Date
                        ? ((DateTime)value.Value!).CompareTo((DateTime)best.Value!)
                        : value.AsNumber() is { } candidate && best.AsNumber() is { } current
                            ? candidate.CompareTo(current)
                            : 0;

                    if (kind == AggregateKind.Minimum ? order < 0 : order > 0)
                    {
                        _best = value;
                    }

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Значення агрегату після перебору.</summary>
        /// <remarks>
        /// ⚠ Ім'я не «Result»: сторож <c>LayerRulesTests.Правило_5</c> читає
        /// текст джерел, і виклик через крапку він приймає за блокування задачі.
        /// </remarks>
        public ExpressionValue Outcome()
            => kind switch
            {
                AggregateKind.Sum => ExpressionValue.Number(_total),
                AggregateKind.Count => ExpressionValue.Number(_count),
                AggregateKind.Average => _count == 0 ? ExpressionValue.Null : ExpressionValue.Number(_total / _count),
                _ => _best ?? ExpressionValue.Null,
            };
    }
}
