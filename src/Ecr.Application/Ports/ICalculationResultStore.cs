// src/Ecr.Application/Ports/ICalculationResultStore.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис результатів прогону розрахунку.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Calculations.CalculationOutputWriter</c> був типізований напряму на
/// <c>EcrDbContext</c> і <c>BulkCellLoader</c>.
///
/// Пише <b>тільки</b> в <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c>.
/// У <c>doc.CellValue</c> результати методологій не потрапляють ніколи (D-69):
/// інакше нічний перерахунок писав би десятки мільйонів рядків у партиції
/// документів і роздував <c>aud.CellChange</c>.
/// </remarks>
public interface ICalculationResultStore
{
    /// <summary>
    /// B85 (хвіст B13): понад стільки результатів за один виклик <see cref="WriteResultsAsync"/> реалізація пише їх
    /// <c>SqlBulkCopy</c> у транзакції викликача; до межі — звичайний <c>SaveChanges</c>. Записувач відкриває
    /// транзакцію для такого чанка сам, щоб результати й трейс лягали атомарно, як одним <c>SaveChanges</c>.
    /// </summary>
    public const int BulkWriteThreshold = 300;

    /// <summary>
    /// Пише результати пакетно (<c>SqlBulkCopy</c>). <c>SaveChanges</c> у циклі
    /// заборонений: бюджет річного перерахунку — 10 хвилин (ПРД-13).
    /// </summary>
    public Task WriteResultsAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct);

    /// <summary>
    /// Пише трейс — лише те, що передбачає <paramref name="traceLevel"/>.
    /// Керуємо тим, <b>що</b> пишемо, а не скільки зберігаємо (ЗБР-3).
    /// </summary>
    public Task WriteTraceAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel, CancellationToken ct);

    /// <summary>
    /// Робить прогін актуальним: попередній перестає бути таким **у тій самій
    /// транзакції** (ФВ-9.11).
    /// </summary>
    /// <param name="calculationRunId">Прогін, який стає актуальним.</param>
    /// <param name="modulesProfileJson">Профіль по модулях; пишеться завжди.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Прапорець живе на ПРОГОНІ, а не на кожному результаті: у схемі
    /// <c>calc.CalculationResult</c> колонки <c>IsCurrent</c> немає, і це
    /// правильно — інакше «перемикання актуального прогону» означало б
    /// оновити десятки мільйонів рядків, і «одна транзакція» з вимоги стала б
    /// блокуванням партиції на хвилини. Результат актуальний тоді, коли
    /// актуальний його прогін.
    /// <para>
    /// Дві половини — зняти зі старого і поставити новому — мусять бути
    /// нероздільні: між ними існує стан, у якому актуальних прогонів нуль або
    /// два, і звіт, побудований у цю мить, не має правильної відповіді.
    /// Викликач відкриває транзакцію; реалізація спершу знімає актуальність
    /// зі старіших, потім ставить новому.
    /// </para>
    /// <para>
    /// ⛔ Прогін, СТАРІШИЙ за вже актуальний прогін тієї ж області (менший Id),
    /// актуальним не стає: завершується станом <c>Superseded</c> із причиною
    /// <c>jobs.calculationRunSupersededByNewer</c>, без винятку.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Прогони, що цим викликом стали НЕактуальними (Q-17, рішення людини 28.10: у базі лишаються результати лише
    /// актуального прогону). Для прогону, відмовленого як старішого за актуальний, — він сам. Порожньо — нікого.
    /// Викликач після коміту передає їх у <see cref="PurgeSupersededRunsAsync"/>.
    /// </returns>
    public Task<IReadOnlyList<SupersededRun>> SwitchCurrentRunAsync(
        long calculationRunId, string modulesProfileJson, CancellationToken ct);

    /// <summary>
    /// Прибирає прогони, що перестали бути актуальними (Q-17; <c>D-560</c>): результати, кроки, входи й сам рядок
    /// <c>calc.CalculationRun</c> (лишається, коли кроки й входи не прибираються — <c>IncludeTrace=false</c>).
    /// </summary>
    /// <param name="runs">Прогони з <see cref="SwitchCurrentRunAsync"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки рядків результатів, кроків і входів видалено (0, якщо прибирання вимкнено).</returns>
    /// <remarks>
    /// ⛔ Викликати ЛИШЕ ПІСЛЯ коміту перемикання: до коміту прогони ще актуальні для читачів. Помилка прибирання
    /// прогін не валить (реалізація пише в журнал і повертає 0): решту доїдає нічна задача
    /// <c>SupersededResultsSweepJob</c>. Інваріант: прогони <c>Current</c>/<c>Running</c> не прибираються ніколи;
    /// <c>Superseded</c> — одразу, <c>Failed</c> — лише старіший за актуальний того самого ланцюга й старший за добу.
    /// </remarks>
    public Task<int> PurgeSupersededRunsAsync(IReadOnlyList<SupersededRun> runs, CancellationToken ct);

    /// <summary>
    /// Переносить у прогін <paramref name="calculationRunId"/> результати методологій, яких цей прогін
    /// НЕ перераховував (RC14, приймальна №10, P2-4): прогін області одного аркуша стає актуальним для
    /// ВСЬОГО документа й періоду, тож без переносу результати інших аркушів зникли б із
    /// <see cref="ReadCurrentAsync"/>.
    /// </summary>
    /// <param name="calculationRunId">Новий прогін (ще не актуальний).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="methodologyIds">Методології (не версії), чиї результати переносяться.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки результатів перенесено.</returns>
    /// <remarks>
    /// ⚠ Джерело — ті самі рядки, що віддасть <see cref="ReadCurrentAsync"/> ДО перемикання актуальності,
    /// тож викликати треба до <see cref="SwitchCurrentRunAsync"/> у тій самій транзакції. Рядки лише
    /// додаються в контекст — зберігає викликач. Трейс (кроки, входи) не переноситься: він лишається за
    /// прогоном, що його порахував.
    /// <para>
    /// ⛔ N2-04: понад стелю реалізації — <c>BusinessRuleException</c> <c>ECR-CALC-0422</c>
    /// <c>err.ECR-CALC-0422.carryOverTooLarge</c>, а не мовчазне обрізання: прогін стає <c>Failed</c>, попередній
    /// лишається актуальним.
    /// </para>
    /// </remarks>
    /// <param name="tableInstanceIds">
    /// ✎ D-557: лише результати цих екземплярів таблиць (методологія, СПІЛЬНА із затвердженим аркушем, перерахована на
    /// інших таблицях, а затверджені лишаються зі старими числами); <c>null</c> — усі екземпляри методологій.
    /// </param>
    public Task<int> CarryOverResultsAsync(
        long calculationRunId, long documentId, IReadOnlyCollection<int> methodologyIds, CancellationToken ct,
        IReadOnlyCollection<long>? tableInstanceIds = null);

    /// <summary>
    /// ✎ B79: де числа нового прогону ВІДРІЗНЯЮТЬСЯ від актуальних — єдина підстава позначки «перераховано після
    /// подання» для результатів методологій (<see cref="Recalculation.SubmittedSheetChanges"/>).
    /// </summary>
    /// <param name="calculationRunId">Новий прогін (ще не актуальний).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="methodologyIds">
    /// Методології (не версії), які прогін ПЕРЕРАХУВАВ для цього документа. Перенесені (<see cref="CarryOverResultsAsync"/>)
    /// сюди не входять: у новому прогоні їх ще немає, і порівняння показало б їх «зниклими».
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Пари «методологія × екземпляр таблиці», де хоч одне число додалося, зникло або змінилося; порожньо — прогін
    /// нічого не змінив.
    /// </returns>
    /// <remarks>
    /// ⚠ Викликати ДО <see cref="SwitchCurrentRunAsync"/> і до переносу, у тій самій транзакції: «актуальне» — та сама
    /// вибірка прогонів, що й у <see cref="ReadCurrentAsync"/> (без самого нового прогону), а після перемикання й
    /// прибирання порівнювати вже нема з чим. Порівнюється число як його бачить читач: екземпляр таблиці, рядок, код
    /// виходу, вид, речовина, одиниця, значення. ВЕРСІЯ методології в порівняння не входить — нова редакція з тими самими
    /// числами нічого не змінила. Перший прогін документа (актуального ще немає) — зміна: числа з'явилися.
    /// </remarks>
    public Task<IReadOnlyList<ChangedResultScope>> ChangedResultsAsync(
        long calculationRunId, long documentId, IReadOnlyCollection<int> methodologyIds, CancellationToken ct);

    /// <summary>
    /// Числа <b>актуального</b> прогону для документа й періоду.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Результати в порядку рядка й коду виходу; порожньо — прогону немає.</returns>
    /// <remarks>
    /// ⛔ Читання живе в тому самому порту, що й запис, бо предмет один:
    /// <c>calc.CalculationResult</c> партиційована за періодом, а «актуальність»
    /// живе на ПРОГОНІ (<c>CalculationRun.CurrentStatus</c>), не на рядку. Другий
    /// порт мав би повторити обидва ці знання — і саме там вони й розійшлися б.
    ///
    /// ⛔ Береться саме актуальний прогін, а не останній за часом. Прогін, який
    /// упав, лишає по собі частину рядків; віддати їх означало б показати в
    /// звіті числа, половина яких порахована старою версією методології.
    ///
    /// ⚠ Без цього методу результат методології не було видно НІДЕ (<c>D-69</c>:
    /// у <c>doc.CellValue</c> він не потрапляє). Перерахунок завершувався
    /// успіхом, число лягало в базу — і єдиним способом його побачити був
    /// <c>SELECT</c>.
    /// </remarks>
    public Task<IReadOnlyList<CalculationResultRow>> ReadCurrentAsync(
        long documentId, int periodKey, CancellationToken ct);

    /// <summary>
    /// Профіль <b>актуального</b> прогону документа за період (D14): з нього читається діагностика «рядок без правила».
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період; <c>0</c> — прогін без періоду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c> — актуального прогону за цей період немає.</returns>
    /// <remarks>
    /// ⚠ Та сама вибірка прогонів, що й у <see cref="ReadCurrentAsync"/> (власний документний прогін має перевагу
    /// над проєктним), звужена до прогону саме цього періоду: числа читаються з партиції періоду, а прогони
    /// проєкту живуть по одному на період.
    /// </remarks>
    public Task<CurrentRunProfile?> ReadCurrentRunProfileAsync(long documentId, int periodKey, CancellationToken ct);

    /// <summary>
    /// Результати КОНКРЕТНОГО прогону документа за період — те, що вже записали методології цього прогону
    /// (MS, план §13.9: методологія-читач бачить результат джерела з того самого прогону).
    /// </summary>
    /// <param name="calculationRunId">Прогін.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="filter">Звуження: екземпляри таблиць, коди виходів, версії методологій.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Рядки прогону за фільтром; порожньо — прогін нічого такого не записав.</returns>
    /// <remarks>
    /// ⚠ Реалізація за замовчуванням порожня: подвійники портів у тестах, що її не знають, лишаються працездатними.
    /// </remarks>
    public Task<IReadOnlyList<CalculationResultRow>> ReadRunAsync(
        long calculationRunId, long documentId, int periodKey, CurrentResultFilter filter, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CalculationResultRow>>([]);
}

/// <summary>Прогін, що перестав бути актуальним (Q-17): що саме прибирати з <c>calc.*</c>.</summary>
/// <param name="RunId">Прогін.</param>
/// <param name="PeriodKey">Партиція його рядків (<c>0</c> — прогін без періоду).</param>
/// <param name="DocumentId">Документ прогону документа; <c>null</c> — прогін усього проєкту.</param>
public sealed record SupersededRun(long RunId, int PeriodKey, long? DocumentId);

/// <summary>✎ B79: де новий прогін змінив числа методології проти актуальних.</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="TableInstanceId">
/// Екземпляр таблиці зміненого числа (D-401); <c>null</c> — результат без екземпляра: викликач відносить зміну до всіх
/// таблиць, до яких методологія прив'язана.
/// </param>
public sealed record ChangedResultScope(int MethodologyId, long? TableInstanceId);

/// <summary>Актуальний прогін документа й збережений профіль (D14).</summary>
/// <param name="RunId">Прогін.</param>
/// <param name="FinishedAt">Коли завершився (UTC); <c>null</c> — немає.</param>
/// <param name="ModulesProfileJson">Профіль; формат — <c>ModuleProfile.ToJson</c> або масив старого формату.</param>
public sealed record CurrentRunProfile(long RunId, DateTime? FinishedAt, string? ModulesProfileJson);

/// <summary>Які результати попереднього актуального прогону перенести в новий (RC14, P2-4).</summary>
/// <param name="DocumentId">Документ прогону.</param>
/// <param name="MethodologyIds">Методології, яких прогін області не перераховував.</param>
/// <param name="TableInstanceIds">
/// ✎ D-557: звузити перенос екземплярами таблиць (затверджені аркуші, чию СПІЛЬНУ методологію прогін перерахував на
/// інших таблицях); <c>null</c> — усі результати методологій.
/// </param>
public sealed record ResultCarryOver(
    long DocumentId, IReadOnlyCollection<int> MethodologyIds, IReadOnlyCollection<long>? TableInstanceIds = null);

/// <summary>Один рядок актуального результату розрахунку.</summary>
/// <param name="MethodologyVersionId">Версія, що дала число: без неї його неможливо пояснити.</param>
/// <param name="SourceRowKey">Рядок документа; <c>null</c> — рівень таблиці.</param>
/// <param name="OutputCode">Код виходу методології.</param>
/// <param name="Value">Значення; <c>decimal</c>, ніколи <c>float</c> (<c>D-30</c>).</param>
/// <param name="UnitId">Одиниця результату — обов'язкова (ФВ-16.6).</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> — вихід без речовини.</param>
/// <param name="TableInstanceId">Екземпляр таблиці, що дав число (D-401); <c>null</c> — результат до D-401.</param>
public sealed record CalculationResultRow(
    int MethodologyVersionId,
    string? SourceRowKey,
    string OutputCode,
    decimal Value,
    int UnitId,
    long? SubstanceEntryId,
    long? TableInstanceId = null);
