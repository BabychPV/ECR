// src/Ecr.Application/Documents/MethodologyRequiredColumnsCache.cs

using System.Globalization;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Application.Documents;

/// <summary>
/// Кеш позначки «колонка обов'язкова за методологією» для зрізу таблиці
/// (<c>RD-04</c> директиви №14, частина 3, §3.4).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Що лікуємо.</b> <c>GetTableSliceHandler.RequiredByMethodologyColumnIdsAsync</c>
/// робив <b>три запити на кожну прив'язану методологію</b>
/// (<c>GetPublishedVersionsAsync</c> + <c>GetRulesAsync</c> +
/// <c>GetRequiredInputsAsync</c>) — <c>3N</c> звернень у найгарячішому читанні
/// системи, бюджет якого 1.5 с на 500×60 (<c>tz/08</c> §8.2), а виміряна
/// лінійка — 21.3 звернення при цілі ≤ 8 (<c>MS-01-BASELINE.md</c> §3.1).
/// </para>
/// <para>
/// ⚠ <b>Два рівні, бо дві різні гарантії свіжості, і плутати їх не можна.</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <see cref="RequiredColumnIdsAsync"/> — вміст <b>опублікованої</b> версії.
/// Інвалідації немає і не потрібно: опублікована версія незмінна за побудовою
/// домену (<c>MethodologyVersion.RequireDraft</c>, <c>:569-578</c>; зміна — це
/// клон і нове вікно дії, <c>ФВ-13.2</c>). Термін життя стоїть лише заради
/// пам'яті, як у <c>RegistryEntryCache</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="CurrentVersionIdsAsync"/> — <b>яка саме</b> версія чинна на
/// кінець періоду. Це НЕ незмінне: публікація нової версії
/// (<c>MethodologyVersion.Publish</c>) і виведення з обігу
/// (<c>MethodologyVersion.Deprecate</c>) змінюють відповідь, не змінивши
/// жодного кешованого вмісту. Тому тут — короткий термін життя
/// (<see cref="ResolutionLifetime"/>), а склад прив'язок
/// (<c>cfg.CalculationBinding</c>) внесено в сам ключ, тож увімкнення чи
/// зняття прив'язки видно <b>негайно</b>, без жодного очікування.
/// </description>
/// </item>
/// </list>
/// <para>
/// ⚠ <b>Стеля несвіжості — тільки для зірочки в заголовку.</b> Протягом
/// <see cref="ResolutionLifetime"/> після публікації нової версії сітка може
/// показати позначку старої. Сам gate запису
/// (<c>PatchCellsHandler.EnforceRequiredInputsAsync</c>) цього кешу не
/// торкається і читає методологію щоразу — тобто відхилити чи пропустити
/// збереження несвіжа позначка не може ніколи.
/// </para>
/// <para>
/// ⚠ <b>Чому <see cref="IMemoryCache"/> напряму, а не порт.</b> Подібні кеші
/// (<c>MetadataCache</c>, <c>RegistryEntryCache</c>, <c>AccessProfileCache</c>)
/// живуть в <c>Ecr.Infrastructure/Caching</c> за портом, і коментар
/// <c>IRegistryEntryCache.cs:12</c> називає це правилом. Тут узято іншу форму
/// свідомо: порт вимагав би рядка реєстрації в
/// <c>Ecr.Infrastructure/DependencyInjection.cs</c>, який у цій хвилі тримає
/// інший рядок роботи, а <c>IMemoryCache</c> там уже зареєстрований
/// (<c>:96</c>) і додаткової реєстрації не потребує. Архітектурного тесту на
/// цю заборону в дереві немає (перевірено <c>grep</c> по
/// <c>tests/Ecr.Architecture.Tests</c>), пакет
/// <c>Microsoft.Extensions.Caching.Memory</c> уже оголошений у
/// <c>Ecr.Application.csproj</c>. Борг названий у звіті: звести до порту, коли
/// файл реєстрації звільниться.
/// </para>
/// </remarks>
/// <param name="memory">
/// Спільне сховище кешу. <c>null</c> вимикає кешування цілком — так
/// побудований обробник поводиться рівно як до <c>RD-04</c>. Ця форма існує
/// заради тих тестів, що конструюють <c>GetTableSliceHandler</c> вручну; у
/// контейнері параметр завжди розв'язується
/// (<see cref="IsEnabled"/> це і перевіряє).
/// </param>
public sealed class MethodologyRequiredColumnsCache(IMemoryCache? memory)
{
    /// <summary>
    /// Стеля несвіжості добору чинної версії.
    /// </summary>
    /// <remarks>
    /// 30 с вибрано так, щоб накрити сплеск відкриття аркуша (до 91 таблиці
    /// підряд) і перезапит зрізу після кожного автозбереження
    /// (<c>useCellPatch.ts:127-129</c>), але не пережити навіть одного
    /// перемикання адміністратора між сторінками методології.
    /// </remarks>
    public static readonly TimeSpan ResolutionLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Стеля життя вмісту опублікованої версії.
    /// </summary>
    /// <remarks>
    /// ⚠ Не про актуальність — вміст незмінний, — а лише про пам'ять: інакше
    /// кожна версія, яку хтось раз відкрив, лишалася б у процесі назавжди.
    /// </remarks>
    public static readonly TimeSpan VersionLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Чи кешування справді ввімкнене (тобто сховище прийшло з контейнера).</summary>
    /// <remarks>
    /// ⛔ Потрібне тестові, а не коду. Кеш, який мовчки вимкнувся, виглядає як
    /// працюючий продукт і як зелений набір — і рівно так втрачається
    /// прискорення, за яке цей рядок роботи й узято.
    /// </remarks>
    public bool IsEnabled => memory is not null;

    /// <summary>
    /// Ідентифікатори версій методологій, чинних на кінець періоду для цієї
    /// таблиці.
    /// </summary>
    /// <param name="tableDefId">Таблиця шаблону.</param>
    /// <param name="periodEnd">Кінець періоду екземпляра — дата добору версії.</param>
    /// <param name="methodologyIds">Методології, активно прив'язані до таблиці.</param>
    /// <param name="resolve">Добір версій, якщо в кеші відповіді немає.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версії в порядку, який дав <paramref name="resolve"/>.</returns>
    public Task<IReadOnlyList<int>> CurrentVersionIdsAsync(
        int tableDefId,
        DateOnly periodEnd,
        IReadOnlyList<int> methodologyIds,
        Func<CancellationToken, Task<IReadOnlyList<int>>> resolve,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);

        return GetOrAddAsync(
            ResolutionKey(tableDefId, periodEnd, methodologyIds),
            resolve,
            ResolutionLifetime,
            methodologyIds.Count + 1,
            ct);
    }

    /// <summary>
    /// Колонки, обов'язкові за опублікованою версією методології; порожній
    /// набір — версія не має жодного правила прив'язки, тобто її вимоги ніколи
    /// не enforced.
    /// </summary>
    /// <param name="publishedVersionId">Опублікована версія методології.</param>
    /// <param name="load">Читання вмісту версії, якщо в кеші його немає.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>ColumnDefId</c> обов'язкових входів.</returns>
    public Task<IReadOnlySet<int>> RequiredColumnIdsAsync(
        int publishedVersionId,
        Func<CancellationToken, Task<IReadOnlySet<int>>> load,
        CancellationToken ct)
        => GetOrAddAsync(VersionKey(publishedVersionId), load, VersionLifetime, size: 1, ct);

    private async Task<T> GetOrAddAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan lifetime,
        int size,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (memory is null)
        {
            return await factory(ct).ConfigureAwait(false);
        }

        if (memory.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        var built = await factory(ct).ConfigureAwait(false);

        memory.Set(key, built, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime,
            Size = size,
        });

        return built;
    }

    /// <summary>Ключ добору версій: таблиця, дата і <b>склад прив'язок</b>.</summary>
    /// <remarks>
    /// ⛔ Ідентифікатори сортуються. <c>GetMethodologyIdsBoundToTableAsync</c>
    /// робить <c>Distinct()</c> без <c>OrderBy</c>, тобто порядок віддає СУБД;
    /// несортований ключ давав би два різні записи кешу на один і той самий
    /// стан — кеш, який іноді працює, гірший за відсутній, бо приховує себе.
    /// </remarks>
    private static string ResolutionKey(int tableDefId, DateOnly periodEnd, IReadOnlyList<int> methodologyIds)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"mrc:bind:{tableDefId}:{periodEnd:yyyyMMdd}:{string.Join('.', methodologyIds.Order())}");

    private static string VersionKey(int publishedVersionId)
        => string.Create(CultureInfo.InvariantCulture, $"mrc:cols:{publishedVersionId}");
}
