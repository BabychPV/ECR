using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Що саме прогін перерахунку мусить ПРОЧИТАТИ: замикання таблиць, які цілі
/// читають, плюс власні таблиці цілей, і чи потрібен суміжний період.
/// </summary>
/// <remarks>
/// ⛔ Директива №14 частина 3, <c>CAL-02</c>. До цього
/// <c>RecalculationService.LoadValuesAsync</c> читав УСІ екземпляри таблиць
/// документа (у великому шаблоні — ~90 таблиць × усі рядки × усі комірки), і
/// робив це ДВІЧІ: попередній період завантажувався завжди, щойно
/// <c>PeriodKey.Sequence &gt; 1</c>. Правка однієї комірки коштувала читання
/// всього документа за два періоди — і це на КОЖНЕ автозбереження.
///
/// ⚠ Замикання рахується по ЦІЛЯХ, а не по таблицях, і одного проходу
/// достатньо — це не спрощення, а властивість набору цілей. Формула, яку
/// цей прогін обчислює, — завжди в <c>targets</c>; формула, якої там немає,
/// не обчислюється, отже її значення береться зі сховища як є, і те, що
/// читає ВОНА, цьому прогону не потрібне. Тому «читає транзитивно» тут
/// збігається з об'єднанням прямих читань усіх цілей: множина цілей уже
/// замкнена каскадом (<see cref="RecalculationService.Plan"/> розкриває
/// залежних транзитивно).
///
/// ⚠ Предикати динамічних діапазонів (<c>RowMode = Dynamic</c>) окремої
/// ознаки не потребують, хоча їхні внутрішні посилання в
/// <c>cfg.FormulaDependency</c> НЕ зберігаються (<c>DependencyExtractor.Visit</c>
/// не спускається в <c>RowSelector.Predicate.Condition</c>). Причина — межа
/// самої мови: <c>PredicateValidator</c> пускає в умову лише посилання виду
/// <c>RowSelector.Current</c> (<c>PredicateValidator.cs:93-97</c>), а
/// <c>Current</c> парсер створює рівно тоді, коли посилання односегментне
/// (<c>Parser.cs:762-779</c>), тобто без коду аркуша й таблиці. Отже умова
/// предиката не може вийти за таблицю, на яку сам предикат і вказує, — а та
/// в залежності записана.
/// </remarks>
/// <param name="TableDefIds">
/// Таблиці, чиї екземпляри треба прочитати; <c>null</c> — <b>усі</b>
/// (див. <see cref="Compute"/>, випадок невідомого читання).
/// </param>
/// <param name="ReadsOtherPeriod">
/// Чи посилається хоч одна ціль на інший період (<c>[Period:-1]</c>). Лише
/// тоді має сенс другий прохід читання.
/// </param>
public sealed record RecalculationReadScope(IReadOnlySet<int>? TableDefIds, bool ReadsOtherPeriod)
{
    /// <summary>Вид залежності «інший період» (<c>cfg.FormulaDependency.DependsOnKind</c> = 3).</summary>
    private const byte CrossPeriodKind = 3;

    /// <summary>Читати все, що є: скільки саме — невідомо.</summary>
    public static RecalculationReadScope Unrestricted { get; } = new(null, ReadsOtherPeriod: true);

    /// <summary>Чи є у виразі хоч одне посилання на комірку.</summary>
    /// <remarks>
    /// ⚠ Питання дешеве й навмисно грубе: обхід AST без резолвінгу посилань.
    /// Воно відрізняє формулу, якій читати НІЧОГО (<c>= 42</c>,
    /// <c>[Period].Days * 2</c>), від формули, яка читає щось, чого граф
    /// залежностей не описує.
    /// </remarks>
    public static bool MentionsCells(AstNode? node)
        => node switch
        {
            null => false,
            CellReferenceNode => true,
            UnaryNode unary => MentionsCells(unary.Operand),
            BinaryNode binary => MentionsCells(binary.Left) || MentionsCells(binary.Right),
            ConditionalNode conditional =>
                MentionsCells(conditional.Condition)
                || MentionsCells(conditional.WhenTrue)
                || MentionsCells(conditional.WhenFalse),
            FunctionNode function => function.Arguments.Any(MentionsCells),
            _ => false,
        };

    /// <summary>Рахує замикання читання для набору цілей.</summary>
    /// <param name="dependencies">Розкриті залежності формул версії шаблону.</param>
    /// <param name="targets">Формули, які цей прогін обчислює.</param>
    /// <param name="tableByFormula">Формула → таблиця, у якій вона живе (куди пише).</param>
    /// <param name="targetsWithUnknownReads">
    /// Цілі, які ЧИТАЮТЬ комірки, але не мають жодного збереженого ребра в
    /// графі залежностей.
    /// </param>
    /// <remarks>
    /// ⛔ <paramref name="targetsWithUnknownReads"/> — не перестраховка, а
    /// названий випадок, який у цьому дереві вже описаний тестом «формула,
    /// додана в шаблон ПІСЛЯ введення даних» (<c>CascadeRecalculationTests</c>):
    /// її залежностей у графі ще немає, і саме заради неї існує повний прогін.
    /// Для такої формули граф не відповідає на питання «що вона читає», і
    /// звужувати читання за його мовчанням означало б порахувати
    /// <c>SUM([Items].[WHERE …].[Amount])</c> як <c>0</c> — тихо неправильне
    /// число замість помилки. Тому тут і тільки тут прогін повертається до
    /// старої поведінки: читати весь документ.
    ///
    /// ⚠ Формула зовсім без посилань на комірки (<c>= 42</c>) сюди НЕ входить:
    /// порожній граф для неї — правда, а не прогалина.
    /// </remarks>
    public static RecalculationReadScope Compute(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<int> targets,
        IReadOnlyDictionary<int, int> tableByFormula,
        IReadOnlyCollection<int> targetsWithUnknownReads)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(tableByFormula);
        ArgumentNullException.ThrowIfNull(targetsWithUnknownReads);

        if (targetsWithUnknownReads.Count > 0)
        {
            return Unrestricted;
        }

        var wanted = new HashSet<int>(targets);
        var tables = new HashSet<int>();

        // ⚠ Власна таблиця цілі — не «про всяк випадок»: формула пише в неї, а
        // `DAT-02` порівнює нове значення зі ЗБЕРЕЖЕНИМ. Без її зрізу
        // порівнювати було б ні з чим, і кожен прогін писав би все заново.
        foreach (var formulaId in wanted)
        {
            if (tableByFormula.TryGetValue(formulaId, out var own))
            {
                tables.Add(own);
            }
        }

        var otherPeriod = false;

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is not { } formulaId || !wanted.Contains(formulaId))
            {
                continue;
            }

            // ⚠ Вид залежності тут НЕ фільтрується: шапка й довідник таблиці не
            // несуть (`TableDefId is null`), а все, що таблицю несе, — читання
            // комірок. Фільтр за видом дав би ту саму множину, лише з іще одним
            // місцем, де перелік видів доводиться тримати в актуальному стані.
            if (dependency.TableDefId is { } tableDefId)
            {
                tables.Add(tableDefId);
            }

            if (dependency.DependsOnKind == CrossPeriodKind
                || dependency.PeriodOffset is { } offset && offset != 0)
            {
                otherPeriod = true;
            }
        }

        return new RecalculationReadScope(tables, otherPeriod);
    }

    /// <summary>Формули, які мають хоч одне збережене ребро на комірку.</summary>
    /// <remarks>
    /// ⚠ Саме «ребро на комірку», а не «будь-яке ребро»: залежність від шапки
    /// (<c>DependsOnKind = 1</c>) не описує жодної таблиці, і формула, у якої в
    /// графі лише вона, про свої читання комірок мовчить так само, як формула
    /// без графа взагалі.
    /// </remarks>
    public static HashSet<int> FormulasWithStoredCellEdges(IReadOnlyList<FormulaDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var known = new HashSet<int>();
        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is { } formulaId && dependency.TableDefId is not null)
            {
                known.Add(formulaId);
            }
        }

        return known;
    }
}
