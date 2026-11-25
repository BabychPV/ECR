// src/Ecr.Application/GasComposition/GasCompositionRules.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.GasComposition;

/// <summary>Версія кейсу на шкалі часу: запис <c>STREAM_CASE</c> і його вікно <c>[From, To)</c>.</summary>
/// <param name="Id">Запис версії.</param>
/// <param name="From">Перший день дії (1-ше число місяця).</param>
/// <param name="To">Перший день, коли версія вже не діє; <c>null</c> — чинна.</param>
public sealed record GasVersionSlot(long Id, DateOnly From, DateOnly? To)
{
    /// <summary>Вікно дії.</summary>
    public ValidityWindow Window => new(From, To);
}

/// <summary>Нове вікно наявної версії, яке тягне за собою операція.</summary>
/// <param name="Id">Запис версії.</param>
/// <param name="Window">Нове вікно.</param>
public sealed record GasVersionAdjustment(long Id, ValidityWindow Window);

/// <summary>Діапазон дат, на яких операція змінює чинний склад: <c>[From, To)</c>, <c>To = null</c> — без кінця.</summary>
/// <param name="From">Початок, включно.</param>
/// <param name="To">Кінець, виключно; <c>null</c> — відкритий.</param>
public sealed record GasDateRange(DateOnly From, DateOnly? To)
{
    /// <summary>Чи перетинається з <c>[start, endExclusive)</c>.</summary>
    /// <param name="start">Початок.</param>
    /// <param name="endExclusive">Кінець, виключно.</param>
    public bool Overlaps(DateOnly start, DateOnly endExclusive)
        => From < endExclusive && (To is not { } to || start < to);
}

/// <summary>План зміни шкали версій одного кейсу.</summary>
/// <param name="NewWindow">Вікно нової/зміненої версії; <c>null</c> — операція її не має (видалення).</param>
/// <param name="Adjustments">Сусідні версії, що змінюють вікно (обрізання, продовження).</param>
/// <param name="Affected">Дати, на яких змінюється чинний склад.</param>
public sealed record GasTimelinePlan(ValidityWindow? NewWindow, IReadOnlyList<GasVersionAdjustment> Adjustments, GasDateRange Affected);

/// <summary>Період, що замкнув склад газу: його результат читав склад, а період закритий або аркуш затверджений.</summary>
/// <param name="PeriodKey">Ключ періоду (<c>yyyyMM</c>; <c>yyyy00</c> — рік).</param>
/// <param name="Reason"><c>closed</c> або <c>approved</c>.</param>
/// <param name="PeriodStart">
/// ✎ D4 (REVIEW-sg-flaring): перший день періоду з <c>doc.Period.PeriodStart</c>; <c>null</c> — рахувати з ключа (як досі).
/// </param>
/// <param name="PeriodEnd">✎ D4: останній день періоду з <c>doc.Period.PeriodEnd</c> (включно); <c>null</c> — з ключа.</param>
public sealed record GasLockedPeriod(int PeriodKey, string Reason, DateOnly? PeriodStart = null, DateOnly? PeriodEnd = null)
{
    /// <summary>Причина: період закритий.</summary>
    public const string Closed = "closed";

    /// <summary>Причина: аркуш затверджений.</summary>
    public const string Approved = "approved";

    /// <summary>
    /// Перший день періоду: з <see cref="PeriodStart"/>, якщо він відомий (квартал, довільний період — ключ не <c>yyyyMM</c>);
    /// інакше місяць <c>1..12</c> — цей місяць, решта — увесь рік (обережно).
    /// </summary>
    public DateOnly Start => PeriodStart
        ?? (Month is { } m ? new DateOnly(PeriodKey / 100, m, 1) : new DateOnly(PeriodKey / 100, 1, 1));

    /// <summary>Перший день після періоду.</summary>
    public DateOnly EndExclusive => PeriodEnd is { } end
        ? end.AddDays(1)
        : Month is not null ? Start.AddMonths(1) : Start.AddYears(1);

    private int? Month => PeriodKey % 100 is var m and >= 1 and <= 12 ? m : null;

    /// <summary>Підпис періоду для тексту відмови: <c>2026-03</c> або <c>2026</c>.</summary>
    public string Label => Month is { } m
        ? string.Create(CultureInfo.InvariantCulture, $"{PeriodKey / 100:D4}-{m:D2}")
        : (PeriodKey / 100).ToString("D4", CultureInfo.InvariantCulture);
}

/// <summary>
/// Правила шкали версій складу газу (D-NEW, людина 24.11): версія діє з 1-го числа місяця до початку наступної, вікна
/// кейсу не перетинаються і не мають дірок усередині; історія, яку прочитав закритий або затверджений період, незмінна.
/// </summary>
/// <remarks>
/// ⚠ Чисті функції: обробник читає версії кейсу й замкнені періоди, правила кажуть, що змінити і чи можна.
/// <para>
/// ⛔ FLERT версіонував за часом НАТИСКАННЯ «Зберегти» (<c>GETDATE()</c>) і з'єднував таблиці за рівним <c>ValidFrom</c>;
/// тут — дата ДІЇ (місяць), шкала одного кейсу, а сусіди змінюються в тій самій транзакції.
/// </para>
/// </remarks>
public static class GasCompositionRules
{
    /// <summary>Допуск Σ мол.% навколо 100 (той самий, що правило <c>SUM_100</c>).</summary>
    public const decimal SumTolerance = 0.5m;

    /// <summary>Ключ: дата дії — не 1-ше число місяця.</summary>
    public const string NotMonthStartKey = "err.ECR-REG-0422.gasCompositionMonthStart";

    /// <summary>Ключ: версія з цієї дати вже є.</summary>
    public const string DateTakenKey = "err.ECR-REG-0409.gasCompositionDateTaken";

    /// <summary>Ключ: нова дата виходить за сусідні версії.</summary>
    public const string DateOutOfRangeKey = "err.ECR-REG-0422.gasCompositionDateOutOfRange";

    /// <summary>Ключ: операція змінює склад у замкненому періоді.</summary>
    public const string LockedKey = "err.ECR-REG-0409.gasCompositionLocked";

    /// <summary>Перевіряє, що дата — 1-ше число місяця.</summary>
    /// <param name="from">Дата дії.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, <see cref="NotMonthStartKey"/>.</exception>
    public static void RequireMonthStart(DateOnly from)
    {
        if (from.Day != 1)
        {
            throw Rule(ErrorCodes.RegistrySwitchInOpenPeriod, NotMonthStartKey,
                $"Версія складу діє з 1-го числа місяця, а не з {Iso(from)}.", ("validFrom", Iso(from)));
        }
    }

    /// <summary>Нова версія кейсу з дати <paramref name="from"/>.</summary>
    /// <param name="versions">Наявні версії кейсу (будь-який порядок).</param>
    /// <param name="from">Дата дії нової версії.</param>
    /// <remarks>
    /// Раніше за першу — діє до початку першої. Усередині версії V — V обрізається до <paramref name="from"/>, нова діє до
    /// старого кінця V. Дата збігається з початком наявної — відмова (змінюйте ту версію).
    /// </remarks>
    public static GasTimelinePlan PlanInsert(IReadOnlyList<GasVersionSlot> versions, DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(versions);
        RequireMonthStart(from);

        var ordered = versions.OrderBy(v => v.From).ToList();
        if (ordered.Exists(v => v.From == from))
        {
            throw Rule(ErrorCodes.RegistryEntryInUse, DateTakenKey,
                $"Версія з {Iso(from)} уже є: змініть її, а не додавайте другу.", ("validFrom", Iso(from)));
        }

        if (ordered.Count == 0)
        {
            return new GasTimelinePlan(new ValidityWindow(from, null), [], new GasDateRange(from, null));
        }

        if (from < ordered[0].From)
        {
            var to = ordered[0].From;
            return new GasTimelinePlan(new ValidityWindow(from, to), [], new GasDateRange(from, to));
        }

        // Остання версія, що почалася раніше; вона або містить дату, або закінчилась раніше (дірка в історії).
        var host = ordered.Last(v => v.From < from);
        var next = ordered.FirstOrDefault(v => v.From > from);
        if (host.To is { } hostTo && hostTo <= from)
        {
            // Дірка (лише в даних, внесених повз правила): нова версія закриває її до наступної.
            var gapTo = next?.From;
            return new GasTimelinePlan(new ValidityWindow(from, gapTo), [], new GasDateRange(from, gapTo));
        }

        return new GasTimelinePlan(
            new ValidityWindow(from, host.To),
            [new GasVersionAdjustment(host.Id, new ValidityWindow(host.From, from))],
            new GasDateRange(from, host.To));
    }

    /// <summary>Зміна значень версії без зміни дати: змінюється склад на всьому її вікні.</summary>
    /// <param name="version">Версія.</param>
    public static GasDateRange AffectedByEdit(GasVersionSlot version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new GasDateRange(version.From, version.To);
    }

    /// <summary>Перенесення початку версії на <paramref name="newFrom"/> (між сусідами).</summary>
    /// <param name="versions">Версії кейсу.</param>
    /// <param name="id">Версія, що переноситься.</param>
    /// <param name="newFrom">Нова дата дії.</param>
    /// <param name="valuesChanged">Чи змінюються ще й значення (тоді зачеплене — усе нове вікно).</param>
    /// <remarks>
    /// Попередня версія (якщо вона закінчувалась на старому початку) тягнеться/обрізається до нової дати; нова дата мусить
    /// лишатися строго між початком попередньої і початком наступної.
    /// </remarks>
    public static GasTimelinePlan PlanMove(IReadOnlyList<GasVersionSlot> versions, long id, DateOnly newFrom, bool valuesChanged)
    {
        ArgumentNullException.ThrowIfNull(versions);
        RequireMonthStart(newFrom);

        var ordered = versions.OrderBy(v => v.From).ToList();
        var index = ordered.FindIndex(v => v.Id == id);
        if (index < 0)
        {
            throw new ArgumentException($"Версії {id} немає серед версій кейсу.", nameof(id));
        }

        var self = ordered[index];
        var previous = index > 0 ? ordered[index - 1] : null;
        var next = index + 1 < ordered.Count ? ordered[index + 1] : null;

        if ((previous is not null && newFrom <= previous.From) || (next is not null && newFrom >= next.From)
            || (self.To is { } selfTo && newFrom >= selfTo))
        {
            throw Rule(ErrorCodes.RegistrySwitchInOpenPeriod, DateOutOfRangeKey,
                $"Нова дата {Iso(newFrom)} мусить лежати між сусідніми версіями.",
                ("validFrom", Iso(newFrom)),
                ("after", previous is null ? string.Empty : Iso(previous.From)),
                ("before", next is null ? string.Empty : Iso(next.From)));
        }

        var adjustments = new List<GasVersionAdjustment>();
        if (previous is not null && previous.To == self.From)
        {
            adjustments.Add(new GasVersionAdjustment(previous.Id, new ValidityWindow(previous.From, newFrom)));
        }

        var low = newFrom < self.From ? newFrom : self.From;
        var high = newFrom < self.From ? self.From : newFrom;
        var affected = valuesChanged
            ? new GasDateRange(low, self.To)
            : new GasDateRange(low, high);

        return new GasTimelinePlan(new ValidityWindow(newFrom, self.To), adjustments, affected);
    }

    /// <summary>Видалення версії: попередня (якщо стикується) тягнеться на її вікно; склад змінюється на всьому вікні.</summary>
    /// <param name="versions">Версії кейсу.</param>
    /// <param name="id">Версія.</param>
    public static GasTimelinePlan PlanDelete(IReadOnlyList<GasVersionSlot> versions, long id)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var ordered = versions.OrderBy(v => v.From).ToList();
        var index = ordered.FindIndex(v => v.Id == id);
        if (index < 0)
        {
            throw new ArgumentException($"Версії {id} немає серед версій кейсу.", nameof(id));
        }

        var self = ordered[index];
        var previous = index > 0 ? ordered[index - 1] : null;
        var adjustments = previous is not null && previous.To == self.From
            ? new[] { new GasVersionAdjustment(previous.Id, new ValidityWindow(previous.From, self.To)) }
            : [];

        return new GasTimelinePlan(null, adjustments, new GasDateRange(self.From, self.To));
    }

    /// <summary>
    /// Відмовляє, якщо зачеплені дати перетинають замкнений період: склад, який прочитав закритий чи затверджений період,
    /// не змінюється — виправлення історії робиться новою версією з першого відкритого місяця.
    /// </summary>
    /// <param name="affected">Зачеплені дати.</param>
    /// <param name="locked">Замкнені періоди.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0409</c>, <see cref="LockedKey"/>.</exception>
    public static void RequireUnlocked(GasDateRange affected, IReadOnlyList<GasLockedPeriod> locked)
    {
        ArgumentNullException.ThrowIfNull(affected);
        ArgumentNullException.ThrowIfNull(locked);

        var hits = locked
            .Where(p => affected.Overlaps(p.Start, p.EndExclusive))
            .OrderBy(p => p.PeriodKey)
            .ToList();
        if (hits.Count == 0)
        {
            return;
        }

        var periods = string.Join(", ", hits.Select(p => p.Label).Distinct(StringComparer.Ordinal));
        var firstFree = hits.Max(p => p.EndExclusive);
        throw Rule(ErrorCodes.RegistryEntryInUse, LockedKey,
            $"Склад газу в періодах {periods} уже використано закритим або затвердженим розрахунком — він незмінний. "
            + $"Додайте нову версію з {Iso(firstFree)}.",
            ("periods", periods),
            ("firstOpen", Iso(firstFree)));
    }

    /// <summary>Чи зачіпає діапазон хоч один замкнений період (для позначки «замкнено» в інтерфейсі).</summary>
    /// <param name="affected">Діапазон.</param>
    /// <param name="locked">Замкнені періоди.</param>
    public static IReadOnlyList<GasLockedPeriod> LocksOf(GasDateRange affected, IReadOnlyList<GasLockedPeriod> locked)
    {
        ArgumentNullException.ThrowIfNull(affected);
        ArgumentNullException.ThrowIfNull(locked);
        return [.. locked.Where(p => affected.Overlaps(p.Start, p.EndExclusive)).OrderBy(p => p.PeriodKey)];
    }

    /// <summary>Роки, які зачіпає діапазон, обмежені відомими роками (відкритий кінець — до <paramref name="lastYear"/>).</summary>
    /// <param name="affected">Діапазон.</param>
    /// <param name="lastYear">Останній рік, що має сенс (наприклад, найпізніший рік періодів).</param>
    public static IReadOnlyList<int> YearsOf(GasDateRange affected, int lastYear)
    {
        ArgumentNullException.ThrowIfNull(affected);
        var last = affected.To is { } to ? to.AddDays(-1).Year : Math.Max(lastYear, affected.From.Year);
        return [.. Enumerable.Range(affected.From.Year, Math.Max(0, last - affected.From.Year + 1))];
    }

    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static BusinessRuleException Rule(string code, string messageKey, string message, params (string Key, string Value)[] parameters)
    {
        var details = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (key, value) in parameters)
        {
            details[key] = value;
        }

        return new BusinessRuleException(code, message, details);
    }
}
