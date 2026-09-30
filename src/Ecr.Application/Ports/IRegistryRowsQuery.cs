// src/Ecr.Application/Ports/IRegistryRowsQuery.cs
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання рядків довідника зі значеннями для редактора даних (RT-13, FEATURE-REGISTRY-TABLES §7.1)
/// — поточний стан або <c>FOR SYSTEM_TIME AS OF</c> момент (<c>D-158</c>).
/// </summary>
/// <remarks>
/// ⚠ Порт лише читає; відбір видимих записів робить обробник правилом <c>RegistryResolver</c> —
/// тим самим, що в пікері й у знімку для формул. Друге формулювання видимості в SQL розійшлося б
/// із ними на межі дня або на частині невидимого батька.
/// <para>
/// Усі методи з тим самим <c>asOfUtc</c> читають один момент: <c>null</c> — поточні таблиці.
/// </para>
/// </remarks>
public interface IRegistryRowsQuery
{
    /// <summary>Усі записи довідника станом на момент, без фільтра видимості.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(int registryDefId, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>Значення переліку полів у всіх записах — для фільтрів і ланцюжка композиції.</summary>
    /// <param name="registryFieldDefIds">Поля; порожній набір — порожній результат.</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryRowValue>> ListFieldValuesAsync(
        IReadOnlyCollection<int> registryFieldDefIds, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>
    /// Значення, версії й записи-цілі <c>Lookup</c> для сторінки — сталою кількістю запитів.
    /// </summary>
    /// <param name="registryEntryIds">Записи сторінки (≤ 500).</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RegistryRowsSlice> ReadRowsAsync(
        IReadOnlyCollection<long> registryEntryIds, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>
    /// Усі системні версії запису та його значень (<c>FOR SYSTEM_TIME ALL</c>, RT-15) — сировина
    /// історії запису; порівняння версій робить обробник.
    /// </summary>
    /// <param name="registryEntryId">Запис; видалений логічно теж.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Версії нульової тривалості (<c>PeriodStart = PeriodEnd</c>: кілька змін рядка в одній
    /// транзакції) відкинуто — їхнього стану ніхто ніколи не бачив.
    /// </remarks>
    public Task<RegistryEntryHistorySlice> ReadEntryHistoryAsync(long registryEntryId, CancellationToken ct);

    /// <summary>
    /// Сторінка видимих записів ПЛОСКОГО довідника (без композиції) за <c>Id</c>: відбір, порядок і
    /// пагінація — у SQL (P1-4), а не над усім довідником у пам'яті.
    /// </summary>
    /// <param name="filter">Правило видимості й звуження.</param>
    /// <param name="after">Курсор: лише <c>Id &gt; after</c>.</param>
    /// <param name="take">Скільки записів узяти.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Видимість у SQL — те саме правило, що <c>RegistryResolver.Select</c> (чинність
    /// <c>[ValidFrom, ValidTo)</c>, активний, не видалений, каскад); рівність двох формулювань
    /// тримає тест <c>RegistryRowsSqlParityTests</c> на межах вікна.
    /// </remarks>
    public Task<VisibleEntryPage> PageVisibleEntriesAsync(
        VisibleEntriesFilter filter, long after, int take, CancellationToken ct);

    /// <summary>Видимі записи плоского довідника в стислому вигляді (без значень), за <c>Id</c>.</summary>
    /// <param name="filter">Правило видимості й звуження.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryEntrySlim>> ListVisibleSlimAsync(VisibleEntriesFilter filter, CancellationToken ct);

    /// <summary>
    /// Значення рядкових полів, що ВМІЩУЮТЬ підрядок (без урахування регістру й діакритики) — НАДМНОЖИНА
    /// для пошуку: точну перевірку робить обробник.
    /// </summary>
    /// <param name="registryFieldDefIds">Рядкові поля.</param>
    /// <param name="contains">Підрядок.</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryRowValue>> ListTextMatchesAsync(
        IReadOnlyCollection<int> registryFieldDefIds, string contains, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>
    /// Значення поля, рівні каноничному значенню фільтра, — НАДМНОЖИНА (рядки порівнюються без регістру
    /// й діакритики): точну перевірку робить обробник.
    /// </summary>
    /// <param name="registryFieldDefId">Поле.</param>
    /// <param name="type">Тип поля.</param>
    /// <param name="canonical">Значення в каноничному поданні (<c>Canonical</c> обробника).</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryRowValue>> ListEqualMatchesAsync(
        int registryFieldDefId, Domain.Enums.CellDataType type, string canonical, DateTime? asOfUtc, CancellationToken ct);
}

/// <summary>Правило відбору видимих записів плоского довідника.</summary>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="AsOf">Бізнес-дата чинності.</param>
/// <param name="AsOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
/// <param name="CascadeParentId">Обраний батько каскаду; <c>null</c> — без звуження.</param>
/// <param name="CascadeAllowed">Дозволені батьком записи (лише разом із <paramref name="CascadeParentId"/>).</param>
/// <param name="EntryIds">Лише ці записи; <c>null</c> — усі.</param>
public sealed record VisibleEntriesFilter(
    int RegistryDefId,
    DateOnly AsOf,
    DateTime? AsOfUtc,
    long? CascadeParentId,
    IReadOnlyCollection<long> CascadeAllowed,
    IReadOnlyCollection<long>? EntryIds);

/// <summary>Сторінка видимих записів і їх загальна кількість (без урахування курсора).</summary>
/// <param name="Items">Записи сторінки за <c>Id</c>.</param>
/// <param name="Total">Скільки всього записів проходить фільтр.</param>
public sealed record VisibleEntryPage(IReadOnlyList<RegistryEntry> Items, int Total);

/// <summary>Запис без значень — для пошуку за назвою.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="DisplayL10n">Назва мовами каталогу.</param>
public sealed record RegistryEntrySlim(long Id, string Code, Domain.ValueObjects.LocalizedText DisplayL10n);

/// <summary>Значення одного поля запису — типізовані колонки <c>dic.RegistryValue</c>.</summary>
/// <param name="RegistryEntryId">Запис.</param>
/// <param name="RegistryFieldDefId">Поле.</param>
/// <param name="Numeric">Число (<c>decimal(34,16)</c>, D-30).</param>
/// <param name="Text">Рядок.</param>
/// <param name="Date">Дата.</param>
/// <param name="Bool">Логічне.</param>
/// <param name="RefEntryId">Ціль <c>Lookup</c>.</param>
/// <param name="UnitId">Одиниця числа або значення поля типу <c>Unit</c>.</param>
/// <param name="UnitCode">Код одиниці.</param>
public sealed record RegistryRowValue(
    long RegistryEntryId,
    int RegistryFieldDefId,
    decimal? Numeric,
    string? Text,
    DateTime? Date,
    bool? Bool,
    long? RefEntryId,
    int? UnitId,
    string? UnitCode);

/// <summary>Дані сторінки рядків.</summary>
/// <param name="Values">Значення полів записів сторінки.</param>
/// <param name="Versions">
/// Запис → найпізніший початок системного періоду запису та його значень (<c>PeriodStart</c>) —
/// жетон конкуренції рядка (<c>D-166</c>).
/// </param>
/// <param name="Referenced">Записи, на які посилаються <c>Lookup</c>-значення сторінки.</param>
public sealed record RegistryRowsSlice(
    IReadOnlyList<RegistryRowValue> Values,
    IReadOnlyDictionary<long, DateTime> Versions,
    IReadOnlyList<RegistryEntry> Referenced);

/// <summary>Системна версія рядка запису (<c>dic.RegistryEntry</c> + <c>dic.RegistryEntryHistory</c>).</summary>
/// <param name="FromUtc">Початок версії (<c>PeriodStart</c>) — момент зміни.</param>
/// <param name="ToUtc">Кінець версії (<c>PeriodEnd</c>); чинна версія — <c>9999-12-31</c>.</param>
/// <param name="ChangedByUserId">Автор версії (<c>D-158</c>); <c>null</c> — невідомий.</param>
/// <param name="Display">Назва мовами каталогу.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день.</param>
/// <param name="IsActive">Активний.</param>
/// <param name="IsDeleted">Видалений логічно.</param>
public sealed record RegistryEntryVersion(
    DateTime FromUtc,
    DateTime ToUtc,
    int? ChangedByUserId,
    Domain.ValueObjects.LocalizedText Display,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    bool IsDeleted);

/// <summary>Системна версія значення поля (<c>dic.RegistryValue</c> + <c>dic.RegistryValueHistory</c>).</summary>
/// <param name="ValueId">Рядок значення: видалене й знову додане значення — інший рядок.</param>
/// <param name="FromUtc">Початок версії.</param>
/// <param name="ToUtc">Кінець версії; чинна — <c>9999-12-31</c>.</param>
/// <param name="ChangedByUserId">Автор версії; <c>null</c> — невідомий.</param>
/// <param name="Value">Значення в типізованих колонках.</param>
public sealed record RegistryValueVersion(
    long ValueId,
    DateTime FromUtc,
    DateTime ToUtc,
    int? ChangedByUserId,
    RegistryRowValue Value);

/// <summary>Сировина історії одного запису.</summary>
/// <param name="Entry">Версії рядка запису в порядку часу.</param>
/// <param name="Values">Версії значень запису в порядку часу.</param>
/// <param name="UserNames">Автор → відображуване ім'я (<c>sec.User.DisplayName</c>, не логін — R-A2).</param>
/// <param name="Referenced">Поточні записи-цілі <c>Lookup</c>-значень усіх версій.</param>
public sealed record RegistryEntryHistorySlice(
    IReadOnlyList<RegistryEntryVersion> Entry,
    IReadOnlyList<RegistryValueVersion> Values,
    IReadOnlyDictionary<int, string> UserNames,
    IReadOnlyList<RegistryEntry> Referenced);
