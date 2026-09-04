// src/Ecr.Application/Ports/IFormulaEngine.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Ports;

/// <summary>
/// Рушій виразів. Один парсер на обидва діалекти; NCalc використовується як
/// обчислювач, а не як парсер нашої мови (D-19, D-20).
/// Граматика — <see href="02b-expressions.md">02b-expressions.md</see>.
/// </summary>
public interface IFormulaEngine
{
    /// <summary>Розбирає вираз. Помилка синтаксису — результат, а не виняток.</summary>
    public ParseResult Parse(string expression, ExpressionDialect dialect);

    /// <summary>
    /// Витягує залежності виразу. Діапазони рядків розкриваються в явний список
    /// <c>RowKey</c> на момент <c>Publish</c> — у рантаймі діапазонів не існує (B03 §4).
    /// </summary>
    public IReadOnlyList<FormulaDependencyRef> ExtractDependencies(ParsedExpression expression, DependencyContext context);

    /// <summary>Обчислює вираз.</summary>
    public EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context);

    /// <summary>
    /// Топологічний порядок обчислення. Цикл повертається як помилка публікації,
    /// а не як тихо неправильне число (ФВ-9.4).
    /// </summary>
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes);
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
/// Вузол графа обчислення для <see cref="IFormulaEngine.BuildEvaluationOrder"/> —
/// контрактна проєкція <c>cfg.FormulaDef</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде</b> (спершу було «слабке»; уточнено за
/// схемою після рев'ю Етапу 0). Поля відповідають колонкам
/// <c>cfg.FormulaDef</c> (<c>02a</c> рядок 374): формула ідентифікується
/// <c>Id</c>, прив'язана до <c>TableDefId</c>, а її <c>Scope</c> визначає,
/// котре з <c>ColumnDefId</c>/<c>RowDefId</c> заповнене — це закріплено
/// перевіркою <c>CK_Formula_Scope</c>. Саме тому вузол оперує
/// <c>RowDefId</c>, а не <c>RowKey</c>: формула належить <b>визначенню</b>
/// рядка, а не його ключу.
/// Результат сортування лягає в <c>cfg.FormulaDef.EvaluationOrder</c> — воно
/// «обчислюється при <c>Publish</c>, не в рантаймі» (ФВ-9.4).
/// </remarks>
/// <param name="FormulaDefId">Ідентифікатор формули — він же вузол графа.</param>
/// <param name="TableDefId">Таблиця, якій належить формула.</param>
/// <param name="Scope">Рівень: колонка, рядок або комірка.</param>
/// <param name="ColumnDefId">Колонка; заповнена для <c>Column</c> і <c>Cell</c>.</param>
/// <param name="RowDefId">Рядок; заповнений для <c>Row</c> і <c>Cell</c>.</param>
/// <param name="DependsOnFormulaDefIds">Формули, від яких залежить ця.</param>
public sealed record FormulaNode(
    int FormulaDefId,
    int TableDefId,
    FormulaScope Scope,
    int? ColumnDefId,
    int? RowDefId,
    IReadOnlyList<int> DependsOnFormulaDefIds);
