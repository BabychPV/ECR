// src/Ecr.Infrastructure/Persistence/CalculationIssueRunCleanup.cs
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ P-1124: ОДНЕ місце, де проблеми розрахунку (<c>calc.CalculationIssue</c>) ідуть разом зі своїм прогоном.
/// </summary>
/// <remarks>
/// ⛔ На <c>calc.CalculationRun</c> посилається <c>FK_CIssue_Run</c> (NO ACTION; каскад заборонено ФВ-7.6): кожен шлях, що
/// видаляє рядок прогону, СПЕРШУ прибирає його проблеми цим помічником (EF-шляхи) або <see cref="DeleteForRunSql"/>
/// (сирий SQL прибирання D-560). Шлях, що забуде, падає 547 замість лишати сироту (так було до FK: 323 рядки на копії
/// live після перебудов Land і прибирання D-560/D-575).
/// <para>
/// Поточні проблеми неперерахованих таблиць до видалення старого прогону вже перейшли на новий
/// (<see cref="CalculationIssueStore.CarryOverAsync"/>), тож тут зникає лише мертве: проблеми прогону, якого вже немає
/// в актуальному ланцюгу, і таблиць, яких у документі вже немає.
/// </para>
/// </remarks>
internal static class CalculationIssueRunCleanup
{
    /// <summary>Скільки ключів в одному <c>IN</c> (стеля параметрів SQL Server — 2100).</summary>
    private const int ChunkSize = 1_000;

    /// <summary>
    /// Фрагмент сирого SQL: проблеми прогону <c>@run</c>, якщо <paramref name="runCondition"/> досі тримається (та сама
    /// умова, що й у <c>DELETE</c> рядка прогону, — атомарно з ним за змістом: актуальний прогін не чіпається).
    /// </summary>
    /// <param name="runCondition">Умова над псевдонімом <c>ru</c> (<c>calc.CalculationRun</c>).</param>
    /// <returns>Оператор <c>DELETE</c> із крапкою з комою.</returns>
    internal static string DeleteForRunSql(string runCondition) => $"""
        DELETE i FROM calc.CalculationIssue AS i
         WHERE i.CalculationRunId = @run
           AND EXISTS (SELECT 1 FROM calc.CalculationRun AS ru WHERE ru.Id = @run AND {runCondition});
        """;

    /// <summary>Видаляє проблеми цих прогонів (у транзакції викликача, одразу).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="runIds">Прогони, рядки яких викликач видалить далі.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки проблем видалено.</returns>
    internal static async Task<int> DeleteForRunsAsync(EcrDbContext db, IEnumerable<long> runIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(runIds);

        var total = 0;
        foreach (var chunk in runIds.Distinct().Chunk(ChunkSize))
        {
            total += await db.CalculationIssues
                .Where(i => chunk.Contains(i.CalculationRunId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return total;
    }
}
