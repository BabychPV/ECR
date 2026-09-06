// src/Ecr.Application/Ports/IFormulaEngine.cs

using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
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
    /// <remarks>
    /// ⛔ Знімок приходить ПАРАМЕТРОМ, а не з кешу метаданих (<c>H-3</c>,
    /// директива №06 §1). Доти метод діставав його сам через
    /// <c>ITemplateStructure.Get</c> — і саме тому ним не міг скористатися
    /// ніхто: і редактор виразів, і публікація працюють над **чернеткою**,
    /// якої в кеші немає за побудовою. Обидва будували знімок із графа
    /// сутностей і йшли повз порт, тобто тримали по власній копії обходу
    /// AST — а дві копії відповіді на питання «від чого залежить формула»
    /// розходяться на першій правці.
    ///
    /// ⚠ Друга причина — чистота <c>Ecr.Expressions</c> (<c>B03</c> §2):
    /// збірка має лишатися лексером, парсером, AST і компілятором із
    /// залежностями BCL + NCalc. Звертання до кешу метаданих із фасада над
    /// нею цю межу стирало.
    /// </remarks>
    /// <param name="expression">Розібраний вираз.</param>
    /// <param name="snapshot">
    /// Знімок структури версії, у межах якої резолвляться посилання на комірки;
    /// <c>null</c> — структури немає. ⚠ Це не «полегшений режим»: у діалекті
    /// методологій посилань на комірки немає за побудовою (парсер їх відхиляє),
    /// тож резолвити нічого, а залежності між формулами <c>!Code</c> видно і
    /// без структури.
    /// </param>
    /// <param name="context">Місце формули: таблиця, рядок, колонка.</param>
    /// <returns>Залежності і зауваження, здобуті тим самим обходом.</returns>
    public DependencyExtraction ExtractDependencies(
        ParsedExpression expression, TemplateVersionSnapshot? snapshot, DependencyContext context);

    /// <summary>Обчислює вираз.</summary>
    /// <param name="expression">Розібраний вираз.</param>
    /// <param name="context">Джерело значень.</param>
    /// <param name="mode">
    /// Арифметичний режим версії (<c>ФВ-9.9</c>).
    /// </param>
    /// <remarks>
    /// ⛔ Режим — **параметр виклику**, а не стан рушія: рушій один на
    /// застосунок, і прогони різних версій ідуть одночасно. Поле режиму
    /// означало б, що <c>Legacy</c>-прогін здатен посеред виразу почати
    /// рахувати в <c>decimal</c>, бо сусідній потік перемкнув режим.
    ///
    /// ⚠ Умовчання — <c>Strict</c>, і це не рішення про методології, а
    /// збереження чинної поведінки двох інших викликачів
    /// (<c>RecalculationService</c>, <c>ValidationEngine</c>): вони рахують
    /// формули ШАБЛОНІВ, тобто діалект A, який наскрізь <c>decimal</c> за
    /// побудовою. Мовчазний вибір мусить збігатися з тим, що було, а не бути
    /// новим твердженням про числа.
    /// </remarks>
    public EvaluationResult Evaluate(
        ParsedExpression expression,
        IEvaluationContext context,
        Domain.Enums.NumericMode mode = Domain.Enums.NumericMode.Strict);

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
    int SortOrder)
{
    /// <summary>
    /// Код формули, на яку посилається токен <c>!Code</c>; <c>null</c> — це
    /// залежність іншого виду.
    /// </summary>
    /// <remarks>
    /// ⛔ Розпаковування живе ТУТ, в одному місці, бо кодування неочевидне:
    /// <c>cfg.FormulaDependency</c> не має колонки під ім'я символу, тож обхід
    /// AST кладе його в <see cref="RowKey"/> при порожньому
    /// <see cref="TableDefId"/>. Повторити цю здогадку на боці викликача
    /// означало б, що зміна кодування мовчки зіпсує граф обчислення: посилання
    /// на комірку завжди має таблицю, а <c>!Code</c> — ніколи.
    /// </remarks>
    public string? FormulaCode
        => DependsOnKind == DependencyExtractor.KindCell && TableDefId is null ? RowKey : null;
}

/// <summary>
/// Результат витягування: залежності і зауваження, здобуті одним обходом.
/// </summary>
/// <remarks>
/// ⛔ Зауваження повертаються РАЗОМ із залежностями, а не збираються окремою
/// перевіркою. Резолвінг посилань — це і є той самий обхід: він або дає
/// залежність, або пояснює, чому не дав («колонки „Apr“ немає в таблиці»).
/// Розділити їх означало б обходити вираз двічі й отримати два переліки
/// зауважень, які розходяться, — а на цьому тримається <c>ФВ-9.15a</c>:
/// редактор каже те саме, що публікація, і на тих самих позиціях.
/// </remarks>
/// <param name="Dependencies">Розкриті залежності виразу.</param>
/// <param name="Diagnostics">Що не резолвилося; порожньо — усе резолвилося.</param>
public sealed record DependencyExtraction(
    IReadOnlyList<FormulaDependencyRef> Dependencies,
    IReadOnlyList<ExpressionDiagnostic> Diagnostics);

/// <summary>
/// Контекст витягування залежностей: те, чого немає в самому виразі, але без
/// чого скорочені форми посилань не резолвляться (02b §3.1).
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде.</b> Поля дослівно повторюють параметри
/// <c>DependencyExtractor.Extract(AstNode, int currentTableDefId,
/// string? currentRowKey, …, int? currentColumnDefId)</c> і
/// <c>ReferenceResolver.Resolve(...)</c> з `05d`.
///
/// ⚠ Поля <c>TemplateVersionId</c> тут БІЛЬШЕ НЕМАЄ (<c>H-3</c>). Воно існувало
/// рівно заради того, щоб порт сам дістав знімок із кешу; тепер знімок
/// приходить параметром і сам несе свою версію. Лишити ідентифікатор означало б
/// дозволити виклик, у якому знімок і версія з різних місць.
/// </remarks>
/// <param name="CurrentTableDefId">Таблиця, в якій живе формула — для скорочених форм.</param>
/// <param name="CurrentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
/// <param name="CurrentColumnDefId">
/// Колонка, яку підставляє плейсхолдер <c>{Month}</c>; <c>null</c> — формула не
/// прив'язана до місячної колонки.
/// </param>
public sealed record DependencyContext(
    int CurrentTableDefId,
    string? CurrentRowKey,
    int? CurrentColumnDefId);

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
