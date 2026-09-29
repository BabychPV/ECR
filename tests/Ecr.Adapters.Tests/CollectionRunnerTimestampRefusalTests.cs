using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
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
/// Збирач і відмова адаптера «мітку часу не прочитати» (<c>ECR-INT-0422</c>
/// <c>.timestampUnreadable</c>, HSE301 F4e / аудит A7) на ОДНОМУ інтервалі.
/// </summary>
/// <remarks>
/// Очікування: цей інтервал без покриття (піде в наздоганяння), решта
/// інтервалів і атрибутів записані, прогін <c>Degraded</c> (не <c>Failed</c>
/// через catch-all B4), а причина — конверт U12 (<c>jobs.collectionRunReason</c>)
/// із кодом і ключем САМОЇ відмови, а не узагальнене «джерело недоступне».
/// <para>
/// Мутаційний доказ: у фейку кидати <see cref="InvalidOperationException"/>
/// замість <see cref="BusinessRuleException"/> 0422 → код причини стає
/// <c>ECR-INT-0503</c>, тест червоний.
/// </para>
/// </remarks>
public sealed class CollectionRunnerTimestampRefusalTests
{
    private const int SourceEntityId = 42;

    private const string RefusalKey = "err.ECR-INT-0422.timestampUnreadable";

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime From = Now.AddDays(-1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "A7")]
    public async Task Відмова_Ts_на_одному_інтервалі_лишає_його_непокритим_а_прогін_Degraded_з_причиною_0422()
    {
        var world = new World();
        world.Maps.Add(EntityFieldMap.ToColumn(SourceEntityId, "tagA", columnDefId: 7));
        world.Maps.Add(EntityFieldMap.ToColumn(SourceEntityId, "tagB", columnDefId: 8));

        // Покриття порожнє → робота: наздоганяння [From − 45 діб, From) і запитаний [From, Now).
        // Атрибут tagA на ЗАПИТАНОМУ інтервалі повертає рядок без читабельного Ts;
        // решта (tagA на наздоганянні, tagB скрізь) читається.
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CollectionResult>>(call =>
            {
                var request = call.ArgAt<CollectionRequest>(0);

                if (request.SourcePath == "tagA" && request.FromUtc >= From)
                {
                    throw new BusinessRuleException(
                        "ECR-INT-0422",
                        "Запит джерела PIAF для «tagA» повернув час типу NULL.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = RefusalKey,
                            ["dataSource"] = "PIAF",
                            ["sourcePath"] = "tagA",
                            ["valueType"] = "NULL",
                        });
                }

                return Task.FromResult(new CollectionResult(
                    [new SourceDataPoint(request.SourcePath, request.ToUtc.AddHours(-1), 10m, null, null, "Good")],
                    [],
                    null));
            });

        // 1. Винятку немає: відмова одного інтервалу не валить задачу, тож і решта
        //    сутностей задачі збирається далі, а не закривається аварійно.
        await world.Runner.RunAsync(SourceEntityId, From, Now, world.Progress, CancellationToken.None);

        // 2. Покриття — лише наздоганяння; запитаний інтервал непокритий.
        var coverage = world.WrittenCoverage();
        Assert.NotEmpty(coverage);
        Assert.All(coverage, c => Assert.True(c.ToUtc <= From, $"покрито {c.FromUtc:O}–{c.ToUtc:O}"));
        Assert.Contains(coverage, c => c.ToUtc == From);

        // 3. Прочитане записано: tagB на запитаному інтервалі, tagA на наздоганянні.
        await world.Store.Received().UpsertRawPointsAsync(
            Arg.Any<long>(), SourceEntityId,
            Arg.Is<IReadOnlyList<SourceDataPoint>>(p => p.Any(x => x.SourcePath == "tagB" && x.Timestamp >= From)),
            Arg.Any<CancellationToken>());
        await world.Store.Received().UpsertRawPointsAsync(
            Arg.Any<long>(), SourceEntityId,
            Arg.Is<IReadOnlyList<SourceDataPoint>>(p => p.Any(x => x.SourcePath == "tagA" && x.Timestamp < From)),
            Arg.Any<CancellationToken>());

        // 4. Прогін — Degraded (затримка), а не Failed; жодного Failed узагалі.
        await world.Store.DidNotReceive().FinishRunAsync(
            Arg.Any<long>(), "Failed", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var message = world.FinishedMessage("Degraded");

        // 5. Причина — конверт U12 з кодом САМОЇ відмови (0422, а не 0503) і її ключем
        //    каталогу з підстановками — а не українське речення адаптера в `detail`.
        Assert.True(
            CollectionRunnerMessageEnvelopeTests.IsReason(message, RefusalKey, "ECR-INT-0422"),
            $"Причина прогону: «{message}».");
        var reason = CollectionRunnerMessageEnvelopeTests.Decode(message).Inner!;
        Assert.Equal("tagA", reason.Params?.GetValueOrDefault("sourcePath"));
    }

    /// <summary>Мінімальне оточення збирача — як у <c>CollectionRunnerTests</c>.</summary>
    private sealed class World
    {
        public World()
        {
            var entity = new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External);
            entity.Describe("Димова труба", @"\\Server\Db\Stack1");

            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
                ExternalTransport.PiSqlClient,
                "Driver={PI SQL Client};Server=pi.example",
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

            Source.Transport.Returns(ExternalTransport.PiSqlClient);

            Catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal)));

            Runner = new CollectionRunner(
                [Source],
                new SourceUnitConverter(new UnitConverter(), Catalog),
                new CatchUpPlanner(Store, new TestClock(Now)),
                Store,
                null);
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IExternalDataSource Source { get; } = Substitute.For<IExternalDataSource>();

        public IUnitCatalog Catalog { get; } = Substitute.For<IUnitCatalog>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public List<EntityFieldMap> Maps { get; } = [];

        public CollectionRunner Runner { get; }

        /// <summary>Усі інтервали, передані в <see cref="ICollectionStore.WriteCoverageAsync"/>.</summary>
        public List<TimeInterval> WrittenCoverage()
            => [.. Store.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ICollectionStore.WriteCoverageAsync))
                .SelectMany(c => (IReadOnlyList<TimeInterval>)c.GetArguments()[2]!)];

        /// <summary>Причина єдиного закриття прогону зі статусом <paramref name="status"/>.</summary>
        public string? FinishedMessage(string status)
        {
            var call = Assert.Single(
                Store.ReceivedCalls(),
                c => c.GetMethodInfo().Name == nameof(ICollectionStore.FinishRunAsync)
                     && (string)c.GetArguments()[1]! == status);

            return (string?)call.GetArguments()[3];
        }
    }
}
