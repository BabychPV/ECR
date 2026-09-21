// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuildTests.cs
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Побудова зрізу на РЕАЛЬНІЙ базі: запит агрегації мусить перекладатися в SQL.
/// </summary>
/// <remarks>
/// ⛔ Сусідній <c>ReportSnapshotBuilderTests</c> перевіряє САМУ СУТНІСТЬ —
/// статуси, іммутабельність, перемикання <c>IsCurrent</c>, — і жодного разу не
/// зачіпає <see cref="ReportSnapshotBuilder"/>. Через цю прогалину
/// <c>AggregateAsync</c> роками містив запит, який EF узагалі не міг
/// перекласти (<c>OrderBy</c> стояв на вже спроєктованому типі застосунку):
/// побудова падала <c>InvalidOperationException</c> на КОЖНОМУ виклику.
/// Помітити це не міг ніхто — <c>rpt.ReportDef</c> не створювало ніщо, тож
/// побудова відмовляла раніше, <c>ECR-RPT-0404</c>, і до запиту не доходила
/// (директива №09 <c>W7</c>, сценарій <c>S-27</c>).
///
/// ⚠ Тест стоїть тут, а не лише в сценаріях: <c>dotnet test Ecr.sln</c>
/// сценарну збірку виключає (<c>D2-293</c>), тобто регресію того самого
/// класу основний гейт не побачив би.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotBuildTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.1")]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Зріз_будується_і_читається_назад()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var def = new ReportDef(
            EcrCode.Create($"RPT{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Build test" }),
            isRegulatory: true);

        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new ReportVersion(
            def.Id,
            "1.0",
            """[{"code":"DocumentId","kind":"number"},{"code":"Value","kind":"number"}]""",
            """{"rowSource":"CalculationResults"}""",
            Now);

        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        // ⛔ Головна перевірка — те, що цей виклик узагалі ЗАВЕРШУЄТЬСЯ.
        // Доти він кидав «could not be translated», і жоден тест цього не
        // бачив, бо жоден не звертався до реального будівника.
        var snapshotId = await builder.BuildAsync(
            version.Id, document.ProjectId, document.PeriodKey, parametersJson: null,
            CancellationToken.None);

        Assert.True(snapshotId > 0);

        var list = await builder.ListAsync(
            document.ProjectId, document.PeriodKey.Value, visibleProjectIds: null, CancellationToken.None);

        var made = Assert.Single(list, s => s.Id == snapshotId);

        // ⚠ Зріз порожній (прогону розрахунку в ланцюгу немає) і чернетковий
        // (аркуші не затверджені) — обидва стани коректні (`D-65`).
        // Перевіряється МЕХАНІЗМ: зріз доведений до кінця, поточний і має
        // контрольну суму, якою його можна звірити з тим, що показує SSRS.
        Assert.True(made.IsCurrent, "щойно побудований зріз не позначено поточним.");
        Assert.False(
            string.IsNullOrWhiteSpace(made.ContentHash),
            "зріз побудовано без контрольної суми: звіряти його було б нічим.");
        Assert.Equal(version.Id, made.ReportVersionId);
    }

    /// <remarks>
    /// ⛔ Дефект знайшла наскрізна перевірка <c>tools/smoke.ps1</c> (крок 23):
    /// «побудова зрізу завершилася станом Failed за 211 с». Жоден із тестів не
    /// будував зріз за ПОДАНИЙ період — а саме там статус, успадкований від
    /// даних (<c>D-65</c>), робить новий зріз <c>Submitted</c> ще до того, як у
    /// ньому з'явився хоч один рядок, і правило «поданий не перебудовується»
    /// відмовляло ПЕРШОМУ ж завершенню (<c>ECR-RPT-0409</c>). Тобто зріз за
    /// поданий період не будувався взагалі — рівно тоді, коли він потрібен
    /// регуляторові.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Зріз_за_ПОДАНИМ_періодом_усе_одно_будується()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        // Єдиний аркуш періоду ПОДАНО: `StatusOfDataAsync` виведе `Submitted`,
        // і саме цей статус дістанеться щойно створеному зрізу.
        var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
        state.Submit(userId: 5, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync(CancellationToken.None);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var def = new ReportDef(
            EcrCode.Create($"RPS{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Submitted period test" }),
            isRegulatory: true);

        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new ReportVersion(
            def.Id,
            "1.0",
            """[{"code":"DocumentId","kind":"number"},{"code":"Value","kind":"number"}]""",
            """{"rowSource":"CalculationResults"}""",
            Now);

        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        // ⛔ Головна перевірка — що виклик узагалі ЗАВЕРШУЄТЬСЯ. До виправлення
        // він кидав `DomainException(ECR-RPT-0409)` з наступного ж рядка після
        // створення зрізу, а черга ретраїла це тричі по 30/60/120 с.
        var snapshotId = await builder.BuildAsync(
            version.Id, document.ProjectId, document.PeriodKey, parametersJson: null,
            CancellationToken.None);

        var list = await builder.ListAsync(
            document.ProjectId, document.PeriodKey.Value, visibleProjectIds: null, CancellationToken.None);

        var made = Assert.Single(list, s => s.Id == snapshotId);

        // Статус — успадкований від аркушів, не вигаданий побудовою (D-65).
        Assert.Equal(nameof(SnapshotStatus.Submitted), made.Status);

        // ⚠ І зріз саме ЗАВЕРШЕНИЙ: без цього твердження тест пройшов би й на
        // зрізі-порожняку, який `BuildAsync` устиг зберегти до відмови.
        Assert.False(
            string.IsNullOrWhiteSpace(made.ContentHash),
            "зріз за поданий період побудовано без контрольної суми: завершення не відбулося.");
        Assert.True(made.IsCurrent, "зріз за поданий період не позначено поточним.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Зріз_зберігає_ВИКОРИСТАНІ_значення_параметрів()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var def = new ReportDef(
            EcrCode.Create($"RPP{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Parameters test" }),
            isRegulatory: true);

        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new ReportVersion(
            def.Id,
            "1.0",
            """[{"code":"DocumentId","kind":"number"},{"code":"Value","kind":"number"}]""",
            """
            {"rowSource":"CalculationResults","schema":2,
             "parameters":[{"code":"Threshold","type":"Number","default":5},{"code":"Mode","type":"Text"}],
             "rules":[{"when":"[Value] > @Threshold","then":{"hideRow":true}}]}
            """,
            Now);

        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        var snapshotId = await builder.BuildAsync(
            version.Id, document.ProjectId, document.PeriodKey, """{"Threshold":12}""",
            CancellationToken.None);

        var stored = await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => s.ParametersJson)
            .SingleAsync(CancellationToken.None);

        // ⛔ Записано те, з ЧИМ рахували, а не те, що надіслали: замовчування
        // `Mode` вже підставлене. Інакше зріз, побудований без параметра, не
        // давав би відповіді на питання, на якому значенні стоять його числа.
        Assert.Equal("""{"Threshold":12,"Mode":null}""", stored);
    }
}
