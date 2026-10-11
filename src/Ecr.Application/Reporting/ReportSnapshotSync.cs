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
    /// Перераховує статус ПОТОЧНИХ неподаних зрізів проєкту за всі періоди — після зміни СКЛАДУ даних
    /// (видалення документа), а не стану одного аркуша.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ X1-02 (аудит R11). Статус зрізу успадковується від даних (D-65): «усе затверджено» рахується по
    /// СКЛАДУ документів проєкту. Видалення документа-чернетки (єдиного неподаного) прибирало з
    /// порахунку аркуші, через які зріз був <c>Draft</c>, а статус лишався старим: решта документів
    /// затверджені, а регуляторна вʼюха не бачить зрізу. Аркуш документа без рядка стану рахується
    /// <c>Draft</c> у КОЖНОМУ періоді, тож зачеплені всі слоти проєкту, а не лише періоди, де в документа
    /// були стани.
    /// <para>
    /// ⚠ Порядок як у <see cref="RefreshAsync"/>: замок слоту — ДО перерахунку (побудова бере той самий
    /// замок), слоти — за зростанням періоду (річний, <c>NULL</c>, — останнім), перелік зрізів
    /// перечитується ПІСЛЯ замків. Залишкове вікно: слот нового періоду, що зʼявився між переліком і
    /// замками, цим викликом не береться — його зріз порахує побудова за станом, що буде видно їй.
    /// Потребує відкритої транзакції (<see cref="IReportSnapshotBuilder.LockSlotAsync"/>).
    /// </para>
    /// </remarks>
    public async Task RefreshProjectAsync(int projectId, CancellationToken ct)
    {
        var slots = (await snapshots.ListAsync(projectId, null, visibleProjectIds: null, ct).ConfigureAwait(false))
            .Where(s => s.IsCurrent && s.Status != nameof(SnapshotStatus.Submitted))
            .Select(s => s.PeriodKey)
            .Distinct()
            .OrderBy(k => k ?? int.MaxValue)
            .ToList();

        if (slots.Count == 0)
        {
            return;
        }

        foreach (var slot in slots)
        {
            await snapshots.LockSlotAsync(projectId, slot, ct).ConfigureAwait(false);
        }

        var current = (await snapshots.ListAsync(projectId, null, visibleProjectIds: null, ct).ConfigureAwait(false))
            .Where(s => s.IsCurrent && s.Status != nameof(SnapshotStatus.Submitted));

        foreach (var snapshot in current)
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
    ///
    /// ⛔ R6-X7 / X7-01: ЗАСТАРІЛИЙ зріз (побудований до останнього актуального
    /// прогону) не морозиться: <c>RefreshStatusAsync</c> віддає для нього <c>Draft</c>.
    /// Інакше регулятор отримав би старі числа з позначкою «подано», а поданий зріз
    /// уже не виправити. Свіжі числа дає нова побудова — за поданим періодом вона
    /// народжується <c>Submitted</c> зі стану даних (D-65).
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

        // ⛔ R6-X1 / X1-01: замок слоту — ДО переліку. Побудова зрізу бере той
        // самий замок перед тим, як порахувати статус нового зрізу і зробити
        // його поточним. Без нього перехід посеред побудови бачив поточним
        // лише СТАРИЙ зріз, і новий ставав поточним зі статусом до переходу:
        // останній Submit не морозив його, Approve не піднімав. Тепер перехід
        // або закомітився раніше (і побудова його врахує), або чекає замка й
        // знаходить уже новий зріз.
        await snapshots.LockSlotAsync(projectId.Value, periodKey.Value, ct).ConfigureAwait(false);

        // ⚠ `visibleProjectIds: null` — і це не пропущена перевірка (Q-239).
        // Тут немає користувача, чиї гранти можна було б спитати: клас
        // проводить у зріз ЗМІНУ СТАНУ аркуша, яку право подавати/затверджувати
        // вже перевірив обробник вище. Обмежити цей виклик грантами того, хто
        // подав, означало б, що зріз проєкту лишається незамороженим просто
        // тому, що подавач бачить не весь проєкт.
        var all = await snapshots
            .ListAsync(projectId, periodKey.Value, visibleProjectIds: null, ct)
            .ConfigureAwait(false);

        return [.. all.Where(s => s.IsCurrent && s.Status != nameof(SnapshotStatus.Submitted))];
    }
}
