// tests/Ecr.Infrastructure.Tests/Jobs/FlertCaseLinkTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Flert;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// FLERT F4d (<c>D-476</c>, план <c>FLERT-F4-plan-1112</c> §1.1): формула-ланка події факела з кейсом FLERT
/// <c>REGONE('STREAM_CASE', ROW.STREAM.LEGACY_ID = [FlertStreamId] &amp;&amp; ROW.CASE_NAME = [FlertCaseName])</c> —
/// справжнім парсером і обчислювачем, над довідниками, які завела <see cref="FlertRegistryProvisioner"/> і наповнив
/// синк FLERT (<see cref="FlertCompositionSyncJob"/>), зі знімком бойового <see cref="RegistrySnapshotLoader"/> на дату
/// кінця періоду. Синтетична таблиця комірок — без справжнього шаблону факелів (Q1 відкрите).
/// </summary>
/// <remarks>
/// Дзеркало: потік 1 (1D-2, суша) — «370 Winter», «370 Summer» з 2022; потік 2 (416, суша) — «Units 331-332 - SOR Rev D04»
/// з 01.07.2026; потік 3 (416, суша) — «Train 2 SOR» з 2022; потік 4 (416, море) — «Train 2 SOR» з 2022; потік 5 (416,
/// суша) — «Train 2 SOR» з 01.07.2026 (дубль номера, як 15 дублів на копії 11.11).
/// <para>
/// Мутаційний доказ (локально, не в коміті): у <c>RegistryResolver.IsSelectable</c> прибрати <c>entry.IsValidOn(asOf)</c>
/// → 2 з 7 червоні: <c>Версія_ще_не_чинна_на_кінець_періоду_не_видна_а_з_наступного_видна</c> (кейс із 01.07 видно в
/// червні) і <c>За_номером_потоку_дубль_дає_MULTI_а_суша_море_і_дата_розводять</c> (у червні вже #MULTI).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class FlertCaseLinkTests(SqlServerFixture sql)
{
    private static readonly DateOnly June30 = new(2026, 6, 30);
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateTime Since2022 = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FromJuly = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string[] StreamValidity = ["Stream_ValidFrom", "Stream_ValidTo"];
    private static readonly string[] TypeValidity = ["StreamType_ValidFrom", "StreamType_ValidTo"];

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private FlertRegistryCodes Codes => new($"STREAM_{_tag}", $"STREAM_CASE_{_tag}", $"GAS_COMPOSITION_{_tag}", $"COMPONENT_{_tag}");

    private string Link => $"REGONE('{Codes.Case}', ROW.STREAM.LEGACY_ID = [FlertStreamId] && ROW.CASE_NAME = [FlertCaseName])";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Пара_потік_і_назва_кейсу_дає_рівно_цей_кейс_і_через_нього_склад()
    {
        await ArrangeAsync();

        var link = await EvalAsync(Link, 1, "370 Winter", June30);

        Assert.Null(link.ErrorCode);
        Assert.Equal(await EntryIdAsync(Codes.Case, "C10_20220101"), (long)link.AsNumber()!.Value);

        // F5 тим самим шляхом: склад кейсу через ланку (Σ мол.% цього кейсу; інший кейс потоку не домішується).
        var sum = await EvalAsync($"REGSUM('{Codes.Composition}', ROW.CASE = {Link}, ROW.MOL_PCT)", 1, "370 Winter", June30);
        Assert.Equal(100m, sum.AsNumber());
        var methane = await EvalAsync(
            $"REGSUM('{Codes.Composition}', ROW.CASE = {Link} && ROW.COMPONENT = REGFIND('{Codes.Component}', 'Methane'), ROW.MOL_PCT)",
            1, "370 Summer", June30);
        Assert.Equal(90m, methane.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Невідомий_потік_чи_кейс_дає_N_A_а_не_нуль_чи_першого_ліпшого()
    {
        await ArrangeAsync();

        Assert.Equal(ExpressionErrors.NotAvailable, (await EvalAsync(Link, 1, "370 Autumn", June30)).ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, (await EvalAsync(Link, 999, "370 Winter", June30)).ErrorCode);

        // Склад через ланку без кейсу — та сама помилка, а не Σ = 0 (F7 зробить із неї причину MissingInputs).
        var sum = await EvalAsync($"REGSUM('{Codes.Composition}', ROW.CASE = {Link}, ROW.MOL_PCT)", 1, "370 Autumn", June30);
        Assert.Equal(ExpressionErrors.NotAvailable, sum.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Версія_ще_не_чинна_на_кінець_періоду_не_видна_а_з_наступного_видна()
    {
        await ArrangeAsync();
        const string sor = "Units 331-332 - SOR Rev D04";

        Assert.Equal(ExpressionErrors.NotAvailable, (await EvalAsync(Link, 2, sor, June30)).ErrorCode);

        var july = await EvalAsync(Link, 2, sor, July31);
        Assert.Null(july.ErrorCode);
        Assert.Equal(await EntryIdAsync(Codes.Case, "C20_20260701"), (long)july.AsNumber()!.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task За_номером_потоку_дубль_дає_MULTI_а_суша_море_і_дата_розводять()
    {
        await ArrangeAsync();
        // ⚠ CONTAINS у діалекті шаблону НЕМАЄ (ECR-TMPL-0422, лише методологія): тут точна назва кейсу. Пошук за
        // фрагментом «SOR»/«EOR» (шляхи 2–3 плану F4) — формула методології або поле варіанта кейсу (окреме рішення).
        var byNumber = $"REGONE('{Codes.Case}', ROW.STREAM.NUMBER = '416' && ROW.STREAM.IS_ONSHORE = TRUE && ROW.CASE_NAME = 'Train 2 SOR')";

        // Червень: кейс потоку 5 ще не чинний — лишається один (потік 3); море (потік 4) відсіяне IS_ONSHORE.
        var june = await EvalAsync(byNumber, 0, string.Empty, June30);
        Assert.Null(june.ErrorCode);
        Assert.Equal(await EntryIdAsync(Codes.Case, "C30_20220101"), (long)june.AsNumber()!.Value);

        // Липень: два суходільні потоки з номером 416 і тим самим кейсом — #MULTI, ніколи не «перший» (FirstOrDefault старої системи).
        Assert.Equal(ExpressionErrors.MultipleMatches, (await EvalAsync(byNumber, 0, string.Empty, July31)).ErrorCode);
    }

    /// <summary>
    /// Поведінка СЬОГОДНІ (до відповіді Q6): рівність тексту в фільтрі REGONE — точна; варіант назви з іншими
    /// пробілами чи регістром кейсу не знаходить. Нормалізацію (Q6 = так) формула зробить сама, напр.
    /// <c>UPPER(TRIM(…))</c> з обох боків — тоді цей тест зміниться разом із рішенням.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    [InlineData("370 winter")]
    [InlineData("370  Winter")]
    [InlineData(" 370 Winter ")]
    public async Task Назва_кейсу_з_іншим_регістром_чи_пробілами_сьогодні_не_зіставляється(string variant)
    {
        await ArrangeAsync();

        Assert.Equal(ExpressionErrors.NotAvailable, (await EvalAsync(Link, 1, variant, June30)).ErrorCode);
    }

    // ─── Стенд: синк FLERT у позначені довідники ───────────────────────────

    private async Task ArrangeAsync()
    {
        string dataSource;
        await using (var db = Context())
        {
            dataSource = $"FLL{_tag}";
            db.DataSources.Add(new DataSource(EcrCode.Create(dataSource), new LocalizedText(new Dictionary<string, string> { ["en"] = "FLERT" }),
                ExternalTransport.Sql, "Server=flert.test;Database=FLERT", "flert-reader"));
            await db.SaveChangesAsync();
        }

        await using var provider = BuildProvider();
        await using var root = Context();
        var job = new FlertCompositionSyncJob(
            root, [new FakeFlert(Snapshot())], new IntegrationActor(root, new JobActorScope()), new TestClock(DateTime.UtcNow),
            provider.GetRequiredService<IServiceScopeFactory>(), Substitute.For<IConfiguration>());

        var outcome = await job.RunAsync(new FlertCompositionSyncOptions(true, dataSource), Codes, CancellationToken.None);
        Assert.True(outcome.Status == FlertCompositionSyncJob.Succeeded, $"{outcome.Status} {outcome.Reason}: {outcome.ToJson()}");
    }

    private static ExternalTable Snapshot()
    {
        var rows = new[]
        {
            Row(1, "1D-2", true, 10, "370 Winter", Since2022, new() { ["Methane"] = 95m, ["H2S"] = 5m }),
            Row(1, "1D-2", true, 11, "370 Summer", Since2022, new() { ["Methane"] = 90m, ["Ethane"] = 10m }),
            Row(2, "416", true, 20, "Units 331-332 - SOR Rev D04", FromJuly, new() { ["Methane"] = 100m }),
            Row(3, "416", true, 30, "Train 2 SOR", Since2022, new() { ["Methane"] = 100m }),
            Row(4, "416", false, 40, "Train 2 SOR", Since2022, new() { ["Methane"] = 100m }),
            Row(5, "416", true, 50, "Train 2 SOR", FromJuly, new() { ["Methane"] = 100m }),
        };

        var columns = FlertCompositionSchema.StreamColumns
            .Concat(StreamValidity)
            .Concat(FlertCompositionSchema.CaseColumns)
            .Concat(TypeValidity)
            .Concat(FlertCompositionSchema.PropertyColumns)
            .Concat(FlertCompositionSchema.ComponentColumns.Select(c => c.Column))
            .ToList();

        return new ExternalTable(
            columns,
            [.. rows.Select(r => (IReadOnlyList<object?>)[.. columns.Select(c => r.TryGetValue(c, out var v) ? v : null)])],
            Truncated: false);
    }

    private static Dictionary<string, object?> Row(
        int streamId, string number, bool onshore, int typeId, string caseName, DateTime from, Dictionary<string, decimal?> components)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stream_ID"] = streamId,
            ["StreamNumber"] = number,
            ["Stream_Name"] = $"Stream {streamId}",
            ["isOnshore"] = onshore,
            ["StreamType_ID"] = typeId,
            ["StreamType"] = caseName,
            ["GasComposition_ID"] = typeId * 10,
            ["GasComposition_ValidFrom"] = from,
            ["Temperature"] = 20m,
        };

        foreach (var (column, value) in components)
        {
            row[column] = value;
        }

        return row;
    }

    /// <summary>Обчислює формулу над комірками рядка події (<c>[FlertStreamId]</c>, <c>[FlertCaseName]</c>) і знімком на кінець періоду.</summary>
    private async Task<ExpressionValue> EvalAsync(string formula, int streamId, string caseName, DateOnly periodEnd)
    {
        await using var db = Context();
        var ids = await db.RegistryDefs.Where(d => d.Code == Codes.Case || d.Code == Codes.Composition).Select(d => d.Id).ToListAsync();
        var snapshot = await new RegistrySnapshotLoader(db).LoadAsync(ids, periodEnd, null, CancellationToken.None);

        var inner = new TestEvaluationContext
        {
            Period = new PeriodContext(new DateOnly(periodEnd.Year, periodEnd.Month, 1), periodEnd, CalendarMode.Actual, periodEnd.Year, (byte)periodEnd.Month),
        };
        inner.SetCell("S", "T", string.Empty, "FlertStreamId", ExpressionValue.Number(streamId));
        inner.SetCell("S", "T", string.Empty, "FlertCaseName", ExpressionValue.Text(caseName));

        return Expr.Eval(formula, new SnapshotContext(inner, snapshot));
    }

    private async Task<long> EntryIdAsync(string registry, string code)
    {
        await using var db = Context();
        var id = await db.RegistryDefs.Where(d => d.Code == registry).Select(d => d.Id).SingleAsync();
        return await db.RegistryEntries.Where(e => e.RegistryDefId == id && e.Code == code).Select(e => e.Id).SingleAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());
        return services.BuildServiceProvider();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FakeFlert(ExternalTable table) : IExternalTableSource
    {
        public ExternalTransport Transport => ExternalTransport.Sql;

        public Task<ExternalTable> ReadTableAsync(int dataSourceId, string queryKey, int maxRows, CancellationToken ct)
            => Task.FromResult(table);
    }

    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }

    /// <summary>Контекст із комірками тесту й бойовим знімком довідників (той самий прийом, що в <c>PermitFormulasModeTests</c>).</summary>
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
