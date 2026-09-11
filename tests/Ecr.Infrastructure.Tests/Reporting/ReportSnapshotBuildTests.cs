// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuildTests.cs
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
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
}
