// src/Ecr.Application/Ports/IFormulaEngine.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

/// <summary>
/// Рушій виразів. Один парсер на обидва діалекти; NCalc використовується як
/// обчислювач, а не як парсер нашої мови (D-19, D-20).
/// Граматика — <see href="02b-expressions.md">02b-expressions.md</see>.
/// </summary>
public interface IFormulaEngine
{
    /// <summary>Розбирає вираз. Помилка синтаксису — результат, а не виняток.</summary>
    ParseResult Parse(string expression, ExpressionDialect dialect);

    /// <summary>
    /// Витягує залежності виразу. Діапазони рядків розкриваються в явний список
    /// <c>RowKey</c> на момент <c>Publish</c> — у рантаймі діапазонів не існує (B03 §4).
    /// </summary>
    IReadOnlyList<FormulaDependencyRef> ExtractDependencies(ParsedExpression expression, DependencyContext context);

    /// <summary>Обчислює вираз.</summary>
    EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context);

    /// <summary>
    /// Топологічний порядок обчислення. Цикл повертається як помилка публікації,
    /// а не як тихо неправильне число (ФВ-9.4).
    /// </summary>
    OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes);
}

// ─────────────────────────────────────────────────────────────────────────────
// Типи, яких у пакеті не було (Q-014). Чернетка на затвердження.
// Оголошені поруч із портом — за конвенцією самого пакета (пор. IBackgroundJobScheduler.cs,
// де в тому самому файлі живуть IBackgroundJob, IJobProgress і JobStatus).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Розкрита залежність формули — контрактна проєкція <c>cfg.FormulaDependency</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде.</b> Поля дослівно повторюють колонки
/// <c>cfg.FormulaDependency</c> (`02a-db-schema.md` рядок 401) і тип
/// <c>Ecr.Expressions.Binding.ExtractedDependency</c> з `05d`, а
/// <c>FormulaEngine.ExtractDependencies</c> у своєму <c>TODO</c> прямо каже
/// «делегувати dependencyExtractor і <b>спроєктувати в контрактний тип</b>».
/// Колонки <c>Id</c>, <c>SourceKind</c>, <c>FormulaDefId</c>, <c>BindingId</c>
/// сюди не входять: їх проставляє той, хто зберігає залежність, а не той, хто
/// її витягує з виразу.
/// </remarks>
/// <param name="DependsOnKind">0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</param>
/// <param name="TableDefId">Таблиця, на яку вказує залежність.</param>
/// <param name="RowKey"><b>Конкретний</b> рядок; <c>null</c> для предиката (B03 §4).</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="FilterJson">Предикат для <c>RowMode = Dynamic</c>.</param>
/// <param name="PeriodOffset"><c>[Period:-1]</c> → −1.</param>
/// <param name="SortOrder">Позиція в розкритому діапазоні.</param>
public sealed record FormulaDependencyRef(
    byte DependsOnKind,
    int? TableDefId,
    string? RowKey,
    int? ColumnDefId,
    string? FilterJson,
    short? PeriodOffset,
    int SortOrder);

/// <summary>
/// Контекст витягування залежностей: те, чого немає в самому виразі, але без
/// чого скорочені форми посилань не резолвляться (02b §3.1).
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Два останні поля дослівно повторюють
/// параметри <c>DependencyExtractor.Extract(AstNode, int currentTableDefId,
/// string? currentRowKey)</c> і <c>ReferenceResolver.Resolve(...)</c> з `05d`.
/// <see cref="TemplateVersionId"/> додано мною: резолвер працює зі
/// <c>TemplateVersionSnapshot</c>, і без ідентифікатора версії порт не може
/// його дістати.
/// </remarks>
/// <param name="TemplateVersionId">Версія шаблону, у межах якої резолвляться коди.</param>
/// <param name="CurrentTableDefId">Таблиця, в якій живе формула — для скорочених форм.</param>
/// <param name="CurrentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
public sealed record DependencyContext(
    int TemplateVersionId,
    int CurrentTableDefId,
    string? CurrentRowKey);

/// <summary>
/// Вузол графа обчислення для <see cref="IFormulaEngine.BuildEvaluationOrder"/>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — слабке (здогадка).</b> У пакеті немає жодного
/// опису цього типу. Форма виведена з двох фактів:
/// <see cref="OrderingResult"/> повертає <c>IReadOnlyList&lt;int&gt; Order</c>,
/// тобто вузол мусить мати цілий ідентифікатор; а <c>TopologicalSorter</c>
/// працює з <c>DependencyGraph</c>, побудованим із ребер «залежить від»,
/// тобто вузол мусить принести список своїх залежностей.
/// Решта полів (<see cref="TableDefId"/>, <see cref="RowKey"/>,
/// <see cref="ColumnDefId"/>) потрібні, щоб показати користувачеві
/// <b>які саме</b> формули замкнулися в цикл — цього прямо вимагає
/// <c>TopologicalSorter.Sort</c>: «ПОВЕРНУТИ шлях циклу, а не просто прапорець».
/// </remarks>
/// <param name="FormulaDefId">Ідентифікатор формули — він же вузол графа.</param>
/// <param name="TableDefId">Таблиця формули.</param>
/// <param name="RowKey">Рядок; <c>null</c> для формул рівня колонки.</param>
/// <param name="ColumnDefId">Колонка; <c>null</c> для формул рівня рядка.</param>
/// <param name="DependsOnFormulaDefIds">Формули, від яких залежить ця.</param>
public sealed record FormulaNode(
    int FormulaDefId,
    int TableDefId,
    string? RowKey,
    int? ColumnDefId,
    IReadOnlyList<int> DependsOnFormulaDefIds);
