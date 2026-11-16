// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.B79.cs
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

// ✎ B79: нічний перерахунок пише подані аркуші; позначка «перераховано після подання» — лише за зміною чисел.
// Справжня задача RecalculationJob зі СПРАВЖНІМ сервісом формул (річна розкладка): шлях запису → набір змін → журнал.
public sealed partial class YearLayoutTailSqlTests
{
    /// <summary>
    /// Нічна задача року (без людини): поданий квітень перераховано й позначено, чернетковий травень перераховано без
    /// позначки, затверджений червень не чіпано й записано як пропущений. Та сама задача вдруге, без змін входів, —
    /// жодного нового запису.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально, відкат власної правки): у <c>RecalculationJob.FormulasAsync</c> передати
    /// <c>writeSubmitted: false</c> — CM4 лишається −4, червоний; прибрати <c>submittedChanges.AddRange</c> — запису
    /// квітня немає, червоний (підсумок TOT тут не змінюється, тож річний набір квітень не рятує); у <c>SheetLogAsync</c>
    /// прибрати умову <c>changes.Contains</c> — після другої ночі записів два, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task B79_нічна_задача_поданий_місяць_перераховано_і_позначено_раз_чернетку_без_позначки_затверджений_пропущено()
    {
        var world = await ArrangeAsync();
        try
        {
            await SeedInputsAsync(world);

            // Усе пораховано, поки нічого не подано (TOT і CM1…CM12 актуальні).
            await RunJobAsync(world, periodKey: null, year: Year, States(closed: []), audit: true);

            await SetCellAsync(world, 4, world.CalcMonth[4], -4m, calculated: true);
            await SetCellAsync(world, 5, world.CalcMonth[5], -5m, calculated: true);
            await SetCellAsync(world, 6, world.CalcMonth[6], -6m, calculated: true);
            await SubmitAsync(world, 4);
            await ApproveAsync(world, 6);

            await RunJobAsync(world, periodKey: null, year: Year, States(closed: []), audit: true);

            Assert.Equal(4 * Input, await PhysicalAsync(world, 4, world.CalcMonth[4]));
            Assert.Equal(5 * Input, await PhysicalAsync(world, 5, world.CalcMonth[5]));
            Assert.Equal(-6m, await PhysicalAsync(world, 6, world.CalcMonth[6]));

            Assert.Equal(1, await SheetLogAsync(world, 4, RecalculationSkips.AuditRecalculatedAfterSubmission));
            Assert.Equal(0, await SheetLogAsync(world, 5, RecalculationSkips.AuditRecalculatedAfterSubmission));
            Assert.Equal(0, await SheetLogAsync(world, 6, RecalculationSkips.AuditRecalculatedAfterSubmission));
            Assert.Equal(1, await SheetLogAsync(world, 6, RecalculationSkips.AuditSkippedApproved));

            // Наступна ніч: входи ті самі — поданому квітню нового запису немає.
            await RunJobAsync(world, periodKey: null, year: Year, States(closed: []), audit: true);

            Assert.Equal(4 * Input, await PhysicalAsync(world, 4, world.CalcMonth[4]));
            Assert.Equal(1, await SheetLogAsync(world, 4, RecalculationSkips.AuditRecalculatedAfterSubmission));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// Не-місячний підсумок (TOT) відкритого якоря пишеться за агрегатом року. Поданий ЛЮТИЙ (решта — чернетки):
    /// підсумок змінився — лютий у наборі поданих місяців року; період прогону (січень, чернетка) — ні. Той самий
    /// прогін удруге — набір порожній.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у гілці B60 <c>RunAsync</c> прибрати <c>NoteAnnualWrite</c> — набір місяців порожній,
    /// червоний (саме так було до правки: гілка виходила раніше за облік); у <c>EnterSheetsAsync</c> не кликати
    /// <c>NoteSubmittedMonth</c> у гілці відкритого якоря — те саме.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task B79_відкритий_якір_підсумок_змінився_поданий_місяць_року_в_наборі_а_вдруге_ні()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            await SubmitAsync(world, 2);

            var (sheets, months) = await RecalculateAnchorWithSinksAsync(world);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(new[] { (Period(2), world.Doc.SheetDefId) }, months);
            Assert.Empty(sheets);

            var (sheetsAgain, monthsAgain) = await RecalculateAnchorWithSinksAsync(world);
            Assert.Empty(monthsAgain);
            Assert.Empty(sheetsAgain);
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// Поданий сам ЯКІР (січень): прогін із записом поданих пише і місячну CM1 (стан січня), і підсумок (агрегат року).
    /// Обидві зміни обліковано: аркуш періоду прогону — за місячну комірку, січень року — за не-місячну.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у гілці B60 <c>RunAsync</c> прибрати додавання в <c>submittedNow</c> — набір аркушів
    /// порожній, червоний; додавати в <c>submittedNow</c> без умови «є місячна комірка» — не ловиться тут (обидві є),
    /// ловить попередній тест (<c>Assert.Empty(sheets)</c> при чернетковому якорі) лише разом зі станом — тому окремо
    /// нижче твердження про чернетковий якір лишається в попередньому тесті.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task B79_відкритий_поданий_якір_місячна_комірка_дає_аркуш_а_підсумок_дає_місяць_року()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            await SubmitAsync(world, Anchor);

            var (sheets, months) = await RecalculateAnchorWithSinksAsync(world);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(Anchor * Input, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
            Assert.Equal(new[] { (world.Doc.SheetDefId, (long?)null) }, sheets);
            Assert.Equal(new[] { (Period(Anchor), world.Doc.SheetDefId) }, months);
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// Закритий якір у відкритому році (B24): задача за названий січень пише лише підсумок під агрегатом року. Поданий
    /// БЕРЕЗЕНЬ лежить поза областю задачі — і все одно отримує запис «перераховано після подання»: число, яке бачить
    /// його погоджувач, змінилось. Січень (не поданий) — без запису.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у <c>SheetLogAsync</c> не додавати <c>changes.PeriodsOf</c> до періодів — запису березня
    /// немає, червоний; у <c>RunAsync</c> прибрати <c>NoteAnnualWrite</c> для <c>annualPeriods</c> — те саме; у
    /// <c>EnterSheetsAsync</c> прибрати <c>!annualAggregate</c> — зайвого запису тут не дає (агрегат — чернетка), тому
    /// окремого твердження немає: умова лише не дає приписати агрегат «поданий» періоду прогону.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task B79_закритий_якір_підсумок_змінився_поданий_місяць_поза_областю_задачі_позначено()
    {
        var world = await ArrangeAsync();
        try
        {
            await SeedInputsAsync(world);
            await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
            await SetCellAsync(world, Anchor, world.Total, 0m, calculated: true);
            await CloseAsync(world, Anchor);
            await SubmitAsync(world, 3);

            // Затверджений травень теж поза областю задачі: у «пропущені» він не йде (задача його й не мала рахувати).
            await ApproveAsync(world, 5);

            await RunJobAsync(world, periodKey: Period(Anchor), year: null, States(closed: [Anchor]), audit: true);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(1, await SheetLogAsync(world, 3, RecalculationSkips.AuditRecalculatedAfterSubmission));
            Assert.Equal(0, await SheetLogAsync(world, Anchor, RecalculationSkips.AuditRecalculatedAfterSubmission));
            Assert.Equal(0, await SheetLogAsync(world, 5, RecalculationSkips.AuditSkippedApproved));

            // Та сама задача вдруге: підсумок не змінився — нового запису березню немає.
            await RunJobAsync(world, periodKey: Period(Anchor), year: null, States(closed: [Anchor]), audit: true);
            Assert.Equal(1, await SheetLogAsync(world, 3, RecalculationSkips.AuditRecalculatedAfterSubmission));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// Поданий якір, а змінився ЛИШЕ підсумок (місячна CM1 уже актуальна): позначка — січню як місяцю року, а аркуш
    /// періоду прогону в набір «місячних» змін не потрапляє (двох записів на одну зміну не буде).
    /// </summary>
    /// <remarks>⛔ ДОКАЗ (локально): у гілці B60 додавати в <c>submittedNow</c> без умови «записано місячну комірку» —
    /// набір аркушів непорожній, червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task B79_відкритий_поданий_якір_змінився_лише_підсумок_аркуш_періоду_не_в_наборі()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            await SetCellAsync(world, Anchor, world.CalcMonth[Anchor], Anchor * Input, calculated: true);
            await SubmitAsync(world, Anchor);

            var (sheets, months) = await RecalculateAnchorWithSinksAsync(world);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Empty(sheets);
            Assert.Equal(new[] { (Period(Anchor), world.Doc.SheetDefId) }, months);
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// Закритий якір (B24), агрегат року «поданий»: січень ЗАТВЕРДЖЕНО, решта місяців подано. Підсумок пишеться (агрегат
    /// дозволяє), позначку отримують одинадцять поданих місяців; затверджений січень — період прогону — ні в набір
    /// аркушів, ні в набір місяців не потрапляє.
    /// </summary>
    /// <remarks>⛔ ДОКАЗ (локально): у <c>EnterSheetsAsync</c> прибрати <c>!annualAggregate</c> — аркуш періоду прогону
    /// (затверджений січень) потрапляє в набір аркушів, червоний; у <c>NoteSubmittedMonth</c> прибрати перевірку стану
    /// місяця — січень у наборі місяців, червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task B79_закритий_якір_агрегат_поданий_позначка_лише_поданим_місяцям_а_не_затвердженому_якорю()
    {
        var world = await ArrangeAsync();
        try
        {
            await SeedInputsAsync(world);
            await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
            await SetCellAsync(world, Anchor, world.Total, 0m, calculated: true);
            await CloseAsync(world, Anchor);
            await ApproveAsync(world, Anchor);
            for (var m = 2; m <= 12; m++)
            {
                await SubmitAsync(world, m);
            }

            var (sheets, months) = await RecalculateAnchorWithSinksAsync(
                world, States(closed: [Anchor]), [.. Enumerable.Range(1, 12).Select(Period)]);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Empty(sheets);
            Assert.Equal(
                Enumerable.Range(2, 11).Select(m => (Period(m), world.Doc.SheetDefId)),
                months.OrderBy(m => m.PeriodKey));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>Повний прогін формул якоря із записом поданих — так, як його кличе <c>RecalculationJob.FormulasAsync</c>.</summary>
    /// <param name="world">Стенд.</param>
    /// <param name="states">Стани місяців; <c>null</c> — усі відкриті.</param>
    /// <param name="annualPeriods">Місяці року для прогону ЗАКРИТОГО якоря (B24); <c>null</c> — відкритий якір.</param>
    private static async Task<(HashSet<(int SheetDefId, long? CopyId)> Sheets, HashSet<(int PeriodKey, int SheetDefId)> Months)>
        RecalculateAnchorWithSinksAsync(
            World world, Dictionary<int, PeriodState>? states = null, IReadOnlyList<int>? annualPeriods = null)
    {
        var sheets = new HashSet<(int SheetDefId, long? CopyId)>();
        var months = new HashSet<(int PeriodKey, int SheetDefId)>();

        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        await Formulas(db, world, store, router, Periods(world, states ?? States(closed: [])))
            .RecalculateAllAsync(
                world.Doc.DocumentId, Key(Anchor), CancellationToken.None,
                writeSubmitted: true, annualPeriods: annualPeriods,
                submittedWritten: sheets, submittedYearWritten: months);

        return (sheets, months);
    }

    /// <summary>Аркуш документа поданий і затверджений у місяці <paramref name="month"/>.</summary>
    private static async Task ApproveAsync(World world, int month)
    {
        await using var setup = world.Builder.CreateContext();
        var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, Period(month));
        state.Submit(userId: 5, DateTime.UtcNow);
        state.Approve(userId: 6, DateTime.UtcNow);
        setup.ApprovalStates.Add(state);
        await setup.SaveChangesAsync();
    }

    /// <summary>Скільки записів журналу аркушів (<c>SheetRecalc</c>) має аркуш документа за місяць і операцію.</summary>
    private async Task<int> SheetLogAsync(World world, int month, string operation)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = @t AND Operation = @o AND CorrelationId = @c";
        command.Parameters.AddWithValue("@t", RecalculationSkips.AuditEntityType);
        command.Parameters.AddWithValue("@o", operation);
        command.Parameters.AddWithValue("@c", RecalculationSkips.AuditCorrelation(world.Doc.DocumentId, world.Doc.SheetDefId, Period(month)));

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
