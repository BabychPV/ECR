// src/Ecr.Application/Registries/Rows/GetRegistryEntryHistoryHandler.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries.Rows;

/// <summary>Рядок історії запису довідника (<c>GET /registries/{code}/entries/{id}/history</c>, RT-15).</summary>
/// <param name="At">Момент зміни (UTC) — початок системної версії.</param>
/// <param name="ByUserId">Автор; <c>null</c> — невідомий (фонова задача без автора, рядок до міграції <c>RK03</c>).</param>
/// <param name="ByDisplayName">Ім'я автора (<c>sec.User.DisplayName</c>, не логін — R-A2); <c>null</c> — невідомий.</param>
/// <param name="Kind">
/// <c>created</c>, <c>value</c>, <c>name</c>, <c>validity</c>, <c>active</c>, <c>deleted</c>.
/// </param>
/// <param name="Field">Код поля для <c>value</c>; інакше <c>null</c>.</param>
/// <param name="OldValue">
/// Було: значення в поданні <see cref="RegistryRowValueDto.Value"/> (число без втрати знаків, дата
/// <c>yyyy-MM-dd</c>, <c>Lookup</c> — Id цілі); для <c>validity</c> — інтервал ISO 8601
/// <c>2026-01-01/2027-01-01</c> з <c>..</c> для відкритого кінця; <c>null</c> — не було.
/// </param>
/// <param name="NewValue">Стало, у тому самому поданні; <c>null</c> — значення прибрано.</param>
public sealed record RegistryEntryHistoryItemDto(
    DateTime At,
    int? ByUserId,
    string? ByDisplayName,
    string Kind,
    string? Field,
    string? OldValue,
    string? NewValue)
{
    /// <summary>Назва цілі <c>Lookup</c> або код одиниці поля <c>Unit</c> для «було»; інакше <c>null</c>.</summary>
    public string? OldDisplay { get; init; }

    /// <summary>Те саме для «стало».</summary>
    public string? NewDisplay { get; init; }
}

/// <summary>
/// Історія одного запису довідника: хто, коли й що змінив (RT-15, FEATURE-REGISTRY-TABLES §7.1, §8.5).
/// Право <c>Registry.View</c> або грант <c>Read</c> на довідник.
/// </summary>
/// <remarks>
/// ⛔ Джерело — системні версії <c>dic.RegistryEntry</c>/<c>dic.RegistryValue</c> (<c>FOR SYSTEM_TIME ALL</c>,
/// <c>R-9</c>, <c>D-158</c>), а не журнал <c>aud.*</c>: версії пише база на кожному шляху запису, журнал —
/// не на кожному (імпорт CSV пише туди лише підсумок файлу). Рядок історії — різниця двох сусідніх
/// версій.
/// <para>
/// ⚠ Значення, задані разом зі створенням запису (той самий момент), окремих рядків не дають: їх
/// підсумовує <c>created</c>, а стан на будь-який момент читається <c>GET …/rows?asOfUtc=</c>.
/// </para>
/// <para>
/// ⚠ Прибране значення (рядок <c>dic.RegistryValue</c> видалено) автора в базі не має — береться автор
/// версії запису того самого моменту, якщо вона є.
/// </para>
/// <para>
/// Порядок — від найновішого; курсор — ключ останнього рядка сторінки, а не зсув: зміна, що з'явилася
/// між сторінками, не повторює рядків.
/// </para>
/// </remarks>
public sealed class GetRegistryEntryHistoryHandler(
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Вид рядка: запис створено.</summary>
    public const string Created = "created";

    /// <summary>Вид рядка: значення поля змінено, задано або прибрано.</summary>
    public const string ValueChanged = "value";

    /// <summary>Вид рядка: назву запису змінено.</summary>
    public const string Renamed = "name";

    /// <summary>Вид рядка: вікно чинності змінено.</summary>
    public const string Validity = "validity";

    /// <summary>Вид рядка: запис увімкнено чи вимкнено.</summary>
    public const string Active = "active";

    /// <summary>Вид рядка: запис видалено логічно.</summary>
    public const string Deleted = "deleted";

    /// <summary>Читає сторінку історії запису.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="entryId">Запис.</param>
    /// <param name="page">Курсор і розмір сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-REG-0404</c>: довідника чи запису в ньому немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c>: розмір сторінки поза межами.</exception>
    public async Task<PagedResult<RegistryEntryHistoryItemDto>> HandleAsync(
        string registryCode, long entryId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Read, new RegistryLookup(registries, registryCode), ct)
            .ConfigureAwait(false);

        if (!page.IsValid)
        {
            var max = CursorRequest.MaxLimit.ToString(CultureInfo.InvariantCulture);
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange", ["max"] = max });
        }

        var definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Довідника «{registryCode}» не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REG-0404.registry", ["registryCode"] = registryCode });

        // ⚠ Видалений запис має історію — саме її й шукають після видалення. Запис ІНШОГО довідника
        // для цього маршруту не існує (як у DELETE, ФВ-14.9a).
        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.RegistryDefId != definition.Id)
        {
            throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Запису {entryId} у довіднику «{registryCode}» немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = entryId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var slice = await rows.ReadEntryHistoryAsync(entryId, ct).ConfigureAwait(false);
        var events = Diff(definition, slice, currentUser.Language);

        var after = DecodeCursor(page.Cursor);
        var ordered = events.OrderByDescending(e => e.Key, KeyOrder.Instance).ToList();
        var pageItems = ordered
            .Where(e => after is null || KeyOrder.Instance.Compare(e.Key, after.Value) < 0)
            .Take(page.Limit + 1)
            .ToList();
        var hasMore = pageItems.Count > page.Limit;
        if (hasMore)
        {
            pageItems.RemoveAt(pageItems.Count - 1);
        }

        return new PagedResult<RegistryEntryHistoryItemDto>(
            [.. pageItems.Select(e => e.Item)],
            hasMore ? EncodeCursor(pageItems[^1].Key) : null,
            ordered.Count);
    }

    /// <summary>Рядки історії — різниці сусідніх версій запису й кожного поля.</summary>
    /// <param name="definition">Опис довідника (типи й коди полів).</param>
    /// <param name="slice">Версії.</param>
    /// <param name="language">Мова назв.</param>
    internal static List<HistoryEvent> Diff(RegistryDef definition, RegistryEntryHistorySlice slice, string language)
    {
        var events = new List<HistoryEvent>();
        if (slice.Entry.Count == 0)
        {
            return events;
        }

        string? Name(int? userId) => userId is { } id && slice.UserNames.TryGetValue(id, out var name) ? name : null;

        void Add(DateTime at, int? by, string kind, string? field, string? oldValue, string? newValue, string? oldDisplay = null, string? newDisplay = null)
            => events.Add(new HistoryEvent(
                new HistoryKey(at.Ticks, Rank(kind), field ?? string.Empty),
                new RegistryEntryHistoryItemDto(at, by, Name(by), kind, field, oldValue, newValue)
                {
                    OldDisplay = oldDisplay,
                    NewDisplay = newDisplay,
                }));

        var first = slice.Entry[0];
        Add(first.FromUtc, first.ChangedByUserId, Created, null, null, null);

        for (var i = 1; i < slice.Entry.Count; i++)
        {
            var (previous, current) = (slice.Entry[i - 1], slice.Entry[i]);
            var (at, by) = (current.FromUtc, current.ChangedByUserId);

            if (!SameText(previous.Display, current.Display))
            {
                Add(at, by, Renamed, null, previous.Display.Get(language), current.Display.Get(language));
            }

            if (previous.ValidFrom != current.ValidFrom || previous.ValidTo != current.ValidTo)
            {
                Add(at, by, Validity, null, Window(previous.ValidFrom, previous.ValidTo), Window(current.ValidFrom, current.ValidTo));
            }

            var deleted = !previous.IsDeleted && current.IsDeleted;

            // Видалення саме вимикає запис — окремий рядок «вимкнено» того самого моменту був би шумом.
            if (previous.IsActive != current.IsActive && !deleted)
            {
                Add(at, by, Active, null, Bool(previous.IsActive), Bool(current.IsActive));
            }

            if (deleted)
            {
                Add(at, by, Deleted, null, null, null);
            }
        }

        var fields = definition.Fields.ToDictionary(f => f.Id);
        var referenced = slice.Referenced.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var entryAuthorAt = slice.Entry.GroupBy(e => e.FromUtc).ToDictionary(g => g.Key, g => g.Last().ChangedByUserId);

        foreach (var group in slice.Values.GroupBy(v => v.Value.RegistryFieldDefId))
        {
            // Поле, якого вже немає в описі, показується своїм Id — історію не приховуємо.
            fields.TryGetValue(group.Key, out var field);
            var code = field?.Code ?? "#" + group.Key.ToString(CultureInfo.InvariantCulture);
            var versions = group.OrderBy(v => v.FromUtc).ThenBy(v => v.ValueId).ToList();

            (string? Text, string? Display) Show(RegistryValueVersion version)
                => field is null
                    ? (null, null)
                    : (GetRegistryRowsHandler.ValueText(field.DataType, version.Value), DisplayOf(field, version.Value, referenced, language));

            for (var i = 0; i < versions.Count; i++)
            {
                var version = versions[i];
                var previous = versions.Take(i).LastOrDefault(v => v.ToUtc == version.FromUtc);

                // Значення, задане разом зі створенням запису, підсумовує рядок `created`.
                if (previous is null && version.FromUtc == first.FromUtc)
                {
                    continue;
                }

                var (oldText, oldDisplay) = previous is null ? (null, null) : Show(previous);
                var (newText, newDisplay) = Show(version);

                // Версія без зміни САМЕ цього значення (переписано тим самим або змінилася лише одиниця
                // числа, якої рядок не несе) — не рядок історії.
                if (previous is not null
                    && string.Equals(oldText, newText, StringComparison.Ordinal)
                    && previous.Value.UnitId == version.Value.UnitId)
                {
                    continue;
                }

                Add(version.FromUtc, version.ChangedByUserId, ValueChanged, code, oldText, newText, oldDisplay, newDisplay);
            }

            // Кінець версії без наступника того самого поля — значення прибрано.
            foreach (var ended in versions.Where(v => v.ToUtc.Year < 9999 && !versions.Exists(n => n.FromUtc == v.ToUtc)))
            {
                var (oldText, oldDisplay) = Show(ended);
                Add(ended.ToUtc, entryAuthorAt.GetValueOrDefault(ended.ToUtc), ValueChanged, code, oldText, null, oldDisplay);
            }
        }

        return events;
    }

    /// <summary>Порядок видів одного моменту: у стрічці «найновіше вгорі» створення лягає найнижче.</summary>
    private static int Rank(string kind) => kind switch
    {
        Created => 0,
        ValueChanged => 1,
        Renamed => 2,
        Validity => 3,
        Active => 4,
        _ => 5,
    };

    private static bool SameText(Domain.ValueObjects.LocalizedText left, Domain.ValueObjects.LocalizedText right)
        => left.Values.Count == right.Values.Count
           && left.Values.All(p => right.Values.TryGetValue(p.Key, out var other) && string.Equals(p.Value, other, StringComparison.Ordinal));

    /// <summary>Вікно інтервалом ISO 8601: <c>2026-01-01/2027-01-01</c>, відкритий кінець — <c>..</c>.</summary>
    private static string Window(DateOnly? from, DateOnly? to) => $"{Iso(from)}/{Iso(to)}";

    private static string Iso(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "..";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? DisplayOf(
        RegistryFieldDef field, RegistryRowValue value, Dictionary<long, RegistryEntry> referenced, string language)
        => field.DataType switch
        {
            CellDataType.Lookup when value.RefEntryId is { } target && referenced.TryGetValue(target, out var entry)
                => entry.DisplayL10n.Get(language) ?? entry.Code,
            CellDataType.Unit => value.UnitCode,
            _ => null,
        };

    private static string EncodeCursor(HistoryKey key)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture, $"{key.Ticks}|{key.Rank}|{key.Field}")));

    /// <summary>Зіпсований курсор — початок переліку, а не помилка (як у решті переліків).</summary>
    private static HistoryKey? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 3);
            return parts.Length == 3
                   && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                   && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rank)
                ? new HistoryKey(ticks, rank, parts[2])
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Ключ порядку рядка історії: момент, вид, поле.</summary>
    internal readonly record struct HistoryKey(long Ticks, int Rank, string Field);

    /// <summary>Порядок ключів: момент, вид, код поля (ordinal — не залежить від культури сервера).</summary>
    private sealed class KeyOrder : IComparer<HistoryKey>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare(HistoryKey x, HistoryKey y)
        {
            var byTime = x.Ticks.CompareTo(y.Ticks);
            if (byTime != 0)
            {
                return byTime;
            }

            var byRank = x.Rank.CompareTo(y.Rank);
            return byRank != 0 ? byRank : string.CompareOrdinal(x.Field, y.Field);
        }
    }

    /// <summary>Рядок історії з ключем порядку.</summary>
    internal sealed record HistoryEvent(HistoryKey Key, RegistryEntryHistoryItemDto Item);
}
