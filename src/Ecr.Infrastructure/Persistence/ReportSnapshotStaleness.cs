using Ecr.Domain.Entities.Calculations;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Застарілість зрізу звітності (ФВ-10.5): ОДНЕ визначення для переліку зрізів
/// і для журналу, що пише завершення прогону.
/// </summary>
/// <remarks>
/// <para>
/// Зріз застарів, якщо ПІСЛЯ його побудови (<c>BuiltAt</c>) актуальним став
/// прогін розрахунку, що зачіпає його проєкт і період. Числа зрізу беруться
/// лише з актуальних прогонів (<c>ReportSnapshotBuilder.AggregateAsync</c>),
/// тож новий актуальний прогін — рівно та подія, після якої числа в системі
/// можуть розійтися з числами зрізу. Правка комірок на зріз не впливає, доки
/// її не перераховано.
/// </para>
/// <para>
/// ⛔ Стан ВИВОДИТЬСЯ, а не зберігається прапорцем у <c>rpt.ReportSnapshot</c>.
/// Прапорець довелося б ставити й поданому зрізу — а поданий іммутабельний
/// (ФВ-9.17, <c>ReportSnapshot.Complete</c>/<c>RefreshStatus</c>), і будь-який
/// запис у нього розмивав би саме це правило. Виведений стан не змінює ні
/// вмісту, ні суми, ні статусу, і не може розійтися з прогонами: його неможливо
/// «забути поставити» на іншому шляху перерахунку (проєкт, документ, нічний
/// розклад — усі проходять через актуальність прогону).
/// </para>
/// <para>
/// ⚠ «Прогін став актуальним» = <c>FinishedAt</c>: <c>SwitchCurrentRunAsync</c>
/// ставить його в момент перемикання, у транзакції перемикання. Потім
/// прогін може стати <c>Superseded</c> — актуальним він БУВ, і зріз, побудований
/// до того, так само застарів. Але <c>Superseded</c> буває й одразу, без
/// актуальності: старіший прогін, що завершився після новішого, — такий несе
/// причину в <c>ErrorMessage</c> (<c>SupersededByNewerKey</c>) і чисел
/// звітові не давав ніколи, тож зрізу не старить.
/// </para>
/// <para>
/// ⚠ Межа точності: зріз, чия побудова почалася між <c>FinishedAt</c> і
/// комітом перемикання (мілісекунди однієї транзакції), бачить ще старі числа
/// і застарілим не вважається. Вікно — довжина транзакції перемикання, а не
/// прогону.
/// </para>
/// </remarks>
internal static class ReportSnapshotStaleness
{
    /// <summary>Ідентифікатори застарілих зрізів — підзапит, не матеріалізований.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="exceptRunId">
    /// Прогін, який НЕ враховувати; <c>null</c> — усі. Потрібен журналові:
    /// «застарів саме через цей прогін» = застарілий із ним і не застарілий без нього.
    /// </param>
    /// <returns>Запит, придатний до <c>Contains</c> у іншому запиті.</returns>
    public static IQueryable<long> StaleSnapshotIds(EcrDbContext db, long? exceptRunId = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var runs = db.CalculationRuns.Where(r =>
            r.FinishedAt != null
            && (r.Status == CalculationRun.CurrentStatus
                || (r.Status == CalculationRun.SupersededStatus && r.ErrorMessage == null))
            && (exceptRunId == null || r.Id != exceptRunId));

        return db.ReportSnapshots
            .Where(s => runs.Any(r =>
                r.ProjectId == s.ProjectId

                // ⚠ Період зрізу `null` — увесь рік: його старить прогін БУДЬ-ЯКОГО
                // періоду. Прогін `null` — теж увесь рік: він старить зріз будь-якого.
                && (s.PeriodKey == null || r.PeriodKey == null || r.PeriodKey == s.PeriodKey)
                && r.FinishedAt > s.BuiltAt))
            .Select(s => s.Id);
    }
}
