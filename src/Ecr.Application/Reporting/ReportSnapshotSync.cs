// src/Ecr.Application/Reporting/ReportSnapshotSync.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Reporting;

/// <summary>
/// Проводить зміну стану аркушів у зрізи звітності (<c>H-23b</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього класу <c>IReportSnapshotBuilder.MarkSubmittedAsync</c> і
/// <c>RefreshStatusAsync</c> не кликав ніхто. Наслідок був не «застарілий
/// статус у переліку»: на <c>IsSubmitted</c> тримається головна гарантія
/// <c>ER-C-11</c> — <b>поданий зріз не перераховується взагалі</b> (ФВ-9.17).
/// Поки поданим його не позначав ніхто, гарантія була написана, і при цьому не
/// діяла жодного разу.
///
/// ⚠ Окремий клас, а не по копії в кожному обробнику. Подання і затвердження
/// відповідають на одне питання — «що тепер зі зрізом» — і дві відповіді на
/// нього розійшлися б мовчки: одна половина робочого процесу морозила б зріз,
/// друга ні.
///
/// ⚠ Зрізи проєкту може бути ще не побудовано, і це нормальний стан: тоді тут
/// не робиться нічого. Побудова — окремий сценарій за розкладом або за
/// командою (<c>BuildReportSnapshotHandler</c>).
/// </remarks>
public sealed class ReportSnapshotSync(IReportSnapshotBuilder snapshots, IDocumentStore documents)
{
    /// <summary>
    /// Перераховує статус поточних зрізів документа за період.
    /// </summary>
    /// <param name="documentId">Документ, стан аркуша якого змінився.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Кличеться після ЗАТВЕРДЖЕННЯ і відхилення. Статус зрізу
    /// успадковується від даних (<c>D-65</c>), і без перерахунку регуляторна
    /// вʼюха назавжди тримала б стан, який був на момент побудови: аркуші
    /// затвердили, а звіт лишився чернетковим.
    /// </remarks>
    public async Task RefreshAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        foreach (var snapshot in await CurrentAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            await snapshots.RefreshStatusAsync(snapshot.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Морозить поточні зрізи, якщо звітність за період уже подано повністю.
    /// </summary>
    /// <param name="documentId">Документ, аркуш якого щойно подали.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="userId">Хто подав.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Умова замороження — не «подали цей аркуш», а «в періоді не лишилося
    /// неподаних». Її рахує сам <c>IReportSnapshotBuilder</c> зі станів
    /// <c>wf.ApprovalState</c> і віддає з <c>RefreshStatusAsync</c>; питати
    /// про те саме окремим запитом означало б завести друге визначення
    /// «звітність подано» — і воно розійшлося б із тим, за яким будується зріз.
    ///
    /// ⛔ Заморожування НЕ відкочується разом із поверненням аркуша в роботу.
    /// Це навмисно: подана форма вже пішла регуляторові, і зріз лишається
    /// доказом того, що саме він бачив. Повторне подання дає НОВИЙ зріз
    /// (ФВ-9.17).
    /// </remarks>
    public async Task MarkSubmittedAsync(
        long documentId, PeriodKey periodKey, int userId, CancellationToken ct)
    {
        foreach (var snapshot in await CurrentAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            var status = await snapshots.RefreshStatusAsync(snapshot.Id, ct).ConfigureAwait(false);

            if (status is SnapshotStatus.Submitted or SnapshotStatus.Approved)
            {
                await snapshots.MarkSubmittedAsync(snapshot.Id, userId, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Поточні, ще не подані зрізи проєкту за період.</summary>
    /// <remarks>
    /// ⚠ Лише <c>IsCurrent</c>: попередні зрізи навмисно лишаються такими, як
    /// були (<c>Supersede</c>), і чіпати їх означало б переписувати історію
    /// заднім числом.
    ///
    /// ⚠ Уже подані відсіюються ТУТ, а не в кожного викликача: інакше кожна
    /// зміна стану аркуша била б у доменну відмову «зріз іммутабельний» —
    /// тобто нормальний хід подій виглядав би як помилка.
    /// </remarks>
    private async Task<IReadOnlyList<ReportSnapshotSummary>> CurrentAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var projectId = await documents.FindProjectIdAsync(documentId, ct).ConfigureAwait(false);

        if (projectId is null)
        {
            return [];
        }

        var all = await snapshots
            .ListAsync(projectId, periodKey.Value, ct)
            .ConfigureAwait(false);

        return [.. all.Where(s => s.IsCurrent && s.Status != nameof(SnapshotStatus.Submitted))];
    }
}
