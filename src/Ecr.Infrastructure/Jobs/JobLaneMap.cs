// src/Ecr.Infrastructure/Jobs/JobLaneMap.cs
using Ecr.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Хто виконує перерахунок (<c>Jobs:Recalculation:Executor</c>, <c>D-206</c>).</summary>
public enum RecalculationExecutor
{
    /// <summary>Сам процес Api (як досі): його воркер черги бере й лейн перерахунку.</summary>
    InProcess,

    /// <summary>Окремий пул воркерів (<c>Ecr.Worker</c>, P1): Api лейн перерахунку НЕ бере.</summary>
    Worker,
}

/// <summary>
/// Задача → лейн черги (MI-02, <c>D-206</c>) і лейни, які опитує воркер Api.
/// </summary>
/// <remarks>
/// ⛔ Одне місце на відповідність «тип задачі → лейн»: постановка
/// (<see cref="DbBackgroundJobScheduler"/>) і виконавці мусять бачити той самий
/// лейн, інакше задача стоїть <c>Queued</c> вічно — її лейн не опитує ніхто.
/// </remarks>
public static class JobLaneMap
{
    /// <summary>Ключ конфігурації виконавця перерахунку.</summary>
    public const string ExecutorKey = "Jobs:Recalculation:Executor";

    /// <summary>Лейн задачі <typeparamref name="TJob"/>.</summary>
    public static string Of<TJob>()
        where TJob : IBackgroundJob
        => Of(typeof(TJob));

    /// <summary>
    /// Лейн задачі: <see cref="IRecalculationJob"/> (маркер або реалізація) —
    /// <see cref="JobLanes.Recalc"/>, <see cref="IFormulaRecalculationJob"/> —
    /// <see cref="JobLanes.Interactive"/>, експорт/імпорт Excel (<see cref="IsExcel"/>) —
    /// <see cref="JobLanes.Excel"/>, решта — <see cref="JobLanes.Default"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Перерахунок ФОРМУЛ шаблону (<see cref="IFormulaRecalculationJob"/>) —
    /// не <see cref="IRecalculationJob"/>: окремий пул (<c>D-206</c>) — для методологій,
    /// чий прогін іде хвилинами.
    /// ⛔ P1-06 (AN-109): і не <see cref="JobLanes.Default"/>. Його ставить PATCH комірок, і людина
    /// чекає оновлених обчислених колонок на сітці; у спільній FIFO-смузі він стояв за довгими
    /// фоновими задачами (збір, синк, імпорт, знімки звітів), що займали всі місця воркера.
    /// </remarks>
    public static string Of(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);

        return typeof(IRecalculationJob).IsAssignableFrom(jobType) ? JobLanes.Recalc
            : typeof(IFormulaRecalculationJob).IsAssignableFrom(jobType) ? JobLanes.Interactive
            : IsExcel(jobType) ? JobLanes.Excel
            : JobLanes.Default;
    }

    /// <summary>
    /// Чи задача — експорт або імпорт Excel (<see cref="IExcelExportJob"/>, <see cref="IExcelImportJob"/>; AN-116).
    /// </summary>
    /// <remarks>
    /// ⛔ Одна відповідність на обидва режими: лейн <see cref="JobLanes.Excel"/> (Database) і межа
    /// <see cref="QuartzJobTypeLimiter"/> (Quartz) питають саме тут — інакше режими розійшлися б у тому,
    /// що вважати «довгою книгою».
    /// </remarks>
    public static bool IsExcel(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);

        return typeof(IExcelExportJob).IsAssignableFrom(jobType) || typeof(IExcelImportJob).IsAssignableFrom(jobType);
    }

    /// <summary>Код задачі застосування імпорту Excel у черзі (<c>JobCode</c> = повне ім'я типу, як ставить планувальник).</summary>
    public static readonly string ExcelImportJobCode = typeof(IExcelImportJob).FullName ?? nameof(IExcelImportJob);

    /// <summary>
    /// Код задачі, яку лейн бере з черги ПЕРШОЮ, попри FIFO; <c>null</c> — лейн суто FIFO.
    /// </summary>
    /// <param name="lane">Лейн.</param>
    /// <returns>Пріоритетний <c>JobCode</c> або <c>null</c>.</returns>
    /// <remarks>
    /// ⛔ AN-123 (L1-04, AUDIT-2026-10-09c). Лейн <see cref="JobLanes.Excel"/> (2 місця) спільний для
    /// експорту й застосування великого імпорту (понад 2 000 змін), а черга бере
    /// <c>ORDER BY AvailableAt, JobId</c>. Наприкінці періоду десятки експортів ставали попереду, і
    /// людина, що відправила дані, хвилинами бачила «у черзі». Застосування імпорту — запис, на
    /// який людина чекає, а експорт — читання, яке почекає. Обрано пріоритет, а не квоту на
    /// користувача: одна зміна в claim без нової відмови в API і без нового контракту.
    ///
    /// ⚠ Голодування експортів можливе лише за безперервного потоку великих імпортів — він
    /// обмежений людьми, що їх надсилають, і кожен імпорт попереду має перегляд diff.
    ///
    /// ⚠ Лише режим Database (<see cref="DbJobQueue"/>); у Quartz межу тримає
    /// <see cref="QuartzJobTypeLimiter"/>, порядок там — порядок тригерів.
    /// </remarks>
    public static string? PriorityJobCodeOf(string lane)
        => string.Equals(lane, JobLanes.Excel, StringComparison.Ordinal) ? ExcelImportJobCode : null;

    /// <summary>Лейни, які опитує воркер процесу Api.</summary>
    /// <param name="executor">Хто виконує перерахунок.</param>
    /// <remarks>
    /// ⛔ За <see cref="RecalculationExecutor.Worker"/> Api НЕ бере
    /// <see cref="JobLanes.Recalc"/>: інакше хвилинний прогін методологій знову
    /// жив би в процесі, що обслуговує HTTP, — рівно те, від чого пул і відокремлюють.
    /// </remarks>
    public static IReadOnlyList<string> ApiLanes(RecalculationExecutor executor)
        => executor == RecalculationExecutor.Worker
            ? [JobLanes.Interactive, JobLanes.Default, JobLanes.Excel]
            : JobLanes.All;

    /// <summary>Лейни з власними місцями у воркері Api (<see cref="JobWorkerOptions.ReservedLanes"/>, P1-06).</summary>
    public static IReadOnlyList<string> ApiReservedLanes { get; } = [JobLanes.Interactive];

    /// <summary>
    /// Лейни, які воркер Api бере ЛИШЕ окремим циклом з власною межею (<see cref="JobWorkerOptions.SeparateLanes"/>,
    /// AN-116): Excel не займає ні спільних місць, ні резерву перерахунку формул.
    /// </summary>
    public static IReadOnlyList<string> ApiSeparateLanes { get; } = [JobLanes.Excel];

    /// <summary>
    /// Ключ межі одночасних задач Excel (AN-116): у режимі Database — місця окремого циклу лейна
    /// <see cref="JobLanes.Excel"/>, у режимі Quartz — <see cref="QuartzJobTypeLimiter"/>.
    /// </summary>
    public const string ExcelMaxConcurrencyKey = "Jobs:Excel:MaxConcurrency";

    /// <summary>Межа одночасних задач Excel, коли ключ не задано.</summary>
    public const int DefaultExcelMaxConcurrency = 2;

    /// <summary>Межа одночасних задач Excel з конфігурації; не задано чи недійсне — <see cref="DefaultExcelMaxConcurrency"/>.</summary>
    /// <remarks>Недійсне значення (нечисло, менше за 1) старт зупиняє раніше (<c>EcrConfigurationValidation.Integers</c>).</remarks>
    public static int ReadExcelMaxConcurrency(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return int.TryParse(
                   configuration[ExcelMaxConcurrencyKey], System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var value)
               && value >= 1
            ? value
            : DefaultExcelMaxConcurrency;
    }

    /// <summary>Виконавець перерахунку з конфігурації; не задано — <see cref="RecalculationExecutor.InProcess"/>.</summary>
    /// <remarks>Недійсне значення старт зупиняє раніше (<c>EcrConfigurationValidation.Choices</c>).</remarks>
    public static RecalculationExecutor ReadExecutor(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Enum.TryParse<RecalculationExecutor>(configuration[ExecutorKey], ignoreCase: true, out var value)
            ? value
            : RecalculationExecutor.InProcess;
    }
}
