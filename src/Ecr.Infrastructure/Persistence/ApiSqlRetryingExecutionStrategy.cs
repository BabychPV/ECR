using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// PSS-12: стратегія повторів EF для процесу Api — стандартна <see cref="SqlServerRetryingExecutionStrategy"/>,
/// але БЕЗ повторів на тайм-ауті очікування пам'яті під запит (<c>8645</c>), тайм-ауті команди (<c>-2</c>) і <see cref="TimeoutException"/>.
/// </summary>
/// <remarks>
/// ⚠ Уточнення передумови аудиту (перевірено на EF 10.0.13): сам <c>-2</c> стандартна стратегія НЕ повторює —
/// повторює <c>8645</c> (сервер не дочекався memory grant) і голий <see cref="TimeoutException"/>. GET, що
/// стояв у черзі на grant, виконувався б до 4 разів і щоразу знову просив grant — тиснув на той самий дефіцит.
/// Для інтерактивного запиту краще відразу віддати 503 (<c>Retry-After</c>, <c>ExceptionHandlingMiddleware</c>).
/// <c>-2</c> лишено в забороненому списку як захист від зміни поведінки провайдера. Deadlock (1205) та обриви
/// з'єднання повторюються як раніше. Воркер лишає стандартну стратегію (<see cref="DependencyInjection"/>).
/// </remarks>
public sealed class ApiSqlRetryingExecutionStrategy : SqlServerRetryingExecutionStrategy
{
    /// <summary>Тайм-аут команди клієнта (SqlClient).</summary>
    public const int CommandTimeoutNumber = -2;

    /// <summary>Тайм-аут очікування пам'яті під запит (memory grant).</summary>
    public const int MemoryGrantTimeoutNumber = 8645;

    /// <summary>Створює стратегію.</summary>
    /// <param name="dependencies">Залежності стратегії від EF.</param>
    /// <param name="maxRetryCount">Максимум повторів.</param>
    /// <param name="maxRetryDelay">Максимальна затримка між повторами.</param>
    public ApiSqlRetryingExecutionStrategy(
        ExecutionStrategyDependencies dependencies, int maxRetryCount, TimeSpan maxRetryDelay)
        : base(dependencies, maxRetryCount, maxRetryDelay, errorNumbersToAdd: null)
    {
    }

    /// <summary>Чи повторювати на цьому винятку (відкрито для тестів).</summary>
    /// <param name="exception">Виняток виконання.</param>
    /// <returns><c>true</c> — повторити.</returns>
    public bool Retries(Exception exception) => ShouldRetryOn(exception);

    /// <inheritdoc />
    protected override bool ShouldRetryOn(Exception exception)
    {
        if (exception is SqlException sql)
        {
            foreach (SqlError error in sql.Errors)
            {
                if (error.Number is CommandTimeoutNumber or MemoryGrantTimeoutNumber)
                {
                    return false;
                }
            }
        }

        // ⚠ Голий TimeoutException стандартна стратегія теж повторює; тут це сам тайм-аут, а не обрив мережі.
        if (exception is TimeoutException)
        {
            return false;
        }

        return base.ShouldRetryOn(exception);
    }
}
