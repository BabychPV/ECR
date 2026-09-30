// src/Ecr.Infrastructure/Jobs/JobLeaseContext.cs
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Оренда задачі, яку виконує ЦЕЙ DI-scope (<see cref="IJobLeaseContext"/>, MI-02).
/// </summary>
/// <remarks>
/// ⚠ Scoped: <see cref="JobWorker"/> відкриває окремий scope на кожну задачу і
/// прив'язує до нього її оренду ДО виконання. Поза воркером (HTTP-запит,
/// Quartz) <see cref="Current"/> лишається <c>null</c> — і код, що читає
/// оренду (fencing у <c>RunCalculationHandler.CompleteAsync</c>), іде старим
/// шляхом.
/// </remarks>
public sealed class JobLeaseContext : IJobLeaseContext
{
    /// <inheritdoc />
    public JobClaimToken? Current { get; private set; }

    /// <summary>Прив'язує оренду до scope — один раз.</summary>
    /// <param name="claim">Оренда задачі, яку виконуватиме scope.</param>
    /// <exception cref="InvalidOperationException">Scope уже виконує іншу задачу.</exception>
    /// <remarks>
    /// ⛔ Повторна прив'язка — помилка, а не перезапис: один scope на дві задачі
    /// означав би спільний <c>DbContext</c>, і fencing однієї задачі перевіряв
    /// би оренду іншої.
    /// </remarks>
    public void Bind(JobClaimToken claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (Current is not null)
        {
            throw new InvalidOperationException(
                $"Scope уже виконує задачу {Current.JobId}; задача {claim.JobId} потребує власного scope.");
        }

        Current = claim;
    }
}
