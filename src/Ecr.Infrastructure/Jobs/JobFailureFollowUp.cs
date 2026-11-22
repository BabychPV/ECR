// src/Ecr.Infrastructure/Jobs/JobFailureFollowUp.cs
namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ PERF-06 (<c>D-566</c>, огляд F1): робота, яку задача, що остаточно провалилась, передає НОВІЙ задачі на ту саму ціль —
/// решта місяців ланцюжка каскаду, крок якого впав. Задача кладе її в <see cref="Exception.Data"/> винятку, що летить із
/// неї (тип винятку не змінюється — класифікацію ретраю <see cref="JobRetryPolicy"/> це не зачіпає); виконавець черги
/// (<c>JobWorker</c>) ставить її лише на ОСТАТОЧНОМУ провалі (без ретраю), до <c>Failed</c>, і називає в помилці рядка місяць
/// провалу й нову задачу.
/// </summary>
/// <remarks>
/// ⚠ Чому не «пропустити місяць і йти далі всередині задачі»: задача не знає, чи провал остаточний (лічильник спроб — у
/// виконавця), а транзієнтний збій повторюється ТИМ САМИМ кроком; пропуск на першій спробі губив би місяць, що пройшов би з
/// повтору. Чому не «нова задача з кожного провалу»: повтор кроку поглинала б нова задача позаду з новим лічильником спроб —
/// стійкий «транзієнтний» збій ішов би по колу без стелі.
/// </remarks>
internal static class JobFailureFollowUp
{
    private const string PayloadKey = "ecr.jobFollowUp.payload";

    private const string FailedPeriodKey = "ecr.jobFollowUp.failedPeriod";

    private const string PeriodsKey = "ecr.jobFollowUp.periods";

    /// <summary>Кладе в <paramref name="failure"/> payload нової задачі та місяць провалу.</summary>
    /// <param name="failure">Виняток, з яким задача провалюється (летить далі як є).</param>
    /// <param name="payloadJson">Payload нової задачі на ту саму ціль.</param>
    /// <param name="failedPeriod">Період кроку, що впав.</param>
    /// <param name="periods">Періоди, які рахуватиме нова задача (для помилки рядка).</param>
    public static void Attach(Exception failure, string payloadJson, int failedPeriod, IReadOnlyList<int> periods)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentNullException.ThrowIfNull(periods);

        failure.Data[PayloadKey] = payloadJson;
        failure.Data[FailedPeriodKey] = failedPeriod;
        failure.Data[PeriodsKey] = periods.ToArray();
    }

    /// <summary>Робота для нової задачі, якщо задача її поклала.</summary>
    /// <param name="failure">Провал задачі.</param>
    /// <param name="followUp">Робота; <c>null</c> — немає.</param>
    public static bool TryGet(Exception failure, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FollowUp? followUp)
    {
        ArgumentNullException.ThrowIfNull(failure);

        followUp = failure.Data[PayloadKey] is string payload
                   && failure.Data[FailedPeriodKey] is int failedPeriod
                   && failure.Data[PeriodsKey] is int[] periods
            ? new FollowUp(payload, failedPeriod, periods)
            : null;
        return followUp is not null;
    }

    /// <summary>Текст помилки рядка, що провалився: місяць провалу, хто рахує решту, і причина.</summary>
    /// <param name="followUp">Робота.</param>
    /// <param name="followUpJobId">Нова задача; <c>null</c> — поставити не вдалося.</param>
    /// <param name="reason">Текст провалу (<see cref="JobRetryPolicy.FailureText"/>).</param>
    public static string Reason(FollowUp followUp, string? followUpJobId, string reason)
    {
        ArgumentNullException.ThrowIfNull(followUp);

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var periods = string.Join(",", followUp.Periods.Select(p => p.ToString(invariant)));
        return followUpJobId is null
            ? string.Create(invariant, $"Step for period {followUp.FailedPeriod} failed; periods {periods} could not be queued again. {reason}")
            : string.Create(invariant, $"Step for period {followUp.FailedPeriod} failed; periods {periods} continue in job {followUpJobId}. {reason}");
    }

    /// <summary>Робота нової задачі.</summary>
    /// <param name="PayloadJson">Payload.</param>
    /// <param name="FailedPeriod">Період кроку, що впав.</param>
    /// <param name="Periods">Періоди нової задачі.</param>
    internal sealed record FollowUp(string PayloadJson, int FailedPeriod, int[] Periods);
}
