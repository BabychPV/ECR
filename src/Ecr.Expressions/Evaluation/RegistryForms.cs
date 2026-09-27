using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Функції довідників, що обчислюються як СПЕЦФОРМИ: <c>REGFIND</c>,
/// <c>REGONE</c>, <c>REGFIELD</c> у методологіях (FEATURE-REGISTRY-TABLES §5.4,
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
            ExpressionDialect.Methodology =>
                string.Equals(name, Find, StringComparison.Ordinal)
                || string.Equals(name, One, StringComparison.Ordinal)
                || string.Equals(name, Field, StringComparison.Ordinal),
            ExpressionDialect.Template =>
                string.Equals(name, Find, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, One, StringComparison.OrdinalIgnoreCase),
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

        if (string.Equals(node.Name, One, StringComparison.OrdinalIgnoreCase))
        {
            return RegOne(node.Arguments, context, budget, evaluate);
        }

        if (string.Equals(node.Name, Field, StringComparison.OrdinalIgnoreCase))
        {
            if (node.Arguments.Count != 2)
            {
                return ExpressionValue.Error(ExpressionErrors.BadValue);
            }

            var entry = evaluate(node.Arguments[0], context);
            var path = evaluate(node.Arguments[1], context);
            return FieldPath(entry, path, context);
        }

        return RegFind(node.Arguments, context, evaluate);
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
    /// ⚠ Індексного шляху (§5.4, «якщо <c>f</c> рівностями покриває ключ») тут
    /// ще немає: він лише прискорює і відповіді не змінює (контракт
    /// <see cref="IRegistrySnapshot.FindReferencing"/>). Заводиться разом з
    /// індексним шляхом агрегатів (RT-20b).
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

        if (context.Registries is not { } snapshot || snapshot.GetEntries(code) is not { } entries)
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
}

/// <summary>
/// Область рядка довідника: контекст, у якому <c>ROW.</c> означає запис
/// <see cref="EntryId"/>; решту делегує зовнішньому контексту.
/// </summary>
/// <remarks>
/// ⚠ Декоратор, а не змінюване поле контексту: область — властивість ОДНОГО
/// спуску обчислювача, і вкладені <c>REGONE</c> отримують кожен свою, не
/// відновлюючи нічого в <c>finally</c>. Зовнішній контекст лишається незмінним.
///
/// ⚠ Делегує ВСЕ, зокрема члени інтерфейсу з типовою реалізацією
/// (<see cref="GetRegistryField"/>, <see cref="Registries"/>): без явного
/// делегування вони мовчки взяли б замовчування інтерфейсу, і всередині умови
/// <c>REGONE</c> <c>REGFIELD</c> і вкладений пошук давали б <c>#REF</c>.
/// </remarks>
/// <param name="inner">Зовнішній контекст.</param>
/// <param name="entryId">Запис, що перебирається.</param>
internal sealed class RegistryRowScope(IEvaluationContext inner, long entryId) : IEvaluationContext
{
    /// <summary>Запис області.</summary>
    public long EntryId { get; } = entryId;

    /// <inheritdoc />
    public IRegistrySnapshot? Registries => inner.Registries;

    /// <inheritdoc />
    public PeriodContext Period => inner.Period;

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference) => inner.Read(reference);

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
        => inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name) => inner.GetArgument(name);

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => inner.GetConstant(name);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name) => inner.GetFormulaResult(name);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name) => inner.GetHeader(name);

    /// <inheritdoc />
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => inner.GetRegistryField(registryEntryId, fieldCode);

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => inner.Convert(value, fromUnitCode, toUnitCode);
}
