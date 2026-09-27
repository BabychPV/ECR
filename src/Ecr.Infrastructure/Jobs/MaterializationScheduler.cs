// src/Ecr.Infrastructure/Jobs/MaterializationScheduler.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Постановка матеріалізації з місця переходу періоду
/// (<see cref="PeriodMaterializationTrigger"/>).
/// </summary>
/// <remarks>
/// ⚠ Адресатів добирає той самий <see cref="MaterializationTargets"/>, що й
/// збір: різниця лише у фільтрі — «ці періоди проєкту» замість «ця сутність і
/// вікно збору».
/// <para>
/// ⚠ Без стелі <see cref="CollectionJob"/>: там стеля відсікає наслідок
/// помилки мапінгу на КОЖНОМУ прогоні, а відкриття періоду буває раз, і
/// відсічений тут адресат лишився б сирим — рівно той дефект, який цей шлях
/// закриває.
/// </para>
/// </remarks>
public sealed class MaterializationScheduler(EcrDbContext db, IBackgroundJobScheduler jobs) : IMaterializationScheduler
{
    /// <inheritdoc />
    public async Task EnqueueAfterTransitionAsync(
        int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);

        if (periodKeys.Count == 0)
        {
            return;
        }

        // ⛔ Лише ПІСЛЯ коміту переходу (див. порт). Відкрита транзакція тут —
        // помилка викликача, і мовчки поставити задачу означало б віддати
        // воркеру стан, якого ще немає, або який відкотиться.
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Матеріалізацію з переходу періоду ставлять лише після коміту переходу: транзакція ще відкрита.");
        }

        var targets = await MaterializationTargets
            .FindAsync(db, sourceEntityId: null, projectId, periodKeys, null, null, int.MaxValue, ct)
            .ConfigureAwait(false);

        await MaterializationTargets.EnqueueAsync(jobs, targets, range: null, ct).ConfigureAwait(false);
    }
}
