// src/Ecr.Application/Integration/SourceEvents/SourceEventSyncPlanner.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>Що синхронізація робить з подією в цьому прогоні.</summary>
public enum SourceEventAction
{
    /// <summary>Записати рядок (створити чи оновити).</summary>
    Write,

    /// <summary>Подія ще триває — рядка немає й не буде (§4.7.4, крок 3).</summary>
    Open,

    /// <summary>Екземпляра періоду ще немає чи період не відкрито — повтор наступним прогоном.</summary>
    PeriodNotOpen,

    /// <summary>Період закритий — нуль записів.</summary>
    PeriodClosed,

    /// <summary>Початок прив'язаної події переїхав в інший період; рядок не переноситься.</summary>
    PeriodChanged,

    /// <summary>Нічого не змінювати: рядок є, а подія знову без кінця чи екземпляр недоступний.</summary>
    KeepAsIs,
}

/// <summary>Період, у який потрапляє початок події, і стан його екземпляра таблиці.</summary>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="State">Стан періоду.</param>
/// <param name="TableInstanceId">Екземпляр таблиці мапінгу в цьому періоді; <c>null</c> — його ще немає.</param>
public sealed record SourceEventPeriodTarget(int PeriodKey, PeriodState State, long? TableInstanceId);

/// <summary>Наявний зв'язок «подія ↔ рядок» — те, що синхронізація знає з минулих прогонів.</summary>
/// <param name="SourceEventId">ID події в джерелі, за яким зв'язок зараз ключований.</param>
/// <param name="EventName">Назва події (до 400 знаків, як у сховищі).</param>
/// <param name="StartUtc">Початок події, як його бачив минулий прогін.</param>
/// <param name="Status">Стан зв'язку.</param>
/// <param name="PeriodKey">Період рядка; <c>null</c> — рядка немає.</param>
/// <param name="TableInstanceId">Екземпляр таблиці рядка; <c>null</c> — рядка немає.</param>
/// <param name="RowKey">Ключ рядка; <c>null</c> — рядка немає.</param>
/// <param name="PrimaryElement">Первинний елемент у ключовому вигляді; <c>null</c> — старий зв'язок (M6).</param>
public sealed record SourceEventLinkState(
    string SourceEventId,
    string? EventName,
    DateTime StartUtc,
    SourceEventLinkStatus Status,
    int? PeriodKey,
    long? TableInstanceId,
    string? RowKey,
    string? PrimaryElement = null)
{
    /// <summary>Чи прив'язано рядок документа.</summary>
    public bool HasRow => TableInstanceId is not null;
}

/// <summary>Вхід планувальника: прочитані події, наявні зв'язки й правило «початок → період».</summary>
/// <param name="FromUtc">Початок вікна прогону, включно.</param>
/// <param name="ToUtc">Кінець вікна прогону, виключно.</param>
/// <param name="Events">Події, які повернуло джерело.</param>
/// <param name="Truncated">Читання обрізано стелею подій: вікно прочитано НЕ повністю.</param>
/// <param name="ErrorCode">Код часткової відмови джерела; <c>null</c> — відмов не було.</param>
/// <param name="Links">Усі зв'язки цього мапінгу.</param>
/// <param name="PeriodOf">Початок події (UTC) → період і екземпляр; <c>null</c> — періоду немає.</param>
/// <param name="FilterAttribute">Звуження мапінгу: атрибут; <c>null</c> — без звуження.</param>
/// <param name="FilterScope">Звуження мапінгу: де лежить атрибут.</param>
/// <param name="FilterValue">Звуження мапінгу: значення.</param>
public sealed record SourceEventSyncInput(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<SourceEvent> Events,
    bool Truncated,
    string? ErrorCode,
    IReadOnlyList<SourceEventLinkState> Links,
    Func<DateTime, SourceEventPeriodTarget?> PeriodOf,
    string? FilterAttribute = null,
    SourceEventAttributeScope? FilterScope = null,
    string? FilterValue = null);

/// <summary>Одна подія плану.</summary>
/// <param name="Event">Подія, як її дало джерело; кінець уже нормалізовано (незакрита — <c>null</c>).</param>
/// <param name="Action">Що з нею робити.</param>
/// <param name="Link">Наявний зв'язок (за ID чи за природним ключем); <c>null</c> — подію бачимо вперше.</param>
/// <param name="RowKey">Ключ рядка: наявного зв'язку, інакше <c>EF-…</c> від ID події.</param>
/// <param name="Target">Період початку події; <c>null</c> — його немає.</param>
/// <param name="ElementToStore">
/// Ключовий вигляд первинного елемента, який зв'язок має записати (M6); <c>null</c> — джерело його не дало
/// чи ключ «початок + елемент» неоднозначний (інша подія чи зв'язок несе той самий) — тоді не пишемо.
/// </param>
public sealed record SourceEventPlanItem(
    SourceEvent Event,
    SourceEventAction Action,
    SourceEventLinkState? Link,
    string RowKey,
    SourceEventPeriodTarget? Target,
    string? ElementToStore = null)
{
    /// <summary>Чи створюється новий рядок (а не оновлюється наявний).</summary>
    public bool IsCreate => Action == SourceEventAction.Write && Link is not { HasRow: true };

    /// <summary>
    /// Чи зв'язок знайдено за природним ключем при ІНШОМУ ID події: ID у джерелі змінився
    /// (подію перестворили), і зв'язок треба перекласти на новий ID.
    /// </summary>
    public bool IsRekey
        => Link is not null && !string.Equals(Link.SourceEventId, Event.EventId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>План прогону за одним мапінгом.</summary>
/// <param name="Items">Події для дії, у порядку джерела.</param>
/// <param name="Missing">Зв'язки з рядком, чиєї події джерело при ПОВНОМУ читанні не повернуло.</param>
/// <param name="SkippedNonRoot">Скільки подій відкинуто як не кореневі.</param>
/// <param name="Filtered">Скільки подій відкинуто звуженням мапінгу.</param>
/// <param name="MissingSuppressed">Позначку «зникла» не ставили, бо читання обрізане чи з відмовою.</param>
public sealed record SourceEventSyncPlan(
    IReadOnlyList<SourceEventPlanItem> Items,
    IReadOnlyList<SourceEventLinkState> Missing,
    int SkippedNonRoot,
    int Filtered,
    bool MissingSuppressed);

/// <summary>
/// Планує синхронізацію подій за одним мапінгом (FEATURE-HSE301-VIEW §4.7.4): що створити,
/// що оновити, що лише позначити. Чиста функція: ні бази, ні джерела, ні годинника.
/// </summary>
/// <remarks>
/// ⛔ Правила, кожне — про те, щоб не записати й не втратити мовчки:
/// <list type="bullet">
/// <item>Лише <b>кореневі</b> події (<c>ParentId</c> порожній): дочірні EF — не звітні (рішення людини
/// 2026-09-30); <c>ParentId</c> лишається сирим полем події, а не перетворюється на ієрархію.</item>
/// <item>Незакрита подія (кінець <c>null</c> чи ≥ <c>9999-01-01</c>) не рахується: без кінця немає ні
/// тривалості, ні вікна об'єму. Її перечитує наступний прогін — вона знову в вікні перетину.</item>
/// <item>EFID — лише кеш. Природний ключ — шаблон (сутність мапінгу), початок і назва: подія, яку
/// перестворили з новим ID, лягає в СВІЙ рядок, а не подвоює викиди. Зіставлення — лише один-до-одного:
/// дві однакові за ключем події не зіставляються з жодною, щоб не злити різні.</item>
/// <item><c>Missing</c> — лише прив'язаним подіям із початком у вікні й лише при повному читанні
/// (<c>Truncated = false</c>, без <c>ErrorCode</c>): усічене читання не доводить зникнення.</item>
/// <item>Закритий період — нуль записів; рядок прив'язаної події, чий початок переїхав в інший період,
/// не переноситься.</item>
/// </list>
/// </remarks>
public static class SourceEventSyncPlanner
{
    /// <summary>
    /// Кінець, починаючи з якого подія вважається незакритою (сторожова дата PI AF). Дзеркало
    /// <c>SourceEventFolder.OpenEndSentinel</c>: планувальник не залежить від адаптера, а тестові
    /// й інші транспорти можуть віддати сторожову дату як є.
    /// </summary>
    public static readonly DateTime OpenEndSentinel = new(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const int MaxEventNameLength = 400;

    /// <summary>Будує план.</summary>
    /// <param name="input">Вхід.</param>
    /// <returns>План.</returns>
    public static SourceEventSyncPlan Plan(SourceEventSyncInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (roots, nonRoot, filtered) = Select(input);

        var byId = new Dictionary<string, SourceEventLinkState>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in input.Links)
        {
            byId.TryAdd(link.SourceEventId, link);
        }

        var returned = roots.Select(e => e.EventId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rekeyed = MatchByNaturalKey(roots, byId, returned);

        var items = new List<SourceEventPlanItem>(roots.Count);
        foreach (var ev in roots)
        {
            var link = byId.GetValueOrDefault(ev.EventId) ?? rekeyed.GetValueOrDefault(ev.EventId);
            var decided = Decide(ev, link, input.PeriodOf);
            items.Add(decided with { ElementToStore = ElementToStore(ev, link, roots, input.Links) });
        }

        var claimed = rekeyed.Values.Select(l => l.SourceEventId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressed = input.Truncated || input.ErrorCode is not null;
        var missing = suppressed
            ? []
            : input.Links
                .Where(l => l.HasRow
                            && l.Status != SourceEventLinkStatus.Missing
                            && !returned.Contains(l.SourceEventId)
                            && !claimed.Contains(l.SourceEventId)
                            && l.StartUtc >= input.FromUtc
                            && l.StartUtc < input.ToUtc)
                .ToList();

        return new SourceEventSyncPlan(items, missing, nonRoot, filtered, suppressed);
    }

    /// <summary>Кінець події: <c>NULL</c> чи сторожова дата — подія ще триває.</summary>
    /// <param name="end">Кінець із джерела.</param>
    public static DateTime? OpenEnd(DateTime? end)
        => end is { } value && value >= OpenEndSentinel ? null : end;

    /// <summary>Мілісекундна точність <c>datetime2(3)</c> сховища: пряме порівняння тиків розійшлося б із базою.</summary>
    /// <param name="value">Момент.</param>
    public static DateTime RoundToMillisecond(DateTime value)
        => new((value.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static (List<SourceEvent> Roots, int NonRoot, int Filtered) Select(SourceEventSyncInput input)
    {
        var roots = new List<SourceEvent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nonRoot = 0;
        var filtered = 0;

        foreach (var raw in input.Events)
        {
            // Повтор ID в одній відповіді — те саме джерельне поле двічі; перший виграє.
            if (!seen.Add(raw.EventId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(raw.ParentId))
            {
                nonRoot++;
                continue;
            }

            if (!PassesFilter(raw, input.FilterAttribute, input.FilterScope, input.FilterValue))
            {
                filtered++;
                continue;
            }

            roots.Add(raw with { EndUtc = OpenEnd(raw.EndUtc) });
        }

        return (roots, nonRoot, filtered);
    }

    private static bool PassesFilter(
        SourceEvent ev, string? attribute, SourceEventAttributeScope? scope, string? value)
    {
        if (attribute is null || scope is null || value is null)
        {
            return true;
        }

        var found = ev.Attributes.FirstOrDefault(a => a.Scope == scope
                                                      && string.Equals(a.Name, attribute, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            return false;
        }

        var wanted = value.Trim();
        if (found.ValueString is { } text)
        {
            return string.Equals(text.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
        }

        return found.ValueNumeric is { } number
               && (string.Equals(number.ToString(CultureInfo.InvariantCulture), wanted, StringComparison.Ordinal)
                   || (decimal.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                       && parsed == number));
    }

    /// <summary>
    /// Нові за ID події ↔ зв'язки, яких джерело за ID не повернуло, з однаковим природним ключем
    /// (початок до мілісекунди + назва без регістру) — лише пара «один до одного».
    /// </summary>
    private static Dictionary<string, SourceEventLinkState> MatchByNaturalKey(
        List<SourceEvent> roots, Dictionary<string, SourceEventLinkState> byId, HashSet<string> returned)
    {
        var fresh = roots.Where(e => !byId.ContainsKey(e.EventId)).ToList();
        var orphans = byId.Values.Where(l => !returned.Contains(l.SourceEventId)).ToList();
        var result = new Dictionary<string, SourceEventLinkState>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<SourceEventLinkState>();

        // Крок 1 — ПОВНИЙ ключ: початок до мс + первинний елемент (шаблон — сам мапінг). Лише пара
        // «один до одного» з обох боків; подія без елемента чи зв'язок без елемента в ньому не беруть участі.
        var freshFull = fresh
            .Where(e => ElementKey(e) is not null)
            .GroupBy(e => (RoundToMillisecond(e.StartUtc).Ticks, ElementKey(e)))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var orphanFull = orphans
            .Where(l => l.PrimaryElement is not null)
            .GroupBy(l => (RoundToMillisecond(l.StartUtc).Ticks, (string?)l.PrimaryElement))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        foreach (var (key, ev) in freshFull)
        {
            if (orphanFull.TryGetValue(key, out var link) && taken.Add(link))
            {
                result[ev.EventId] = link;
            }
        }

        // Крок 2 — слабкий ключ (початок до мс + назва без регістру) для решти: зв'язки, записані до M6
        // (елемент NULL), і події без елемента. Зв'язок із ІНШИМ елементом сюди не потрапляє.
        var freshWeak = fresh
            .Where(e => !result.ContainsKey(e.EventId))
            .GroupBy(e => NaturalKey(e.StartUtc, e.Name))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var orphanWeak = orphans.Where(l => !taken.Contains(l)).GroupBy(l => NaturalKey(l.StartUtc, l.EventName));
        foreach (var group in orphanWeak)
        {
            if (!freshWeak.TryGetValue(group.Key, out var ev))
            {
                continue;
            }

            var candidates = group
                .Where(l => l.PrimaryElement is null || ElementKey(ev) is null)
                .ToList();
            if (candidates.Count == 1)
            {
                result[ev.EventId] = candidates[0];
            }
        }

        return result;
    }

    private static string? ElementKey(SourceEvent ev) => SourceEventLink.NormalizeElement(ev.PrimaryElementPath);

    /// <summary>
    /// Елемент, який зв'язок запише (заповнення старих рядків м'яко, через синк): лише коли ключ
    /// «початок + елемент» однозначний серед подій прогону й серед інших зв'язків мапінгу —
    /// унікальний індекс <c>UX_SEL_NaturalKey</c> не має право впасти посеред збереження прогону.
    /// </summary>
    private static string? ElementToStore(
        SourceEvent ev, SourceEventLinkState? link, List<SourceEvent> roots, IReadOnlyList<SourceEventLinkState> links)
    {
        var element = ElementKey(ev);
        if (element is null)
        {
            return null;
        }

        var start = RoundToMillisecond(ev.StartUtc);
        var sameInRun = roots.Count(r => RoundToMillisecond(r.StartUtc) == start
                                         && string.Equals(ElementKey(r), element, StringComparison.Ordinal));
        var heldByOther = links.Any(l => !ReferenceEquals(l, link)
                                         && string.Equals(l.PrimaryElement, element, StringComparison.Ordinal)
                                         && RoundToMillisecond(l.StartUtc) == start);
        return sameInRun == 1 && !heldByOther ? element : null;
    }

    private static (long Start, string Name) NaturalKey(DateTime startUtc, string? name)
    {
        var trimmed = name is null ? string.Empty : name.Length > MaxEventNameLength ? name[..MaxEventNameLength] : name;
        return (RoundToMillisecond(startUtc).Ticks, trimmed.Trim().ToUpperInvariant());
    }

    private static SourceEventPlanItem Decide(
        SourceEvent ev, SourceEventLinkState? link, Func<DateTime, SourceEventPeriodTarget?> periodOf)
    {
        var rowKey = link?.RowKey ?? IntegrationRowUpsert.EventRowKey(ev.EventId);
        var hasRow = link is { HasRow: true };

        if (ev.EndUtc is null)
        {
            // Рядок є, а PI знову показує подію відкритою: «знову триває» синхронізація виразити
            // не може (Missing → Open заборонено) — рядок і зв'язок лишаються як є.
            return new SourceEventPlanItem(
                ev, hasRow ? SourceEventAction.KeepAsIs : SourceEventAction.Open, link, rowKey, null);
        }

        var target = periodOf(ev.StartUtc);

        if (hasRow && (target is null || target.PeriodKey != link!.PeriodKey))
        {
            return new SourceEventPlanItem(ev, SourceEventAction.PeriodChanged, link, rowKey, target);
        }

        if (target is null || target.TableInstanceId is null || target.State == PeriodState.Scheduled)
        {
            return new SourceEventPlanItem(
                ev, hasRow ? SourceEventAction.KeepAsIs : SourceEventAction.PeriodNotOpen, link, rowKey, target);
        }

        return target.State is PeriodState.Open or PeriodState.Grace
            ? new SourceEventPlanItem(ev, SourceEventAction.Write, link, rowKey, target)
            : new SourceEventPlanItem(ev, SourceEventAction.PeriodClosed, link, rowKey, target);
    }
}
