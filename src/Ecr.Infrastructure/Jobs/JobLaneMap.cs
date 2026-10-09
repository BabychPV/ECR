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
    /// <see cref="JobLanes.Interactive"/>, решта — <see cref="JobLanes.Default"/>.
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
            : JobLanes.Default;
    }

    /// <summary>Лейни, які опитує воркер процесу Api.</summary>
    /// <param name="executor">Хто виконує перерахунок.</param>
    /// <remarks>
    /// ⛔ За <see cref="RecalculationExecutor.Worker"/> Api НЕ бере
    /// <see cref="JobLanes.Recalc"/>: інакше хвилинний прогін методологій знову
    /// жив би в процесі, що обслуговує HTTP, — рівно те, від чого пул і відокремлюють.
    /// </remarks>
    public static IReadOnlyList<string> ApiLanes(RecalculationExecutor executor)
        => executor == RecalculationExecutor.Worker ? [JobLanes.Interactive, JobLanes.Default] : JobLanes.All;

    /// <summary>Лейни з власними місцями у воркері Api (<see cref="JobWorkerOptions.ReservedLanes"/>, P1-06).</summary>
    public static IReadOnlyList<string> ApiReservedLanes { get; } = [JobLanes.Interactive];

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
