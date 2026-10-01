// tests/Ecr.Application.Tests/Integration/SourceEventSyncPlannerTests.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// План синхронізації подій (HSE301 A5b, FEATURE-HSE301-VIEW §4.7.4): що створити, що оновити,
/// що лише позначити.
/// </summary>
/// <remarks>
/// Мутаційні докази (A5b), кожен — точковою правкою <c>SourceEventSyncPlanner</c>:
/// <list type="bullet">
/// <item>закритий період: гілку <c>PeriodState.Closed</c> у <c>Decide</c> замінити на <c>Write</c> —
/// червоніє <see cref="Закритий_період_не_пишеться_а_відкладений_чекає"/>;</item>
/// <item><c>Missing</c> без <c>Truncated</c>: прибрати <c>suppressed</c> з умови — червоніє
/// <see cref="Missing_лише_при_повному_читанні"/>;</item>
/// <item>природний ключ: <c>MatchByNaturalKey</c> повертає порожній словник — червоніє
/// <see cref="Перестворена_подія_лягає_у_рядок_попередньої"/>;</item>
/// <item>кореневі: прибрати перевірку <c>ParentId</c> — червоніє <see cref="Дочірня_подія_не_береться"/>.</item>
/// </list>
/// </remarks>
public sealed class SourceEventSyncPlannerTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");
    private static readonly DateTime From = new(2026, 1, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);

    private static readonly SourceEventPeriod January = new(
        202601, PeriodState.Open, Period.UtcBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Atyrau));

    private static readonly SourceEventPeriod February = new(
        202602, PeriodState.Open, Period.UtcBounds(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), Atyrau));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Нова_завершена_подія_у_відкритому_періоді_створює_рядок_з_ключем_EF_id()
    {
        var plan = Plan([Ev("E1", Start, Start.AddMinutes(15))], []);

        var item = Assert.Single(plan.Items);
        Assert.Equal((SourceEventAction.Write, true, "EF-E1"), (item.Action, item.IsCreate, item.RowKey));
        Assert.Equal((202601, 10L), (item.Target!.PeriodKey, item.Target.TableInstanceId!.Value));
        Assert.Empty(plan.Missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Наявний_зв_язок_із_рядком_оновлюється_за_його_ключем_рядка()
    {
        var link = Linked("E1", "EF-first-key");

        var item = Assert.Single(Plan([Ev("E1", Start, Start.AddMinutes(15))], [link]).Items);

        Assert.Equal((SourceEventAction.Write, false, "EF-first-key", false), (item.Action, item.IsCreate, item.RowKey, item.IsRekey));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData(false)]
    [InlineData(true)]
    public void Незакрита_подія_не_рахується_а_сторожова_дата_це_теж_незакрита(bool sentinel)
    {
        DateTime? end = sentinel ? new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc) : null;

        var plan = Plan([Ev("E1", Start, end)], []);

        var item = Assert.Single(plan.Items);
        Assert.Equal((SourceEventAction.Open, (DateTime?)null), (item.Action, item.Event.EndUtc));
        Assert.True(item.IsCreate is false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Незакрита_подія_з_рядком_лишає_рядок_як_є()
    {
        var item = Assert.Single(Plan([Ev("E1", Start, null)], [Linked("E1", "EF-E1")]).Items);

        Assert.Equal(SourceEventAction.KeepAsIs, item.Action);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData(PeriodState.Closed, SourceEventAction.PeriodClosed)]
    [InlineData(PeriodState.Scheduled, SourceEventAction.PeriodNotOpen)]
    [InlineData(PeriodState.Grace, SourceEventAction.Write)]
    public void Закритий_період_не_пишеться_а_відкладений_чекає(PeriodState state, SourceEventAction expected)
    {
        var periods = new[] { January with { State = state } };

        var plan = SourceEventSyncPlanner.Plan(Input([Ev("E1", Start, Start.AddMinutes(15))], [], periods));

        Assert.Equal(expected, Assert.Single(plan.Items).Action);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Період_без_екземпляра_таблиці_чи_невідомий_період_чекає_наступного_прогону()
    {
        var noInstance = SourceEventSyncPlanner.Plan(Input(
            [Ev("E1", Start, Start.AddMinutes(15))], [], [January], instances: new Dictionary<int, long>()));
        var noPeriod = SourceEventSyncPlanner.Plan(Input(
            [Ev("E2", new DateTime(2027, 5, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 5, 1, 1, 0, 0, DateTimeKind.Utc))], [], [January]));

        Assert.Equal(SourceEventAction.PeriodNotOpen, Assert.Single(noInstance.Items).Action);
        Assert.Equal(SourceEventAction.PeriodNotOpen, Assert.Single(noPeriod.Items).Action);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData("2026-01-31T18:59:59Z", 202601)]
    [InlineData("2026-01-31T19:00:00Z", 202602)]
    [InlineData("2026-02-28T18:59:59Z", 202602)]
    public void Місяць_події_за_поясом_проєкту_а_не_за_UTC_датою(string startUtc, int expectedPeriod)
    {
        var start = DateTime.Parse(startUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

        var plan = SourceEventSyncPlanner.Plan(Input(
            [Ev("E1", start, start.AddMinutes(5))], [], [January, February]));

        Assert.Equal(expectedPeriod, Assert.Single(plan.Items).Target!.PeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Початок_переїхав_в_інший_місяць_позначається_і_не_переноситься()
    {
        var link = Linked("E1", "EF-E1", periodKey: 202601);
        var moved = new DateTime(2026, 2, 3, 6, 0, 0, DateTimeKind.Utc);

        var item = Assert.Single(SourceEventSyncPlanner.Plan(Input(
            [Ev("E1", moved, moved.AddMinutes(10))], [link], [January, February])).Items);

        Assert.Equal(SourceEventAction.PeriodChanged, item.Action);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Дочірня_подія_не_береться()
    {
        var plan = Plan([Ev("E1", Start, Start.AddMinutes(15)), Ev("E2", Start, Start.AddMinutes(20), parent: "E1")], []);

        Assert.Equal(["E1"], plan.Items.Select(i => i.Event.EventId));
        Assert.Equal(1, plan.SkippedNonRoot);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(false, "ECR-INT-0503", false)]
    public void Missing_лише_при_повному_читанні(bool truncated, string? errorCode, bool expectMissing)
    {
        var lost = Linked("GONE", "EF-GONE");

        var plan = SourceEventSyncPlanner.Plan(Input([], [lost], [January], truncated, errorCode));

        Assert.Equal(expectMissing ? ["GONE"] : [], plan.Missing.Select(m => m.SourceEventId));
        Assert.Equal(!expectMissing, plan.MissingSuppressed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Missing_не_ставиться_зв_язку_без_рядка_поза_вікном_і_вже_позначеному()
    {
        var noRow = new SourceEventLinkState("OPEN", "Flaring", Start, SourceEventLinkStatus.Open, null, null, null);
        var outside = Linked("OLD", "EF-OLD", start: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        var already = Linked("MISSED", "EF-MISSED", status: SourceEventLinkStatus.Missing);
        var seen = Linked("SEEN", "EF-SEEN");

        var plan = Plan([Ev("SEEN", Start, Start.AddMinutes(15))], [noRow, outside, already, seen]);

        Assert.Empty(plan.Missing);
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ (повна звірка): прибрати <c>suppressed</c> з умови <c>gone</c> — червоніє
    /// кейс «обрізане/з відмовою»; прибрати <c>l.StartUtc &gt;= input.FromUtc</c> — червоніє «поза вікном»;
    /// <c>!suppressed &amp;&amp;</c> із <c>SourceEmpty</c> — червоніє кейс порожньої відповіді.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public void Повна_звірка_Gone_містить_зниклі_включно_з_уже_Missing_і_без_рядка_але_не_повернені_й_не_поза_вікном()
    {
        var gone = Linked("GONE", "EF-GONE");
        var already = Linked("MISSED", "EF-MISSED", status: SourceEventLinkStatus.Missing);
        var noRow = new SourceEventLinkState("OPEN", "Flaring", Start, SourceEventLinkStatus.Open, null, null, null);
        var closedHistory = new SourceEventLinkState("CL", "Flaring", Start, SourceEventLinkStatus.PeriodClosed, null, null, null);
        var outside = Linked("OLD", "EF-OLD", start: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        var seen = Linked("SEEN", "EF-SEEN");

        var plan = Plan([Ev("SEEN", Start, Start.AddMinutes(15))], [gone, already, noRow, closedHistory, outside, seen]);

        Assert.Equal(["GONE", "MISSED", "OPEN"], plan.Gone!.Select(g => g.SourceEventId).Order(StringComparer.Ordinal));
        Assert.False(plan.SourceEmpty);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [InlineData(true, null)]
    [InlineData(false, "ECR-INT-0503")]
    public void Повна_звірка_обрізане_чи_відмовне_читання_нічого_не_видаляє(bool truncated, string? errorCode)
    {
        var plan = SourceEventSyncPlanner.Plan(Input([], [Linked("GONE", "EF-GONE")], [January], truncated, errorCode));

        Assert.Empty(plan.Gone!);
        Assert.False(plan.SourceEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public void Повна_звірка_порожня_відповідь_ставить_SourceEmpty_а_непорожня_ні()
    {
        var lost = Linked("GONE", "EF-GONE");

        Assert.True(Plan([], [lost]).SourceEmpty);
        Assert.False(Plan([Ev("OTHER", Start.AddHours(1), Start.AddHours(2), name: "Other")], [lost]).SourceEmpty);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public void Повна_звірка_перестворена_подія_не_потрапляє_в_Gone()
    {
        var old = Linked("OLD-ID", "EF-OLD-ID", name: "Flaring HP");

        var plan = Plan([Ev("NEW-ID", Start, Start.AddMinutes(15), name: "flaring hp")], [old]);

        Assert.Empty(plan.Gone!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public void Братній_шаблон_подія_переїхала_Handoff_а_не_Gone_і_не_Missing_а_порожність_рахується_по_об_єднанню()
    {
        var moved = Linked("MOVED", "EF-MOVED");
        var gone = Linked("GONE", "EF-GONE");
        var baseInput = Input([Ev("KEEP", Start.AddHours(1), Start.AddHours(2), name: "Keep")], [moved, gone], [January]);

        var plan = SourceEventSyncPlanner.Plan(baseInput with { OtherTemplateIds = new HashSet<string> { "MOVED" } });

        Assert.Equal(["MOVED"], plan.HandedOff!.Select(h => h.SourceEventId));
        Assert.Equal(["GONE"], plan.Gone!.Select(g => g.SourceEventId));
        Assert.Equal(["GONE"], plan.Missing.Select(m => m.SourceEventId));

        // Власних подій нуль, але братній шаблон щось віддав — це не «джерело порожнє».
        var emptyOwn = SourceEventSyncPlanner.Plan(
            Input([], [moved], [January]) with { OtherTemplateIds = new HashSet<string> { "X" } });
        Assert.False(emptyOwn.SourceEmpty);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [InlineData("P_Auto", "P_Auto_Day", true)]
    [InlineData("P_Auto_Day", "P_Manual", true)]
    [InlineData("P_Manual", "P_Manual_Day", true)]
    [InlineData("P_Manual_Day", "P_Auto", false)]
    [InlineData("P_Manual", "P_Auto", false)]
    public void Порядок_шаблонів_Auto_AutoDay_Manual_ManualDay(string winner, string other, bool outranks)
        => Assert.Equal(outranks, SourceEventTemplateOrder.Outranks(winner, other));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Перестворена_подія_лягає_у_рядок_попередньої()
    {
        var old = Linked("OLD-ID", "EF-OLD-ID", name: "Flaring HP");

        var plan = Plan([Ev("NEW-ID", Start, Start.AddMinutes(15), name: "flaring hp")], [old]);

        var item = Assert.Single(plan.Items);
        Assert.Equal((true, false, "EF-OLD-ID"), (item.IsRekey, item.IsCreate, item.RowKey));
        Assert.Equal("OLD-ID", item.Link!.SourceEventId);
        Assert.Empty(plan.Missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Однакові_за_природним_ключем_події_не_зіставляються_щоб_не_злити_різні()
    {
        var old = Linked("OLD-ID", "EF-OLD-ID");

        var plan = Plan(
            [Ev("NEW-1", Start, Start.AddMinutes(15)), Ev("NEW-2", Start, Start.AddMinutes(20))], [old]);

        Assert.All(plan.Items, i => Assert.Equal((true, "EF-" + i.Event.EventId), (i.IsCreate, i.RowKey)));
        Assert.Equal(["OLD-ID"], plan.Missing.Select(m => m.SourceEventId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Інша_назва_чи_початок_не_є_тією_самою_подією()
    {
        var old = Linked("OLD-ID", "EF-OLD-ID", name: "Flaring HP");

        var plan = Plan(
            [Ev("A", Start, Start.AddMinutes(15), name: "Flaring LP"), Ev("B", Start.AddSeconds(1), Start.AddMinutes(15), name: "Flaring HP")],
            [old]);

        Assert.All(plan.Items, i => Assert.True(i.IsCreate));
        Assert.Equal(["OLD-ID"], plan.Missing.Select(m => m.SourceEventId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Звуження_мапінгу_відкидає_події_чужої_ділянки_і_вони_вважаються_відсутніми()
    {
        var mine = Ev("E1", Start, Start.AddMinutes(15), attrs: [Attribute("Flare", "HP")]);
        var other = Ev("E2", Start.AddHours(1), Start.AddHours(2), attrs: [Attribute("Flare", "LP")]);
        var noAttribute = Ev("E3", Start.AddHours(3), Start.AddHours(4));
        var wasMine = Linked("E2", "EF-E2", start: Start.AddHours(1));

        var plan = SourceEventSyncPlanner.Plan(Input([mine, other, noAttribute], [wasMine], [January])
            with { FilterAttribute = "flare", FilterScope = SourceEventAttributeScope.Event, FilterValue = " hp " });

        Assert.Equal(["E1"], plan.Items.Select(i => i.Event.EventId));
        Assert.Equal(2, plan.Filtered);
        Assert.Equal(["E2"], plan.Missing.Select(m => m.SourceEventId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5b")]
    public void Повтор_ID_у_відповіді_бере_першу_подію()
    {
        var plan = Plan(
            [Ev("E1", Start, Start.AddMinutes(15), name: "first"), Ev("e1", Start, Start.AddMinutes(99), name: "second")], []);

        Assert.Equal("first", Assert.Single(plan.Items).Event.Name);
    }

    // ── Повний природний ключ (HSE301 M6) ────────────────────────────────────
    // Мутації: (а) крок 1 MatchByNaturalKey прибрати — червоніє Повний_ключ_зіставляє_за_елементом_при_різних_назвах;
    // (б) у кроці 2 прибрати фільтр candidates (l.PrimaryElement is null || …) — червоніє Зв_язок_з_іншим_елементом_не_зіставляється_слабким_ключем;
    // (в) ElementToStore завжди повертає element — червоніє Двозначний_ключ_елемента_не_записується.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Повний_ключ_розрізняє_події_з_однаковими_початком_і_назвою_на_різних_елементах()
    {
        var onA = Linked("OLD-A", "EF-OLD-A", element: "FLARE A");
        var onB = Linked("OLD-B", "EF-OLD-B", element: "FLARE B");

        // Обидві події перестворені з новими ID; без елемента в ключі вони були б двозначні й не зіставились.
        var plan = Plan(
            [Ev("NEW-B", Start, Start.AddMinutes(15), element: " flare b "), Ev("NEW-A", Start, Start.AddMinutes(15), element: "Flare A")],
            [onA, onB]);

        Assert.Equal(
            [("NEW-B", "EF-OLD-B", true), ("NEW-A", "EF-OLD-A", true)],
            plan.Items.Select(i => (i.Event.EventId, i.RowKey, i.IsRekey)));
        Assert.Empty(plan.Missing);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Повний_ключ_зіставляє_за_елементом_при_різних_назвах()
    {
        var old = Linked("OLD", "EF-OLD", name: "Old name", element: "FLARE A");

        var item = Assert.Single(Plan([Ev("NEW", Start, Start.AddMinutes(15), name: "Renamed", element: "flare a")], [old]).Items);

        Assert.Equal((true, false, "EF-OLD"), (item.IsRekey, item.IsCreate, item.RowKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Інший_елемент_не_є_тією_самою_подією_навіть_з_однаковою_назвою()
    {
        var old = Linked("OLD", "EF-OLD", element: "FLARE A");

        var plan = Plan([Ev("NEW", Start, Start.AddMinutes(15), element: "Flare B")], [old]);

        Assert.True(Assert.Single(plan.Items).IsCreate);
        Assert.Equal(["OLD"], plan.Missing.Select(m => m.SourceEventId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Старий_зв_язок_без_елемента_зіставляється_слабким_ключем_і_отримує_елемент()
    {
        var old = Linked("OLD", "EF-OLD");

        var item = Assert.Single(Plan([Ev("NEW", Start, Start.AddMinutes(15), element: "Flare A")], [old]).Items);

        Assert.Equal((true, "EF-OLD", "FLARE A"), (item.IsRekey, item.RowKey, item.ElementToStore));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Зв_язок_з_іншим_елементом_не_зіставляється_слабким_ключем()
    {
        // Той самий початок і назва, але елементи відомі й різні: слабкий ключ тут злив би різні події.
        var old = Linked("OLD", "EF-OLD", element: "FLARE A");

        var item = Assert.Single(Plan([Ev("NEW", Start, Start.AddMinutes(15), element: "Flare B")], [old]).Items);

        Assert.False(item.IsRekey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Два_старі_зв_язки_без_елемента_на_двох_нових_подіях_лишаються_двозначними()
    {
        var plan = Plan(
            [Ev("N1", Start, Start.AddMinutes(15), element: "A"), Ev("N2", Start, Start.AddMinutes(15), element: "B")],
            [Linked("O1", "EF-O1")]);

        Assert.All(plan.Items, i => Assert.True(i.IsCreate));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Двозначний_ключ_елемента_не_записується()
    {
        // Дві події одного елемента з одним початком (різні назви): унікальний індекс не витримав би обох.
        var plan = Plan(
            [Ev("E1", Start, Start.AddMinutes(15), name: "a", element: "FLARE A"),
             Ev("E2", Start, Start.AddMinutes(20), name: "b", element: "FLARE A"),
             Ev("E3", Start.AddHours(1), Start.AddHours(2), element: "FLARE A")],
            []);

        Assert.Equal([null, null, "FLARE A"], plan.Items.Select(i => i.ElementToStore));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-M6")]
    public void Ключ_яким_уже_володіє_інший_зв_язок_не_записується()
    {
        var other = Linked("OTHER", "EF-OTHER", element: "FLARE A");

        // OTHER повернувся за ID з новим початком, але в базі ще тримає (Start, FLARE A).
        var plan = Plan(
            [Ev("E1", Start, Start.AddMinutes(15), element: "Flare A"),
             Ev("OTHER", Start.AddHours(1), Start.AddHours(2), element: "Flare A")],
            [other]);

        Assert.Equal([null, "FLARE A"], plan.Items.Select(i => i.ElementToStore));
    }

    // ── Помічники ────────────────────────────────────────────────────────────

    private static SourceEventSyncPlan Plan(IReadOnlyList<SourceEvent> events, IReadOnlyList<SourceEventLinkState> links)
        => SourceEventSyncPlanner.Plan(Input(events, links, [January]));

    private static SourceEventSyncInput Input(
        IReadOnlyList<SourceEvent> events,
        IReadOnlyList<SourceEventLinkState> links,
        IReadOnlyList<SourceEventPeriod> periods,
        bool truncated = false,
        string? errorCode = null,
        Dictionary<int, long>? instances = null)
    {
        instances ??= periods.ToDictionary(p => p.PeriodKey, p => (long)(p.PeriodKey == 202601 ? 10 : 20));

        return new SourceEventSyncInput(
            From,
            To,
            events,
            truncated,
            errorCode,
            links,
            start =>
            {
                var period = SourceEventPeriods.Locate(start, periods);
                return period is null
                    ? null
                    : new SourceEventPeriodTarget(
                        period.PeriodKey, period.State, instances.TryGetValue(period.PeriodKey, out var id) ? id : null);
            });
    }

    private static SourceEvent Ev(
        string id,
        DateTime start,
        DateTime? end,
        string? name = "Flaring",
        string? parent = null,
        IReadOnlyList<SourceEventAttribute>? attrs = null,
        string? element = null)
        => new(id, "FlareEvent", name, start, end, null, element, parent, attrs ?? []);

    private static SourceEventAttribute Attribute(string name, string value)
        => new(name, SourceEventAttributeScope.Event, null, value, null);

    private static SourceEventLinkState Linked(
        string id,
        string rowKey,
        int periodKey = 202601,
        DateTime? start = null,
        string? name = "Flaring",
        SourceEventLinkStatus status = SourceEventLinkStatus.Synced,
        string? element = null)
        => new(id, name, start ?? Start, status, periodKey, 10, rowKey, element);
}
