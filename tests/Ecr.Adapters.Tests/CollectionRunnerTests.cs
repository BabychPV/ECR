using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Поведінка збору при відмові джерела і при зміні одиниці (ФВ-11.3, ФВ-16.9).
/// </summary>
/// <remarks>
/// ⚠ Тут перевіряється саме те, що ззовні не видно: простій джерела має бути
/// <b>затримкою, а не втратою</b>, і дізнатися про це можна лише за журналом
/// покриття. Система, яка «нічого не повідомляє», і система, яка «нічого не
/// зібрала», для користувача виглядають однаково.
/// </remarks>
public sealed class CollectionRunnerTests
{
    private const int SourceEntityId = 42;
    private const int KilogramId = 1;
    private const int TonneId = 2;

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відмова_джерела_не_кидає_винятку_і_лишає_діапазон_непокритим()
    {
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CollectionResult>>(_ => throw new HttpRequestException("AF недоступний"));

        await world.Runner.RunAsync(
            SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None);

        // ⚠ Покриття НЕ пишеться: саме порожнеча в журналі відправляє діапазон
        // у наздоганяння. Записане наперед покриття було б дірою, якої більше
        // ніхто не знайде.
        await world.Store.DidNotReceive().WriteCoverageAsync(
            Arg.Any<long>(), Arg.Any<int>(),
            Arg.Is<IReadOnlyList<TimeInterval>>(i => i.Count > 0), Arg.Any<CancellationToken>());

        // Прогін завершується зі станом «Degraded» — це затримка, не збій.
        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Degraded", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Успішний_збір_пише_покриття_і_завершує_прогін_успіхом()
    {
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult(
                [new SourceDataPoint("tag", Now.AddHours(-2), 10m, null, "kg", "Good")], [], null));

        await world.Runner.RunAsync(
            SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None);

        await world.Store.Received().WriteCoverageAsync(
            Arg.Any<long>(), SourceEntityId,
            Arg.Is<IReadOnlyList<TimeInterval>>(i => i.Count > 0), Arg.Any<CancellationToken>());

        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Succeeded", Arg.Any<int>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зміна_UOM_атрибута_зупиняє_збір_і_позначає_прогін_невдалим()
    {
        var world = new World();
        world.Maps.Add(Map(sourceUnitId: KilogramId));

        // Джерело повернуло тонни там, де в мапінгу оголошено кілограми.
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult(
                [new SourceDataPoint("tag", Now.AddHours(-2), 10m, null, "t", "Good")], [], null));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Runner.RunAsync(
                SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);

        await world.Store.Received().FinishRunAsync(
            Arg.Any<long>(), "Failed", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-11.2")]
    public async Task Незареєстрований_транспорт_відмовляє_зрозуміло()
    {
        var world = new World(transport: ExternalTransport.PiWebApi);
        world.Source.Transport.Returns(ExternalTransport.PiSqlClient);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Runner.RunAsync(
                SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-12.1")]
    public async Task Вимкнена_сутність_джерела_відмовляє_а_не_мовчить()
    {
        var world = new World();
        world.Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>())
            .Returns((SourceEntity?)null);

        // ⚠ Саме виняток, а не тихий вихід: розклад, який посилається на
        // неіснуючу сутність, не полагодиться сам, і мовчазний пропуск
        // виглядав би як успішний збір без даних.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Runner.RunAsync(
                SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
    }

    private static EntityFieldMap Map(int? sourceUnitId)
    {
        var map = EntityFieldMap.ToColumn(SourceEntityId, "tag", columnDefId: 7);
        map.SetUnits(sourceUnitId, TonneId);

        return map;
    }

    /// <summary>Мінімальне оточення збирача: сховище, джерело, довідник, годинник.</summary>
    private sealed class World
    {
        public World(ExternalTransport transport = ExternalTransport.PiSqlClient)
        {
            var entity = new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External);
            entity.Describe("Димова труба", @"\\Server\Db\Stack1");

            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
                transport,
                "https://pi.example",
                "PiAf.Primary");

            Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
            Store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
            Store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Maps);
            Store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<TimeInterval>());
            Store.StartRunAsync(
                    Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(77L);
            Store.UpsertRawPointsAsync(
                    Arg.Any<long>(), Arg.Any<int>(),
                    Arg.Any<IReadOnlyList<SourceDataPoint>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => callInfo.ArgAt<IReadOnlyList<SourceDataPoint>>(2).Count);

            Source.Transport.Returns(transport);

            Catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kg"] = new(KilogramId, "kg", DimensionId: 1),
                    ["t"] = new(TonneId, "t", DimensionId: 1, FactorToBase: 1000m),
                },
                new Dictionary<string, int>(StringComparer.Ordinal)));

            Runner = new CollectionRunner(
                [Source],
                new SourceUnitConverter(new UnitConverter(), Catalog),
                new CatchUpPlanner(Store, new TestClock(Now)),
                Store);
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IExternalDataSource Source { get; } = Substitute.For<IExternalDataSource>();

        public IUnitCatalog Catalog { get; } = Substitute.For<IUnitCatalog>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public List<EntityFieldMap> Maps { get; } = [];

        public CollectionRunner Runner { get; }
    }
}
