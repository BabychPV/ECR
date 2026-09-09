using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IOrphanScanner"/>: ставить і **знімає**
/// <c>doc.TableRow.IsOrphaned</c> (ФВ-8.13a, D-98).
/// </summary>
/// <remarks>
/// ⚠ Правило вирішує <see cref="OrphanScanPlan"/>, не цей клас. Тут — лише
/// вибірка кандидатів і запис результату. Розділення не косметичне: правило
/// перевіряється тестами без бази, а запис — це один <c>UPDATE</c> на десятки
/// тисяч рядків, який інакше довелося б перевіряти вручну на живому SQL.
/// <para>
/// Чинність запису рахується <see cref="RegistryResolver.IsSelectable"/> — тим
/// самим методом, що формує випадний список. Окрема, «майже така сама» умова
/// тут означала б, що рядок зі значенням, яке користувач щойно обрав зі
/// списку, оголошується осиротілим.
/// </para>
/// </remarks>
public sealed class OrphanScanner(EcrDbContext db, RegistryResolver resolver, IClock clock) : IOrphanScanner
{
    /// <summary>Стеля одного проходу: скільки рядків-кандидатів беремо за раз.</summary>
    /// <remarks>
    /// Обсяг <c>doc.TableRow</c> — ~108 млн рядків на рік. Без межі перший же
    /// прохід спробував би матеріалізувати їх усі й упав би не на логіці, а на
    /// пам'яті — після годин роботи.
    /// </remarks>
    private const int BatchSize = 20_000;

    /// <inheritdoc />
    public Task<int> ScanAllAsync(CancellationToken ct)
        => RunAsync(registryEntryId: null, ct);

    /// <inheritdoc />
    public Task<int> RescanForEntryAsync(long registryEntryId, CancellationToken ct)
        => RunAsync(registryEntryId, ct);

    /// <summary>Спільний прохід: <c>null</c> — усі записи, інакше один.</summary>
    private async Task<int> RunAsync(long? registryEntryId, CancellationToken ct)
    {
        // 1. Періоди, які взагалі перевіряються. Закриті не чіпаються: їхні
        //    дані вже подані, ознака нічого не розблокує і не заборонить
        //    (ФВ-8.13a).
        var periods = await db.Periods
            .AsNoTracking()
            .Where(p => p.State == PeriodState.Open || p.State == PeriodState.Grace)
            .Select(p => new { p.PeriodKeyValue, p.State, p.PeriodEnd })
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (periods.Count == 0)
        {
            return 0;
        }

        var periodKeys = periods.Select(p => p.PeriodKeyValue).ToList();

        // ⚠ Дата резолвінгу — КІНЕЦЬ періоду, а не «сьогодні». Запис, чинний
        // до 30 червня, лишається чинним для всього червневого звіту, навіть
        // якщо перевірка йде в жовтні (ФВ-8.5).
        var asOfByPeriod = periods.ToDictionary(p => p.PeriodKeyValue, p => p.PeriodEnd);
        var stateByPeriod = periods.ToDictionary(p => p.PeriodKeyValue, p => p.State);

        // 2. Кандидати: рядки, у яких є хоч одна комірка-посилання на довідник.
        var candidateQuery =
            from cell in db.CellValues.AsNoTracking()
            where cell.ValueRegistryEntryId != null
            join row in db.TableRows.AsNoTracking()
                on new { P = cell.PeriodKeyValue, I = cell.TableRowId }
                equals new { P = row.PeriodKeyValue, I = row.Id }
            where !row.IsDeleted && periodKeys.Contains(row.PeriodKeyValue)
            select new CellReference(
                row.Id, row.PeriodKeyValue, row.IsOrphaned, cell.ValueRegistryEntryId!.Value);

        if (registryEntryId is { } single)
        {
            candidateQuery = candidateQuery.Where(c => c.RegistryEntryId == single);
        }

        var references = await candidateQuery
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (references.Count == 0)
        {
            return 0;
        }

        // 3. Записи довідника, на які ці рядки посилаються.
        var entryIds = references.Select(r => r.RegistryEntryId).Distinct().ToList();
        var entries = await db.RegistryEntries
            .AsNoTracking()
            .Where(e => entryIds.Contains(e.Id))
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var entryById = entries.ToDictionary(e => e.Id);

        // 4. Рядок осиротілий, якщо ХОЧ ОДНЕ його посилання нечинне.
        //    Саме «хоч одне», а не «всі»: рядок із чинним дозволом і нечинним
        //    водним об'єктом описує викид у місце, якого вже немає.
        var byRow = references
            .GroupBy(r => (r.RowId, r.PeriodKeyValue))
            .Select(group =>
            {
                var asOf = asOfByPeriod[group.Key.PeriodKeyValue];

                var allValid = group.All(r =>
                    entryById.TryGetValue(r.RegistryEntryId, out var entry)
                    && resolver.IsSelectable(entry, asOf));

                return new OrphanCandidate(
                    group.Key.RowId,
                    stateByPeriod[group.Key.PeriodKeyValue],
                    group.First().IsOrphaned,
                    allValid);
            })
            .ToList();

        var decision = OrphanScanPlan.Plan(byRow);
        if (decision.Total == 0)
        {
            return 0;
        }

        // 5. Запис. Set-based, не рядок за рядком: рядків тут — десятки тисяч.
        //
        // ⚠ Директива №11 §4, пункт `#49` (`Q-193`): нижче навмисно НЕ
        // кличеться `TableRow.SetOrphaned` — завантаження кожного рядка через
        // EF заради одного виклику домену звело б пакетну операцію до
        // рядок-за-рядком на обсязі, для якого весь клас і писався. Обидва
        // `SetProperty`-вирази нижче тримають ТОЙ САМИЙ інваріант, що описує
        // `TableRow.SetOrphaned` («`OrphanedAt` нульується разом з ознакою») —
        // звіряй їх із визначенням там, коли міняєш один із двох.
        var now = clock.UtcNow;

        if (decision.ToFlag.Count > 0)
        {
            await db.TableRows
                .Where(r => decision.ToFlag.Contains(r.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.IsOrphaned, true)
                          .SetProperty(r => r.OrphanedAt, now),
                    ct)
                .ConfigureAwait(false);
        }

        if (decision.ToClear.Count > 0)
        {
            // ⚠ OrphanedAt зануляється разом із ознакою. Лишити його означало б
            // мати рядок, який «колись був осиротілим» і виглядає як осиротілий
            // у будь-якому звіті, що дивиться на дату, а не на прапорець.
            await db.TableRows
                .Where(r => decision.ToClear.Contains(r.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.IsOrphaned, false)
                          .SetProperty(r => r.OrphanedAt, (DateTime?)null),
                    ct)
                .ConfigureAwait(false);
        }

        return decision.Total;
    }

    /// <summary>Одне посилання рядка на запис довідника.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: анонімний розриває вираз фігурною дужкою,
    /// і архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» бачить
    /// половину інструкції без межі (`D1-08`).
    /// </remarks>
    private sealed record CellReference(
        long RowId, int PeriodKeyValue, bool IsOrphaned, long RegistryEntryId);
}
