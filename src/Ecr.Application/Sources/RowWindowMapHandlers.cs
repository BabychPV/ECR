// src/Ecr.Application/Sources/RowWindowMapHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>Джерело прив'язки «значення селектора → атрибут» у відповіді.</summary>
/// <param name="Id">Ідентифікатор <c>ext.RowWindowSource</c>.</param>
/// <param name="SelectorValue">Значення колонки-селектора; <c>null</c> — для всіх рядків.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourceField">Шлях атрибута в джерелі.</param>
/// <param name="SourceUnitId">Одиниця, у якій віддає значення джерело.</param>
public sealed record RowWindowSourceDto(int Id, string? SelectorValue, int SourceEntityId, string SourceField, int SourceUnitId);

/// <summary>Прив'язка «атрибут PI → колонка, вікно = рядок» у відповіді (HSE301 §4.4).</summary>
/// <param name="Id">Ідентифікатор <c>ext.RowWindowMap</c>.</param>
/// <param name="TableDefId">Таблиця прив'язки.</param>
/// <param name="TargetColumnDefId">Колонка-ціль (<c>Decimal</c>).</param>
/// <param name="TargetColumnCode">Код колонки-цілі.</param>
/// <param name="StartColumnDefId">Колонка початку вікна (<c>Date</c>).</param>
/// <param name="StartColumnCode">Код колонки початку.</param>
/// <param name="EndColumnDefId">Колонка кінця вікна (<c>Date</c>).</param>
/// <param name="EndColumnCode">Код колонки кінця.</param>
/// <param name="SelectorColumnDefId">Колонка-селектор; <c>null</c> — один атрибут на всі рядки.</param>
/// <param name="SelectorColumnCode">Код колонки-селектора.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий.</param>
/// <param name="MaxGapSeconds">Поріг прогалини, секунди; <c>null</c> — порога немає.</param>
/// <param name="TargetUnitId">Одиниця, у якій значення лягає в колонку.</param>
/// <param name="MinPercentGood">Покриття 0–100, нижче якого — <c>Partial</c>.</param>
/// <param name="RefetchWithinDays">Діб повтору за пізніми даними.</param>
/// <param name="IsActive">Чи діє прив'язка (пауза — <c>false</c>).</param>
/// <param name="RowVersion">Версія для оптимістичного блокування (hex).</param>
/// <param name="Sources">Джерела.</param>
public sealed record RowWindowMapDto(
    int Id,
    int TableDefId,
    int TargetColumnDefId,
    string TargetColumnCode,
    int StartColumnDefId,
    string StartColumnCode,
    int EndColumnDefId,
    string EndColumnCode,
    int? SelectorColumnDefId,
    string? SelectorColumnCode,
    RowWindowSummaryKind Summary,
    bool IsStep,
    int? MaxGapSeconds,
    int TargetUnitId,
    decimal MinPercentGood,
    int RefetchWithinDays,
    bool IsActive,
    string RowVersion,
    IReadOnlyList<RowWindowSourceDto> Sources);

/// <summary>Джерело у запиті на збереження прив'язки.</summary>
/// <param name="SelectorValue">Значення колонки-селектора; <c>null</c> чи порожнє — для всіх рядків.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourceField">Шлях атрибута в джерелі.</param>
/// <param name="SourceUnitId">Одиниця джерела.</param>
public sealed record RowWindowSourceInput(string? SelectorValue, int SourceEntityId, string SourceField, int SourceUnitId);

/// <summary>Створення прив'язки.</summary>
/// <param name="TableDefId">Таблиця прив'язки; збігається з таблицею колонки-цілі.</param>
/// <param name="TargetColumnDefId">Колонка-ціль (<c>Decimal</c>); одна прив'язка на колонку.</param>
/// <param name="StartColumnDefId">Колонка початку вікна (<c>Date</c>).</param>
/// <param name="EndColumnDefId">Колонка кінця вікна (<c>Date</c>).</param>
/// <param name="SelectorColumnDefId">Колонка-селектор; <c>null</c> — один атрибут на всі рядки.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий.</param>
/// <param name="TargetUnitId">Одиниця колонки-цілі.</param>
/// <param name="MinPercentGood">Покриття 0–100; <c>null</c> — типове 95.</param>
/// <param name="RefetchWithinDays">Діб повтору 0–366; <c>null</c> — типове 7.</param>
/// <param name="MaxGapSeconds">Поріг прогалини, секунди; <c>null</c> — порога немає.</param>
/// <param name="Sources">Джерела; порожньо — прив'язка без джерел (рядки матимуть <c>NotApplicable</c>).</param>
public sealed record CreateRowWindowMapCommand(
    int TableDefId,
    int TargetColumnDefId,
    int StartColumnDefId,
    int EndColumnDefId,
    int? SelectorColumnDefId,
    RowWindowSummaryKind Summary,
    bool IsStep,
    int TargetUnitId,
    decimal? MinPercentGood,
    int? RefetchWithinDays,
    int? MaxGapSeconds,
    IReadOnlyList<RowWindowSourceInput>? Sources);

/// <summary>
/// Повна заміна налаштувань прив'язки; таблиця й колонка-ціль — її ключ і не змінюються.
/// </summary>
/// <param name="StartColumnDefId">Колонка початку вікна.</param>
/// <param name="EndColumnDefId">Колонка кінця вікна.</param>
/// <param name="SelectorColumnDefId">Колонка-селектор.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий.</param>
/// <param name="TargetUnitId">Одиниця колонки-цілі.</param>
/// <param name="MinPercentGood">Покриття 0–100; <c>null</c> — типове 95.</param>
/// <param name="RefetchWithinDays">Діб повтору 0–366; <c>null</c> — типове 7.</param>
/// <param name="MaxGapSeconds">Поріг прогалини, секунди.</param>
/// <param name="IsActive">Чи діє прив'язка (пауза — <c>false</c>).</param>
/// <param name="RowVersion">Версія, яку бачив клієнт; інша — <c>409</c>; <c>null</c> — без перевірки.</param>
/// <param name="Sources">Нові джерела замість усіх наявних.</param>
public sealed record UpdateRowWindowMapCommand(
    int StartColumnDefId,
    int EndColumnDefId,
    int? SelectorColumnDefId,
    RowWindowSummaryKind Summary,
    bool IsStep,
    int TargetUnitId,
    decimal? MinPercentGood,
    int? RefetchWithinDays,
    int? MaxGapSeconds,
    bool IsActive,
    string? RowVersion,
    IReadOnlyList<RowWindowSourceInput>? Sources);

/// <summary>Спільні кроки обробників прив'язок вікна рядка (HSE301 A1).</summary>
internal static class RowWindowMapSupport
{
    /// <summary>DTO прив'язки; коди колонок беруться зі <paramref name="columns"/>.</summary>
    public static RowWindowMapDto ToDto(RowWindowMap map, IReadOnlyDictionary<int, ColumnDef> columns)
    {
        string CodeOf(int id) => columns.TryGetValue(id, out var column) ? column.Code.ToString() : $"#{id}";

        return new(
            map.Id,
            map.TableDefId,
            map.TargetColumnDefId,
            CodeOf(map.TargetColumnDefId),
            map.StartColumnDefId,
            CodeOf(map.StartColumnDefId),
            map.EndColumnDefId,
            CodeOf(map.EndColumnDefId),
            map.SelectorColumnDefId,
            map.SelectorColumnDefId is { } selector ? CodeOf(selector) : null,
            map.Summary,
            map.IsStep,
            map.MaxGapSeconds,
            map.TargetUnitId,
            map.MinPercentGood,
            map.RefetchWithinDays,
            map.IsActive,
            Convert.ToHexString(map.RowVersion),
            [.. map.Sources.OrderBy(s => s.Id).Select(s => new RowWindowSourceDto(
                s.Id, s.SelectorValue, s.SourceEntityId, s.SourceField, s.SourceUnitId))]);
    }

    /// <summary>Ідентифікатори колонок прив'язки.</summary>
    public static IEnumerable<int> ColumnIds(RowWindowMap map)
        => new[] { map.TargetColumnDefId, map.StartColumnDefId, map.EndColumnDefId, map.SelectorColumnDefId ?? 0 }
            .Where(id => id != 0);

    /// <summary>
    /// Те з конфігурації прив'язки, що змінює число чи статус згортки, але НЕ входить у провенанс підтягування
    /// (<see cref="RowWindowProvenance"/>): форма ряду, поріг прогалини, поріг покриття й одиниця джерела (X3-04).
    /// </summary>
    /// <param name="IsStep">Форма ряду.</param>
    /// <param name="MaxGapSeconds">Поріг прогалини.</param>
    /// <param name="MinPercentGood">Поріг покриття.</param>
    /// <param name="Sources">Атрибути джерел з одиницею джерела.</param>
    public sealed record FoldConfig(
        bool IsStep,
        int? MaxGapSeconds,
        decimal MinPercentGood,
        IReadOnlyList<(int SourceEntityId, string SourceField, int SourceUnitId)> Sources)
    {
        /// <summary>Знімок поточної конфігурації згортки прив'язки.</summary>
        /// <param name="map">Прив'язка.</param>
        public static FoldConfig Of(RowWindowMap map)
        {
            ArgumentNullException.ThrowIfNull(map);

            return new(
                map.IsStep,
                map.MaxGapSeconds,
                map.MinPercentGood,
                [.. map.Sources.Select(s => (s.SourceEntityId, s.SourceField, s.SourceUnitId))]);
        }
    }

    /// <summary>
    /// Атрибути НОВОЇ конфігурації, чиї вже підтягнуті числа порахувала стара конфігурація згортки (X3-04).
    /// </summary>
    /// <param name="before">Конфігурація згортки до зміни.</param>
    /// <param name="after">Прив'язка після зміни.</param>
    /// <remarks>
    /// Змінилися форма ряду, поріг прогалини чи поріг покриття — усі атрибути прив'язки; змінилася одиниця джерела
    /// атрибута — лише він. ⚠ Атрибути, яких у новій конфігурації немає, сюди НЕ потрапляють: їхні рядки задача
    /// бачить через чинний запис (провенанс, «немає джерела», журнал I1-03), і зняття чинності його б загубило.
    /// </remarks>
    public static IReadOnlyList<(int SourceEntityId, string SourceField)> Refolded(FoldConfig before, RowWindowMap after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var all = before.IsStep != after.IsStep
                  || before.MaxGapSeconds != after.MaxGapSeconds
                  || before.MinPercentGood != after.MinPercentGood;

        return [.. after.Sources
            .Where(s => all || before.Sources.Any(o => o.SourceEntityId == s.SourceEntityId
                                                       && string.Equals(o.SourceField, s.SourceField, StringComparison.OrdinalIgnoreCase)
                                                       && o.SourceUnitId != s.SourceUnitId))
            .GroupBy(s => (s.SourceEntityId, Field: s.SourceField.ToUpperInvariant()))
            .Select(g => (g.Key.SourceEntityId, g.First().SourceField))];
    }

    /// <summary>Розмір сторінки екземплярів, яким правка прив'язки ставить підтягування чи знімає чинність.</summary>
    /// <remarks>
    /// ⛔ Аудит Z6-02: це сторінка, а не стеля — проходяться всі сторінки (<see cref="ForEachOpenInstancePageAsync"/>).
    /// Раніше одна вибірка <c>Take(1000)</c> за Id без курсора лишала екземпляри понад першу тисячу (версія шаблону
    /// спільна для кількох проєктів) без зняття чинності й без задачі — назавжди з числом за старою конфігурацією.
    /// </remarks>
    public const int MaxFetchInstances = 1_000;

    /// <summary>
    /// Проходить усі екземпляри таблиці у відкритих періодах сторінками keyset-а за Id (аудит Z6-02).
    /// </summary>
    /// <param name="store">Сховище прив'язок.</param>
    /// <param name="tableDefId">Таблиця прив'язки.</param>
    /// <param name="onPage">Дія над однією непорожньою сторінкою.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task ForEachOpenInstancePageAsync(
        IRowWindowMapStore store,
        int tableDefId,
        Func<IReadOnlyList<RowWindowFetchRequest>, CancellationToken, Task> onPage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(onPage);

        long? after = null;
        while (true)
        {
            var page = await store.OpenInstancesAsync(tableDefId, after, MaxFetchInstances, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return;
            }

            await onPage(page, ct).ConfigureAwait(false);

            if (page.Count < MaxFetchInstances)
            {
                return;
            }

            after = page[^1].TableInstanceId;
        }
    }

    /// <summary>
    /// Знімає чинність із підтягнутих записів атрибутів, згортку яких змінила правка (X3-04), в УСІХ відкритих
    /// екземплярах таблиці прив'язки (Z6-02).
    /// </summary>
    public static async Task SupersedeRefoldedAsync(
        IRowWindowMapStore store,
        RowWindowMap map,
        IReadOnlyList<(int SourceEntityId, string SourceField)> refolded,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(refolded);

        if (refolded.Count == 0)
        {
            return;
        }

        await ForEachOpenInstancePageAsync(
            store,
            map.TableDefId,
            async (page, pageCt) =>
            {
                foreach (var (sourceEntityId, sourceField) in refolded)
                {
                    await store
                        .SupersedeFoldedValuesAsync(map.Id, page, sourceEntityId, sourceField, pageCt)
                        .ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ставить підтягування всім екземплярам таблиці прив'язки у відкритих періодах (аудит I1-02).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього заведена чи змінена прив'язка не підтягувала НІЧОГО в рядки, що вже існують: тригер
    /// реагує лише на правку Початку/Кінця/селектора, а щогодинний повтор бере тільки рядки з наявним
    /// провенансом. Правки вікон під час паузи тригера теж не ставили (індекс прив'язки на паузі не містить).
    /// Злиття за екземпляром: сплеск правок дає одну задачу.
    /// </remarks>
    public static async Task EnqueueFetchAsync(
        IRowWindowMapStore store, IBackgroundJobScheduler jobs, RowWindowMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!map.IsActive)
        {
            return;
        }

        // ⛔ Z6-02: усі сторінки, а не перша тисяча.
        await ForEachOpenInstancePageAsync(
            store,
            map.TableDefId,
            async (page, pageCt) =>
            {
                foreach (var instance in page)
                {
                    await jobs
                        .EnqueueCoalescedAsync<IRowWindowFetchJob>(
                            RowWindowFetchTarget.Of(instance.TableInstanceId), instance, pageCt, createdByUserId: null)
                        .ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>DTO однієї прив'язки з кодами колонок (один запит).</summary>
    public static async Task<RowWindowMapDto> ToDtoAsync(IRowWindowMapStore store, RowWindowMap map, CancellationToken ct)
        => ToDto(map, await store.FindColumnsAsync([.. ColumnIds(map)], ct).ConfigureAwait(false));

    /// <summary>Прив'язка або 404.</summary>
    public static async Task<RowWindowMap> RequireMapAsync(IRowWindowMapStore store, int id, CancellationToken ct)
        => await store.FindMapAsync(id, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound,
               $"Прив'язки вікна рядка {id} немає.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.rowWindowMap",
                   ["rowWindowMapId"] = id.ToString(CultureInfo.InvariantCulture),
               });

    /// <summary>Колонки за ідентифікаторами; відсутня чи видалена — 404 <c>ECR-INT-0405</c>.</summary>
    public static async Task<IReadOnlyDictionary<int, ColumnDef>> RequireColumnsAsync(
        IRowWindowMapStore store, IEnumerable<int> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var found = await store.FindColumnsAsync(wanted, ct).ConfigureAwait(false);

        foreach (var id in wanted.Where(id => !found.ContainsKey(id)))
        {
            throw new NotFoundException(
                ErrorCodes.EntityFieldMapTargetNotFound,
                $"Колонки {id} немає, або її видалено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0405.column",
                    ["columnDefId"] = id.ToString(CultureInfo.InvariantCulture),
                });
        }

        return found;
    }

    /// <summary>
    /// Колонки запиту для <see cref="RequireColumnsAsync"/>: обов'язкові — завжди, селектор — коли заданий.
    /// </summary>
    /// <remarks>
    /// ⛔ Відкидати нуль можна лише в необов'язкового селектора. Раніше `!= 0` стояло на всіх
    /// чотирьох, тож тіло без колонок (`{}`) проходило перевірку існування, і наступне
    /// `columns[command.TargetColumnDefId]` падало `KeyNotFoundException` — тобто 500 замість
    /// тієї самої відмови «колонки немає», що й на неіснуючий номер.
    /// </remarks>
    public static IEnumerable<int> RequestedColumnIds(int target, int start, int end, int? selector)
        => selector is { } id ? [target, start, end, id] : [target, start, end];

    /// <summary>Форма запиту: відома згортка, непорожні й не задовгі рядки джерел.</summary>
    public static void RequireShape(RowWindowSummaryKind summary, IReadOnlyList<RowWindowSourceInput> inputs)
    {
        if (!Enum.IsDefined(summary))
        {
            throw Invalid("err.ECR-REQ-0422.rowWindowSummaryUnknown", "Невідомий спосіб згортки вікна.");
        }

        foreach (var input in inputs)
        {
            var selector = input.SelectorValue?.Trim();
            if (string.IsNullOrWhiteSpace(input.SourceField)
                || input.SourceField.Length > RowWindowMap.MaxSourceFieldLength
                || selector?.Length > RowWindowMap.MaxSelectorValueLength)
            {
                throw Invalid(
                    "err.ECR-REQ-0422.rowWindowSourceInvalid",
                    "Шлях атрибута обов'язковий (до 200 знаків), значення селектора — до 100 знаків.");
            }
        }
    }

    /// <summary>Одиниці й сутності джерел існують.</summary>
    public static async Task RequireReferencesAsync(
        ICollectionStore sources, int targetUnitId, IReadOnlyList<RowWindowSourceInput> inputs, CancellationToken ct)
    {
        foreach (var unitId in inputs.Select(i => i.SourceUnitId).Append(targetUnitId).Distinct())
        {
            if (!await sources.UnitExistsAsync(unitId, ct).ConfigureAwait(false))
            {
                throw new NotFoundException(
                    ErrorCodes.UnitNotFound,
                    $"Одиниці {unitId} немає в довіднику.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-UOM-0404.unitId",
                        ["id"] = unitId.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }

        foreach (var entityId in inputs.Select(i => i.SourceEntityId).Distinct())
        {
            if (await sources.FindSourceEntityAsync(entityId, ct).ConfigureAwait(false) is null)
            {
                throw new NotFoundException(
                    ErrorCodes.SourceEntityNotFound,
                    $"Сутності джерела {entityId} немає або вона вимкнена.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                        ["id"] = entityId.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }
    }

    /// <summary>Пороги підтягування (типові, коли не задані) і перелік джерел.</summary>
    public static void ApplyPolicyAndSources(
        RowWindowMap map,
        decimal? minPercentGood,
        int? refetchWithinDays,
        int? maxGapSeconds,
        IReadOnlyList<RowWindowSourceInput> inputs)
    {
        map.SetFetchPolicy(
            minPercentGood ?? RowWindowMap.DefaultMinPercentGood,
            refetchWithinDays ?? RowWindowMap.DefaultRefetchWithinDays,
            maxGapSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

        foreach (var input in inputs)
        {
            map.AddSource(input.SelectorValue, input.SourceEntityId, input.SourceField, input.SourceUnitId);
        }
    }

    private static BusinessRuleException Invalid(string messageKey, string message)
        => new(ErrorCodes.RequestInvalid, message, new Dictionary<string, object?> { ["messageKey"] = messageKey });
}

/// <summary>Перелік прив'язок і одна прив'язка. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
/// <param name="store">Сховище прив'язок.</param>
/// <param name="access">Служба рішень про доступ.</param>
/// <param name="currentUser">Поточний користувач.</param>
public sealed class ListRowWindowMapsHandler(
    IRowWindowMapStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Прив'язки таблиці й/або сутності джерела, або всі.</summary>
    /// <param name="tableDefId">Таблиця; <c>null</c> — усі.</param>
    /// <param name="sourceEntityId">Сутність джерела, що має джерело в прив'язці; <c>null</c> — будь-яка.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<RowWindowMapDto>> ListAsync(int? tableDefId, int? sourceEntityId, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        var maps = await store.ListMapsAsync(tableDefId, sourceEntityId, ct).ConfigureAwait(false);
        var columns = await store
            .FindColumnsAsync([.. maps.SelectMany(RowWindowMapSupport.ColumnIds).Distinct()], ct)
            .ConfigureAwait(false);

        return [.. maps.Select(m => RowWindowMapSupport.ToDto(m, columns))];
    }

    /// <summary>Одна прив'язка; немає — 404.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<RowWindowMapDto> GetAsync(int id, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        var map = await RowWindowMapSupport.RequireMapAsync(store, id, ct).ConfigureAwait(false);

        return await RowWindowMapSupport.ToDtoAsync(store, map, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Заводить прив'язку вікна рядка. Право <c>Integration.Manage</c> і грант <c>Manage</c> на кожен проєкт, що
/// використовує колонку-ціль.
/// </summary>
/// <remarks>
/// ⚠ За зразком <see cref="CreateEntityFieldMapHandler"/>: право першим, існування цілей — до запису, слід у журналі
/// структурних змін (<c>ФВ-12.10</c>) — в одній транзакції із записом. Типи колонок тримає домен
/// (<see cref="RowWindowMap.Create"/>).
/// </remarks>
public sealed class CreateRowWindowMapHandler(
    IRowWindowMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    IRowWindowColumnIndex columnIndex,
    IBackgroundJobScheduler jobs)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "CreateRowWindowMap";

    /// <summary>Заводить прив'язку.</summary>
    /// <param name="command">Налаштування.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<RowWindowMapDto> HandleAsync(CreateRowWindowMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var inputs = command.Sources ?? [];
        RowWindowMapSupport.RequireShape(command.Summary, inputs);

        var columns = await RowWindowMapSupport
            .RequireColumnsAsync(
                store,
                RowWindowMapSupport.RequestedColumnIds(
                    command.TargetColumnDefId, command.StartColumnDefId, command.EndColumnDefId, command.SelectorColumnDefId),
                ct)
            .ConfigureAwait(false);

        var target = columns[command.TargetColumnDefId];
        if (target.TableDefId != command.TableDefId)
        {
            throw new BusinessRuleException(
                "ECR-INT-0422",
                $"Колонка-ціль {command.TargetColumnDefId} не належить таблиці {command.TableDefId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.rowWindowTargetNotInTable",
                    ["tableDefId"] = command.TableDefId.ToString(CultureInfo.InvariantCulture),
                    ["targetColumn"] = target.Code.ToString(),
                });
        }

        await ColumnProjectGrants.RequireManageAsync(sources, profile, target.Id, ct).ConfigureAwait(false);
        await RowWindowMapSupport.RequireReferencesAsync(sources, command.TargetUnitId, inputs, ct).ConfigureAwait(false);

        if (await store.TargetTakenAsync(target.TableDefId, target.Id, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"На колонку «{target.Code}» уже є прив'язка вікна рядка.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowTargetTaken",
                    ["targetColumn"] = target.Code.ToString(),
                });
        }

        var map = RowWindowMap.Create(
            target,
            columns[command.StartColumnDefId],
            columns[command.EndColumnDefId],
            command.SelectorColumnDefId is { } selector ? columns[selector] : null,
            command.Summary,
            command.IsStep,
            command.TargetUnitId);
        RowWindowMapSupport.ApplyPolicyAndSources(map, command.MinPercentGood, command.RefetchWithinDays, command.MaxGapSeconds, inputs);

        // ⛔ V6-02 (MI-02 (в), як у AcceptSourceUnitChangeHandler / PatchCellsHandler): для нової прив'язки жодного
        // провенансу ще нема, тож щогодинний повтор її не підбере - постановка підтягування після коміту, яку
        // зупинка процесу чи збій черги міг загубити, лишала прив'язку без жодного прочитаного рядка. Черга в базі:
        // постановка ВСЕРЕДИНІ транзакції, останнім оператором (разом із прив'язкою або нічого); Quartz у пам'яті -
        // після коміту (його задача до коміту не бачила б прив'язки).
        var enlist = jobs.EnlistsInCallerTransaction;

        RowWindowMap? created = null;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            created = await store.AddMapAsync(map, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.RowWindowMapType, created.Id, AuditOperation,
                oldJson: null, newJson: IntegrationConfigAudit.Snapshot(RowWindowMapSupport.ToDto(created, columns)),
                reason: IntegrationConfigAudit.Reason("integrationAudit.rowWindowCreated", ("column", target.Code)),
                innerCt).ConfigureAwait(false);

            if (enlist)
            {
                await RowWindowMapSupport.EnqueueFetchAsync(store, jobs, created, innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        // Знімок колонок вікна живе в пам'яті до 60 с: без скидання правка Початку/Кінця нової прив'язки не
        // ставила б підтягування (IRowWindowTrigger питає індекс, а не базу).
        columnIndex.Invalidate();

        if (!enlist)
        {
            await RowWindowMapSupport.EnqueueFetchAsync(store, jobs, created!, ct).ConfigureAwait(false);
        }

        return RowWindowMapSupport.ToDto(created!, columns);
    }
}

/// <summary>
/// Замінює налаштування прив'язки: вікно, селектор, згортку, пороги, стан і джерела. Право <c>Integration.Manage</c>
/// і грант <c>Manage</c> на проєкти колонки-цілі.
/// </summary>
/// <remarks>
/// ⚠ Пауза й відновлення — це <c>isActive</c> у тілі. Таблиця й колонка-ціль не змінюються (ключ прив'язки, на
/// нього посилається провенанс підтягувань).
/// </remarks>
public sealed class UpdateRowWindowMapHandler(
    IRowWindowMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    IRowWindowColumnIndex columnIndex,
    IBackgroundJobScheduler jobs)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "UpdateRowWindowMap";

    /// <summary>Замінює налаштування прив'язки.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="command">Нові налаштування.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<RowWindowMapDto> HandleAsync(int id, UpdateRowWindowMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var map = await RowWindowMapSupport.RequireMapAsync(store, id, ct).ConfigureAwait(false);
        await ColumnProjectGrants.RequireManageAsync(sources, profile, map.TargetColumnDefId, ct).ConfigureAwait(false);

        if (command.RowVersion is { } expected
            && !string.Equals(expected, Convert.ToHexString(map.RowVersion), StringComparison.OrdinalIgnoreCase))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"Прив'язку вікна рядка {id} змінили після того, як ви її прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowConcurrency",
                    ["rowWindowMapId"] = id.ToString(CultureInfo.InvariantCulture),
                });
        }

        var inputs = command.Sources ?? [];
        RowWindowMapSupport.RequireShape(command.Summary, inputs);

        var columns = await RowWindowMapSupport
            .RequireColumnsAsync(
                store,
                RowWindowMapSupport.RequestedColumnIds(
                    map.TargetColumnDefId, command.StartColumnDefId, command.EndColumnDefId, command.SelectorColumnDefId),
                ct)
            .ConfigureAwait(false);
        await RowWindowMapSupport.RequireReferencesAsync(sources, command.TargetUnitId, inputs, ct).ConfigureAwait(false);

        var old = IntegrationConfigAudit.Snapshot(await RowWindowMapSupport.ToDtoAsync(store, map, ct).ConfigureAwait(false));
        var foldBefore = RowWindowMapSupport.FoldConfig.Of(map);

        // Спершу перевірка домену (стан не змінюється, доки вона не пройшла), потім позначка старих джерел до
        // видалення, потім заміна: відмова не лишає відстежених джерел без власника.
        map.Reconfigure(
            columns[map.TargetColumnDefId],
            columns[command.StartColumnDefId],
            columns[command.EndColumnDefId],
            command.SelectorColumnDefId is { } selector ? columns[selector] : null,
            command.Summary,
            command.IsStep,
            command.TargetUnitId);
        store.ReleaseSources(map);
        map.ClearSources();
        RowWindowMapSupport.ApplyPolicyAndSources(map, command.MinPercentGood, command.RefetchWithinDays, command.MaxGapSeconds, inputs);
        map.SetActive(command.IsActive);

        // ⛔ V8-03: зняття чинності — і на паузі. Інакше «пауза → правка згортки → відновлення» різниці не бачить
        // (foldBefore останнього PUT — уже нова конфігурація), а NeedsFetch не знає IsStep/MaxGap/MinPercentGood/
        // одиниці джерела: старе Fetched-число закритого вікна лишалося чинним назавжди. На паузі задача не
        // ставиться (EnqueueFetchAsync), її поставить відновлення — і NeedsFetch(current: null) перечитає рядок.
        var refolded = RowWindowMapSupport.Refolded(foldBefore, map);

        // ⛔ V6-02 (MI-02 (в)): знято чинність (нижче) і поставлено перечитування мусять бути ОДНІЄЮ дією. Доти
        // чинність знімала транзакція, а перечитування ставилося після коміту: зупинка процесу чи збій черги в цьому
        // вікні лишали вікна без чинного числа, а UI вже показував нову конфігурацію. Черга в базі - постановка ВСЕРЕДИНІ
        // транзакції, останнім оператором; Quartz у пам'яті - після коміту (до коміту його задача прочитала б
        // стару чинність).
        var enlist = jobs.EnlistsInCallerTransaction;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.SaveAsync(innerCt).ConfigureAwait(false);

            // ⛔ X3-04: форма ряду, поріг прогалини, поріг покриття й одиниця джерела змінюють число (чи статус), але
            // не входять у провенанс — без цього закрите вікно Fetched/Partial лишалося «вже підтягнутим» за старою
            // конфігурацією, а UI показував нову. Зняття чинності — та сама операція, що й у звичайному повторі
            // (історія лишається); тоді NeedsFetch(current: null) дає true, і поставлена нижче задача перечитує
            // рядки. ⚠ Лише відкриті екземпляри (ті, яким ставиться задача) — УСІ, сторінками (Z6-02). Задача, що вже
            // виконується й прочитала прив'язку до PUT, може ще записати число за старою конфігурацією — закрити це
            // можна лише відбитком конфігурації в провенансі (міграція).
            await RowWindowMapSupport.SupersedeRefoldedAsync(store, map, refolded, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.RowWindowMapType, map.Id, AuditOperation,
                old, IntegrationConfigAudit.Snapshot(RowWindowMapSupport.ToDto(map, columns)),
                IntegrationConfigAudit.Reason("integrationAudit.rowWindowChanged", ("id", map.Id)), innerCt).ConfigureAwait(false);

            if (enlist)
            {
                await RowWindowMapSupport.EnqueueFetchAsync(store, jobs, map, innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        // Колонки Початку/Кінця/селектора могли змінитися, а активність — вимкнутися: скидаємо знімок індексу.
        // Старі RowWindowValue лишаються історією — PUT лише знімає чинність (X3-04 вище), не видаляє.
        columnIndex.Invalidate();

        // ⛔ Аудит I1-02: нова конфігурація (джерела, згортка, одиниця) чи відновлення з паузи — рядки
        // відкритих періодів перечитуються; що саме, вирішує NeedsFetch за провенансом.
        if (!enlist)
        {
            await RowWindowMapSupport.EnqueueFetchAsync(store, jobs, map, ct).ConfigureAwait(false);
        }

        return RowWindowMapSupport.ToDto(map, columns);
    }
}

/// <summary>
/// Видаляє прив'язку, за якою нічого не підтягнуто. Право <c>Integration.Manage</c> і грант <c>Manage</c> на
/// проєкти колонки-цілі.
/// </summary>
/// <remarks>
/// ⛔ Прив'язка із записами провенансу (<c>ext.RowWindowValue</c>) не видаляється — <c>409 ECR-INT-0409</c>: записи
/// пояснюють, звідки числа в колонці, і зникли б (чи завадили б ключу) разом з нею. Вихід — пауза
/// (<c>isActive = false</c>).
/// </remarks>
public sealed class DeleteRowWindowMapHandler(
    IRowWindowMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    IRowWindowColumnIndex columnIndex)
{
    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "DeleteRowWindowMap";

    /// <summary>Видаляє прив'язку.</summary>
    /// <param name="id">Прив'язка.</param>
    /// <param name="ct">Скасування.</param>
    public async Task HandleAsync(int id, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var map = await RowWindowMapSupport.RequireMapAsync(store, id, ct).ConfigureAwait(false);
        await ColumnProjectGrants.RequireManageAsync(sources, profile, map.TargetColumnDefId, ct).ConfigureAwait(false);

        var values = await store.CountValuesAsync(id, ct).ConfigureAwait(false);
        if (values > 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"За прив'язкою вікна рядка {id} уже підтягнуто {values} значень: видалення лишило б числа без пояснення. Призупиніть прив'язку.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowMapHasValues",
                    ["values"] = values,
                });
        }

        var removed = IntegrationConfigAudit.Snapshot(await RowWindowMapSupport.ToDtoAsync(store, map, ct).ConfigureAwait(false));

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.RemoveMapAsync(map, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.RowWindowMapType, id, AuditOperation,
                removed, newJson: null, IntegrationConfigAudit.Reason("integrationAudit.rowWindowDeleted", ("id", id)), innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        columnIndex.Invalidate();
    }
}
