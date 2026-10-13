using Ecr.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Карта «документ → відколи результати застаріли» ОДНОГО періоду (LP-45, етап 2): ті самі дані, що дає
/// <see cref="StaleResultsQuery"/>, але для ВСЬОГО періоду одним запитом і з коротким кешем. З неї беруть і позначка
/// в рядках переліку, і фільтр <c>resultsStale</c>, і лічильники <c>/documents/summary</c>.
/// </summary>
/// <remarks>
/// ⛔ Карта НЕ залежить від читача: це «які документи періоду застаріли», без проєкту, ролі й гранту. Доступ
/// накладають викликачі, і ЗАВЖДИ тим, що вже відфільтровано доступом: рядки сторінки беруться з запиту документів
/// із межею грантів, лічильники рахуються як <c>CountAsync</c> по документах, видимих читачеві. Кеш не містить нічого,
/// чого б відповідь не дала й без нього, і чужий документ не з'являється в чужій відповіді через кеш.
///
/// ⛔ Чинність запису = ВСІ чотири умови:
/// 1) TTL (<see cref="Ttl"/>, годинник <c>IClock</c>) — страховка від усього, чого не видно нижче;
/// 2) епоха процесу (<see cref="StaleCountsEpoch.ValueFor"/>): запис комірок і перемикання актуального прогону
///    піднімають її ПІСЛЯ коміту — власна правка видна одразу;
/// 3) відбиток стану прогонів періоду (кількість і суми <c>Id</c> актуальних прогонів): міжпроцесна подія —
///    прогін завершила служба Worker, документ видалено разом із прогонами — змінює його, навіть якщо епоха цього
///    процесу не рухалась;
/// 4) відбиток довідників (<c>cfg.RegistryDef.DataChangedAt</c> і ребра <c>cfg.RegistryUse</c>): правка довідника
///    чи перепублікація методології, теж з іншого процесу.
/// Відбитки — СТАН закомічених рядків (кількість, суми, <c>CHECKSUM_AGG</c>), а не «максимум часу»: будь-який
/// наступний коміт, що змінює вхід предиката, змінює й відбиток, хоч би в якому порядку закомітились писачі.
/// Відбиток і епоха читаються ДО запиту карти: запис, що закомітився між ними, лише зайвий раз скине кеш.
///
/// ⚠ ЗНАЧНА ПРОГАЛИНА, названа прямо: нові рядки <c>aud.CellChange</c>, записані ІНШИМ процесом (імпорт і збір у
/// Worker; це не <c>Recalculation</c>, яку предикат ігнорує), відбитком не видно — дешевого відбитка стану для
/// партиційованого журналу на мільйони рядків немає. Такі правки доходять за ≤ <see cref="Ttl"/>. Правки, зроблені
/// цим процесом (<c>PATCH</c> комірок), видно одразу через епоху.
///
/// ⚠ Пам'ять: один запис на (період, автор-фільтр); карта містить лише застарілі документи періоду.
/// </remarks>
internal sealed class StaleResultsMapCache(
    EcrDbContext db, IMemoryCache? cache, IClock? clock, StaleCountsEpoch? epoch)
{
    /// <summary>Стеля карти: документів із застарілими результатами в одному періоді (з великим запасом).</summary>
    private const int MaxDocuments = 200_000;

    /// <summary>Життя запису (страховка; див. <see cref="StaleResultsMapCache"/>).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);

    /// <summary>Рядок відбитка стану.</summary>
    public sealed record FingerprintRow(
        long RunCount, long RunIdSum, int RunChecksum, long RegistryCount, int RegistryChecksum, long UseCount, long UseIdSum);

    private sealed record Entry(
        IReadOnlyDictionary<long, DateTime?> Map, long Epoch, FingerprintRow Fingerprint, DateTime ExpiresAt);

    /// <summary>
    /// Карти періоду для кількох фільтрів автора за ОДНЕ читання відбитка: <c>null</c> — будь-який автор,
    /// число — лише правки цього користувача.
    /// </summary>
    /// <param name="periodKey">Період.</param>
    /// <param name="byUserIds">Фільтри автора.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<IReadOnlyDictionary<long, DateTime?>>> GetAsync(
        int periodKey, IReadOnlyList<int?> byUserIds, CancellationToken ct)
    {
        var result = new IReadOnlyDictionary<long, DateTime?>[byUserIds.Count];
        var caching = cache is not null && clock is not null;

        FingerprintRow? fingerprint = null;
        var epochValue = epoch?.ValueFor(periodKey) ?? 0;
        if (caching)
        {
            fingerprint = await FingerprintAsync(periodKey, ct).ConfigureAwait(false);
        }

        for (var i = 0; i < byUserIds.Count; i++)
        {
            var user = byUserIds[i];
            var key = $"doc-stale-map:{periodKey}:{(user is { } u ? u.ToString(System.Globalization.CultureInfo.InvariantCulture) : "*")}";

            if (caching
                && cache!.TryGetValue(key, out Entry? hit) && hit is not null
                && hit.ExpiresAt > clock!.UtcNow && hit.Epoch == epochValue && hit.Fingerprint == fingerprint)
            {
                result[i] = hit.Map;
                continue;
            }

            var map = await LoadAsync(periodKey, user, ct).ConfigureAwait(false);
            result[i] = map;

            if (caching)
            {
                cache!.Set(key, new Entry(map, epochValue, fingerprint!, clock!.UtcNow + Ttl), Ttl);
            }
        }

        return result;
    }

    /// <summary>Карта без кешу: ті самі рядки, що й <see cref="StaleResultsQuery"/>, для всього періоду.</summary>
    private async Task<IReadOnlyDictionary<long, DateTime?>> LoadAsync(int periodKey, int? byUserId, CancellationToken ct)
    {
        var rows = await StaleResultsQuery.Documents(db, periodKey, byUserId)
            .OrderBy(r => r.DocumentId)
            .Take(MaxDocuments + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Стеля - від нескінченної вибірки, а не законний обсяг: обрізана карта мовчки віддала б «не застарілі»
        // там, де вони застарілі, тож перевищення - гучна помилка, а не тиха неправда.
        if (rows.Count > MaxDocuments)
        {
            throw new InvalidOperationException(
                $"Карта застарілості періоду {periodKey} перевищила {MaxDocuments} документів; стелю треба переглянути.");
        }

        // Kind::Unspecified з datetime2 - у JSON без `Z` читався б як локальний час; журнал пише UTC (L3-01).
        return rows.ToDictionary(
            r => r.DocumentId,
            r => r.Since is { } since ? (DateTime?)DateTime.SpecifyKind(since, DateTimeKind.Utc) : null);
    }

    private async Task<FingerprintRow> FingerprintAsync(int periodKey, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<FingerprintRow>($"""
                SELECT
                    (SELECT COUNT_BIG(*) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunCount,
                    (SELECT COALESCE(SUM(CAST(Id AS bigint)), 0) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunIdSum,
                    (SELECT COALESCE(CHECKSUM_AGG(CHECKSUM(Id)), 0) FROM calc.CalculationRun WHERE Status = N'Current' AND PeriodKey = {periodKey}) AS RunChecksum,
                    (SELECT COUNT_BIG(*) FROM cfg.RegistryDef) AS RegistryCount,
                    (SELECT COALESCE(CHECKSUM_AGG(CHECKSUM(Id, DataChangedAt)), 0) FROM cfg.RegistryDef) AS RegistryChecksum,
                    (SELECT COUNT_BIG(*) FROM cfg.RegistryUse WHERE SourceKind = 1) AS UseCount,
                    (SELECT COALESCE(SUM(CAST(Id AS bigint)), 0) FROM cfg.RegistryUse WHERE SourceKind = 1) AS UseIdSum
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }
}
