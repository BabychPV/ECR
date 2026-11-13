using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Кеш числової частини зведення «Calculation results — усі місяці року» (CRY03): ключі чисел, агрегат виходів із
/// маскою місяців і числа без екземпляра — СИРІ факти документа, до будь-якого звуження доступу.
/// </summary>
/// <remarks>
/// ⛔ Ключ запису — документ, періоди, актуальні прогони кожного періоду і відбиток екземплярів документа
/// (Id, TableDefId, SheetCopyId) — див. <c>CalculationResultStore.YearResultsKey</c>. Числа прогону після того, як він
/// став актуальним, не змінюються (нові числа — новий прогін, новий Id), а з'єднання з екземплярами (таблиця, копія
/// аркуша) змінює лише перенесення версії чи склад аркушів — і те, і те міняє ключ. Тому записів не інвалідує ніхто:
/// застарілий ключ просто більше не питають, і запис доживає ковзний строк. Ціна запису 0: шлях запису не знає про кеш.
/// <para>
/// ⛔ Доступ: у кеші лише факти ДО звуження; права перевіряє обробник ДО читання, звужує будівник ПІСЛЯ — для кожного
/// читача окремо. Звужений вигляд у кеш не потрапляє ніколи.
/// </para>
/// <para>
/// ⚠ Власне сховище, а не спільний <see cref="IMemoryCache"/> застосунку: у спільного немає <c>SizeLimit</c> (пояснення
/// біля <c>AddMemoryCache</c> у <c>DependencyInjection.cs</c>), а тут стеля потрібна — запис великого документа важить
/// мегабайти. Розмір запису — число рядків фактів у ньому.
/// </para>
/// </remarks>
public sealed class CalculationYearResultsCache : IDisposable
{
    /// <summary>
    /// Стеля: 500 тис. рядків фактів (~75 МБ за ~150 Б на рядок) — ~20 документів розміру синтетики CRY (≈25 тис. рядків
    /// на запис) або всі 94 документи живої бази (≈7 тис. рядків на документ-рік у середньому).
    /// </summary>
    public const long SizeLimit = 500_000;

    /// <summary>Ковзний строк запису: екран року відкривають серіями, рідше — запис іде сам.</summary>
    public static readonly TimeSpan Sliding = TimeSpan.FromMinutes(10);

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = SizeLimit });

    /// <summary>Запис за ключем або <c>null</c>.</summary>
    internal CalculationResultStore.YearResultsEntry? Get(string key) => cache.TryGetValue(key, out CalculationResultStore.YearResultsEntry? entry) ? entry : null;

    /// <summary>Кладе запис; завеликий для стелі — не кладе.</summary>
    internal void Set(string key, CalculationResultStore.YearResultsEntry entry)
    {
        var size = Math.Max(1, entry.Size);
        if (size > SizeLimit)
        {
            return;
        }

        cache.Set(key, entry, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = Sliding });
    }

    /// <inheritdoc />
    public void Dispose() => cache.Dispose();
}
