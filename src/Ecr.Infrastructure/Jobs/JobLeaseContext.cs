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
    private readonly List<Func<CancellationToken, Task>> afterSettle = [];

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

    /// <inheritdoc />
    /// <remarks>Лише для задачі воркера (оренду прив'язано): <see cref="JobWorker"/> виконує роботу після фіксації рядка.</remarks>
    public bool TryRunAfterSettle(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Current is null)
        {
            return false;
        }

        lock (afterSettle)
        {
            afterSettle.Add(work);
        }

        return true;
    }

    /// <summary>Забирає доручену роботу (<see cref="TryRunAfterSettle"/>) — один раз.</summary>
    internal IReadOnlyList<Func<CancellationToken, Task>> TakeAfterSettle()
    {
        lock (afterSettle)
        {
            var work = afterSettle.ToArray();
            afterSettle.Clear();
            return work;
        }
    }
}
