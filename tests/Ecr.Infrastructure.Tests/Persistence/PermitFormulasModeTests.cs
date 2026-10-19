// tests/Ecr.Infrastructure.Tests/Persistence/PermitFormulasModeTests.cs
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Режим привʼязки «Formulas» (Permits, PM-12): формула ШАБЛОНУ читає дозвіл через <c>REGSUM/REGCOUNT/REGONE/REGFIELD</c>
/// над реєстрами <c>PRM_&lt;ТИП&gt;_&lt;ТАБЛИЦЯ&gt;</c> - справжнім парсером і обчислювачем, зі знімком, який вантажить бойовий
/// <see cref="RegistrySnapshotLoader"/> на живому SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// Привʼязка в режимі Formulas нічого не зберігає окрім вибору дозволу (колонок немає, рядки не створюються), тож «звʼязок»
/// аркуша з дозволом - це сама формула: Lookup-комірка шапки/рядка з id запису-шапки (<c>[Permit]</c>) або фільтр за полем
/// шапки (<c>ROW.PERMIT.Permit_Number</c>). Знімок бачить дозвіл лише коли шапка <c>IsActive</c>, не видалена і чинна на
/// бізнес-дату (останній день періоду) за пікерною умовою <c>[ValidFrom, ValidTo)</c>; рядки дитини видно рівно тоді, коли
/// видно шапку (композиція, §4.8). Чернетка й закінчений дозвіл дають <c>0</c> у <c>REGSUM</c>, а не помилку.
/// </para>
/// <para>
/// Мутаційний доказ (виконано): у <c>RegistryResolver.IsSelectable</c> прибрати <c>&amp;&amp; entry.IsActive</c> - червоніють
/// «REGSUM_за_ключем…» і «Чернетка_дає_нуль…» (2 з 8), решта зелені.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitFormulasModeTests(SqlServerFixture sql)
{
    private static readonly DateOnly Y2026 = new(2026, 1, 1);
    private static readonly DateOnly Y2027 = new(2027, 1, 1);
    private static readonly DateOnly June30 = new(2026, 6, 30);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string TypeCode => $"FM{_tag}";

    private string Limits => $"PRM_{TypeCode}_Limits";

    private string Header => $"PRM_{TypeCode}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task REGSUM_за_ключем_дозволу_рахує_лише_рядки_цього_дозволу_а_чернетку_не_бачить()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        var a = await ActiveAsync(h, "N-A", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", a, "X", 10m);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", a, "Y", 5.5m);
        var b = await ActiveAsync(h, "N-B", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", b, "Z", 100m);
        var draft = await CreateAsync(h, "N-D", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", draft, "W", 1000m);

        // Індексний шлях (ROW.<Lookup> = вираз без ROW): дозвіл береться з Lookup-комірки аркуша.
        var byKey = $"REGSUM('{Limits}', ROW.PERMIT = [Permit], ROW.Annual)";
        Assert.Equal(15.5m, await NumberAsync(byKey, a));
        Assert.Equal(100m, await NumberAsync(byKey, b));
        Assert.Equal(0m, await NumberAsync(byKey, draft));

        // Повний перегляд: фільтр за полем шапки через Lookup-шлях, без Lookup-комірки.
        Assert.Equal(15.5m, await NumberAsync($"REGSUM('{Limits}', ROW.PERMIT.Permit_Number = 'N-A', ROW.Annual)", null));
        Assert.Equal(2m, await NumberAsync($"REGCOUNT('{Limits}', ROW.PERMIT = [Permit])", a));

        // Усі чинні дозволи разом: чернетка виключена і тут.
        Assert.Equal(115.5m, await NumberAsync($"REGSUM('{Limits}', TRUE, ROW.Annual)", null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_дає_нуль_REGSUM_і_N_A_REGONE_а_після_активації_все_зʼявляється()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        var permit = await CreateAsync(h, "N-D", Y2026, Y2027);
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", permit, "X", 7m);
        var findByNumber = $"REGONE('{Header}', ROW.Permit_Number = 'N-D')";
        var number = "REGFIELD([Permit], 'Permit_Number')";

        // Чернетка (IsActive = 0): рядки невидимі, шапка невидима - REGONE дає #N/A, REGFIELD по Lookup - #REF.
        Assert.Equal(0m, await NumberAsync($"REGSUM('{Limits}', ROW.PERMIT = [Permit], ROW.Annual)", permit));
        Assert.Equal(ExpressionErrors.NotAvailable, (await EvalAsync(findByNumber, null)).ErrorCode);
        Assert.Equal(ExpressionErrors.BadReference, (await EvalAsync(number, permit)).ErrorCode);

        await using var activate = await h.NextRequestAsync();
        await activate.ActivatePermit.HandleAsync(permit, default);

        Assert.Equal(7m, await NumberAsync($"REGSUM('{Limits}', ROW.PERMIT = [Permit], ROW.Annual)", permit));
        Assert.Equal(permit, (long)(await EvalAsync(findByNumber, null)).AsNumber()!.Value);
        Assert.Equal("N-D", (string)(await EvalAsync(number, permit)).Value!);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(2026, 2, 28, 0)] // ще не чинний
    [InlineData(2026, 3, 1, 5)] // перший чинний день - включно
    [InlineData(2026, 5, 31, 5)] // останній чинний день
    [InlineData(2026, 6, 1, 0)] // ValidTo - перший НЕчинний день: виключно
    [InlineData(2026, 6, 30, 0)] // закінчений на кінець періоду
    public async Task Межі_вікна_чинності_береться_кінець_періоду_і_ValidTo_виключно(int year, int month, int day, int expected)
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        var permit = await ActiveAsync(h, "N-W", new DateOnly(2026, 3, 1), new DateOnly(2026, 6, 1));
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", permit, "X", 5m);

        var periodEnd = new DateOnly(year, month, day);
        Assert.Equal((decimal)expected, await NumberAsync($"REGSUM('{Limits}', ROW.PERMIT = [Permit], ROW.Annual)", permit, periodEnd));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозвіл_що_закінчився_посеред_періоду_на_кінець_періоду_дає_нуль_а_не_частку()
    {
        // Знімок будується на ОДНУ дату - останній день періоду. Дозвіл до 15.06 у червневому періоді (кінець 30.06)
        // нечинний, тож REGSUM = 0, хоч півмісяця він діяв. Це поведінка «як реалізовано», а не побажання (див. FEATURE-PERMITS §5).
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        await PermitFixtures.PublishTypeAsync(h, TypeCode);

        var permit = await ActiveAsync(h, "N-M", Y2026, new DateOnly(2026, 6, 15));
        await PermitFixtures.AddRowAsync(h, TypeCode, "Limits", permit, "X", 9m);

        Assert.Equal(0m, await NumberAsync($"REGSUM('{Limits}', ROW.PERMIT = [Permit], ROW.Annual)", permit, June30));
        Assert.Equal(9m, await NumberAsync($"REGSUM('{Limits}', ROW.PERMIT = [Permit], ROW.Annual)", permit, new DateOnly(2026, 5, 31)));
    }

    private async Task<long> ActiveAsync(PermitHarness h, string number, DateOnly from, DateOnly to)
    {
        var id = await CreateAsync(h, number, from, to);
        await PermitFixtures.ActivateDirectAsync(h, TypeCode, id);
        return id;
    }

    private async Task<long> CreateAsync(PermitHarness h, string number, DateOnly from, DateOnly to)
    {
        await using var request = await h.NextRequestAsync();
        return (await request.CreatePermit.HandleAsync(new CreatePermitRequest(TypeCode, number, null, null, from, to, null), default)).Id;
    }

    private async Task<decimal> NumberAsync(string formula, long? permit, DateOnly? periodEnd = null)
    {
        var value = await EvalAsync(formula, permit, periodEnd);
        return value.AsNumber() ?? throw new InvalidOperationException($"{formula} -> {value.Type} {value.ErrorCode}");
    }

    private async Task<ExpressionValue> EvalAsync(string formula, long? permit, DateOnly? periodEnd = null)
    {
        var date = periodEnd ?? new DateOnly(2026, 6, 15);
        await using var db = sql.CreateContext();
        var ids = db.RegistryDefs.Where(d => d.Code == Header || d.Code == Limits).Select(d => d.Id).ToList();
        var snapshot = await new RegistrySnapshotLoader(db).LoadAsync(ids, date, null, CancellationToken.None);

        var inner = new TestEvaluationContext
        {
            Period = new PeriodContext(new DateOnly(date.Year, date.Month, 1), date, CalendarMode.Actual, date.Year, (byte)date.Month),
        };
        if (permit is { } id)
        {
            inner.SetCell("S", "T", string.Empty, "Permit", ExpressionValue.Number(id));
        }

        return Expr.Eval(formula, new SnapshotContext(inner, snapshot));
    }

    /// <summary>Контекст із комірками тесту й бойовим знімком довідників.</summary>
    private sealed class SnapshotContext(IEvaluationContext inner, IRegistrySnapshot snapshot) : IEvaluationContext
    {
        public IRegistrySnapshot? Registries => snapshot;

        public PeriodContext Period => inner.Period;

        public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference) => inner.Read(reference);

        public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
            => inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

        public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
            => inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

        public ExpressionValue GetArgument(string name) => inner.GetArgument(name);

        public ExpressionValue GetConstant(string name) => inner.GetConstant(name);

        public ExpressionValue GetFormulaResult(string name) => inner.GetFormulaResult(name);

        public ExpressionValue GetHeader(string name) => inner.GetHeader(name);

        public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
            => inner.GetRegistryField(registryEntryId, fieldCode);

        public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
            => inner.Convert(value, fromUnitCode, toUnitCode);
    }
}
