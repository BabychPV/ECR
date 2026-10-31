// src/Ecr.Infrastructure/Jobs/MaterializationScheduler.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
public sealed class MaterializationScheduler(
    EcrDbContext db, IBackgroundJobScheduler jobs, IRowStore? rowStore = null) : IMaterializationScheduler
{
    /// <summary>Стеля пар «документ + період» для заведення екземплярів за один перехід.</summary>
    private const int MaxEnsured = 1000;

    /// <inheritdoc />
    public bool EnlistsInCallerTransaction => jobs.EnlistsInCallerTransaction;

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
        // воркеру стан, якого ще немає, або який відкотиться. Черга в базі —
        // виняток: рядок черги комітиться разом із переходом або ніяк.
        if (db.Database.CurrentTransaction is not null && !EnlistsInCallerTransaction)
        {
            throw new InvalidOperationException(
                "Матеріалізацію з переходу періоду ставлять лише після коміту переходу: транзакція ще відкрита.");
        }

        // ⛔ RC14B P3-2: екземпляри свіжого документа ще не створені (ліниві) - заводимо, інакше
        // добір нижче не знайде адресата.
        await MaterializationTargets
            .EnsureInstancesAsync(db, rowStore, sourceEntityId: null, projectId, periodKeys, null, null, MaxEnsured, ct)
            .ConfigureAwait(false);

        var targets = await MaterializationTargets
            .FindAsync(db, sourceEntityId: null, projectId, periodKeys, null, null, int.MaxValue, ct)
            .ConfigureAwait(false);

        // ⚠ Закритий період без сирих точок задачі не отримує: вона лише
        // записала б `SkippedPeriodClosed` — шум, а не пропуск. Open/Grace — без
        // змін: там задача пише значення.
        targets = await MaterializationTargets
            .KeepClosedWithRawPointsAsync(db, targets, ct)
            .ConfigureAwait(false);

        await MaterializationTargets.EnqueueAsync(jobs, targets, range: null, ct).ConfigureAwait(false);

        // D-464: мапінги подій документів проєкту — негайний синк (правило — SourceEventSyncTrigger).
        await SourceEventSyncTrigger.EnqueueForPeriodsAsync(db, jobs, projectId, periodKeys, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Запитів для аркуша без мапінгів збору — два легкі (проєкт документа, пошук мапінгів проєкту за період);
    /// екземпляри аркуша читаються, лише якщо мапінги є. Діапазон — увесь період (<c>range: null</c>): для річного
    /// періоду це весь рік, тож комірки наздоганяють поточний сирий шар повністю.
    /// </remarks>
    public async Task EnqueueAfterSheetReturnAsync(long documentId, int sheetDefId, int periodKey, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null && !EnlistsInCallerTransaction)
        {
            throw new InvalidOperationException(
                "Матеріалізацію після повернення аркуша ставлять лише після коміту переходу: транзакція ще відкрита.");
        }

        // D-464: мапінги подій аркуша — негайний синк (правило — SourceEventSyncTrigger).
        await SourceEventSyncTrigger.EnqueueForSheetAsync(db, jobs, documentId, sheetDefId, ct).ConfigureAwait(false);

        var projectId = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (projectId is null)
        {
            return;
        }

        var targets = (await MaterializationTargets
                .FindAsync(db, sourceEntityId: null, projectId, [periodKey], null, null, int.MaxValue, ct)
                .ConfigureAwait(false))
            .Where(t => t.DocumentId == documentId)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var instanceIds = targets.Select(t => t.TableInstanceId).Distinct().ToList();
        var ofSheet = (await db.TableInstances
                .AsNoTracking()
                .Where(i => i.PeriodKeyValue == periodKey && instanceIds.Contains(i.Id)
                            && db.TableDefs.Any(t => t.Id == i.TableDefId && t.SheetDefId == sheetDefId))
                .Select(i => i.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        targets = targets.Where(t => ofSheet.Contains(t.TableInstanceId)).ToList();
        targets = await MaterializationTargets.KeepClosedWithRawPointsAsync(db, targets, ct).ConfigureAwait(false);

        await MaterializationTargets.EnqueueAsync(jobs, targets, range: null, ct).ConfigureAwait(false);
    }
}
