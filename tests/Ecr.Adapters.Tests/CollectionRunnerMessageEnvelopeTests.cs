// tests/Ecr.Adapters.Tests/CollectionRunnerMessageEnvelopeTests.cs
using Ecr.Adapters.PiAf;
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
/// U12: прогрес і причина прогону збору — конверт (ключ + параметри, <c>Q-326</c>),
/// а не готове українське речення.
/// </summary>
/// <remarks>
/// ⛔ Регресія, яку ловлять тести: <c>CollectionRunner</c> писав «Зібрано точок: …»
/// у <c>itg.JobProgress.Message</c> і «ECR-INT-0503: джерело недоступне» у
/// <c>itg.CollectionRun.ErrorMessage</c>, і на англійському інтерфейсі
/// <c>/admin/jobs</c> та шухляди прогону стояли українські речення.
/// МУТАЦІЙНИЙ ДОКАЗ: повернути будь-який із літералів замість конверта —
/// <see cref="JobProgressMessageCodec.TryDecode"/> дає <c>false</c>, тест червоний.
/// </remarks>
public sealed class CollectionRunnerMessageEnvelopeTests
{
    private const int SourceEntityId = 42;

    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Ключі, які збирач пише в прогрес і причину; кожен мусить бути в сіді.</summary>
    public static TheoryData<string> CollectorKeys =>
    [
        "jobs.collectionProgress", "jobs.collectionDone", "jobs.collectionDonePartial",
        "jobs.collectionRunReason", "jobs.collectionSourceUnavailable", "jobs.collectionSourceError",
        "jobs.collectionSameTimestamp", "jobs.collectionPageLimit", "jobs.collectionUnitChanged",
        "jobs.collectionTimeout", "jobs.collectionCancelled", "jobs.collectionRuleFailed",
        "jobs.collectionRunFailed", "jobs.collectionCloseFailed", "jobs.collectionAuthRefused",
        "jobs.collectionAbandoned",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "U12")]
    public async Task Прогрес_успішного_збору_пише_конверт_із_ключем_і_параметрами()
    {
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult(
                [new SourceDataPoint("STACK-1", Now.AddHours(-2), 10m, null, null, "Good")], [], null));

        await world.Runner.RunAsync(SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None);

        var messages = ProgressMessages(world.Progress);
        Assert.NotEmpty(messages);

        var envelopes = messages.Select(Decode).ToList();

        var step = Assert.Single(envelopes, e => e.Key == "jobs.collectionProgress");
        Assert.Equal("1", step.Params!["step"]);
        Assert.Equal("1", step.Params["total"]);
        Assert.Equal("1", step.Params["points"]);

        var done = envelopes[^1];
        Assert.Equal("jobs.collectionDone", done.Key);
        Assert.Equal("1", done.Params!["points"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "U12")]
    public async Task Відмова_джерела_дає_причину_конвертом_з_кодом_і_безпечним_текстом_параметром()
    {
        // ⛔ SEC (TIER2): сирий `Message` транспорту (хост, URL, порт, пароль) у причину
        // прогону не йде — лише код і кореляція; повний виняток лишається журналу.
        // Мутація: повернути `ex.Message` у `CollectionRunner.Refused` — тест червоний.
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CollectionResult>>(_ => throw new HttpRequestException(
                "No connection could be made (pi01.internal:5450) https://pi01.internal:5450/piwebapi "
                + "Server=db01;Password=Secret123 C:\\Users\\svc\\app\\secret.cfg SELECT * FROM sec.User"));

        await world.Runner.RunAsync(SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None);

        var raw = FinishedMessage(world.Store, "Degraded");
        var reason = Decode(raw);

        Assert.Equal("jobs.collectionRunReason", reason.Key);
        Assert.Equal("ECR-INT-0503", reason.Params!["code"]);
        Assert.Equal("jobs.collectionSourceError", reason.Inner!.Key);
        var detail = reason.Inner.Params!["detail"];
        Assert.Contains("ECR-SYS-0500", detail, StringComparison.Ordinal);
        Assert.Contains("correlation", detail, StringComparison.Ordinal);
        foreach (var secret in new[] { "Secret123", "db01", "secret.cfg", "sec.User", "pi01.internal" })
        {
            Assert.DoesNotContain(secret, raw!, StringComparison.Ordinal);
        }

        // Прогрес теж: частковий збір — окремий ключ, а не речення.
        Assert.Equal("jobs.collectionDonePartial", Decode(ProgressMessages(world.Progress)[^1]).Key);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "U12")]
    public async Task Відмова_джерела_з_кодом_HTTP_називає_лише_код()
    {
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CollectionResult>>(_ => throw new HttpRequestException(
                "Response status code does not indicate success: 503 (https://pi01.internal:5450/piwebapi)",
                null,
                System.Net.HttpStatusCode.ServiceUnavailable));

        await world.Runner.RunAsync(SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None);

        var raw = FinishedMessage(world.Store, "Degraded");
        var detail = Decode(raw).Inner!.Params!["detail"];

        Assert.StartsWith("The source answered HTTP 503", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pi01.internal", raw!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "U12")]
    public async Task Код_відмови_без_тексту_дає_ключ_недоступного_джерела()
    {
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult([], [], "ECR-INT-0503"));

        await world.Runner.RunAsync(SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None);

        var reason = Decode(FinishedMessage(world.Store, "Degraded"));

        Assert.Equal("jobs.collectionRunReason", reason.Key);
        Assert.Equal("jobs.collectionSourceUnavailable", reason.Inner!.Key);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "U12")]
    public async Task Непередбачений_збій_дає_причину_конвертом_Failed()
    {
        var world = new World();
        world.Source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult([], [], null));
        world.Progress.ReportAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException(
                "progress store is down: Server=db01;Password=Secret123 C:\\Users\\svc\\app\\secret.cfg"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Runner.RunAsync(SourceEntityId, Now.AddDays(-1), Now, world.Progress, CancellationToken.None));

        var reason = Decode(FinishedMessage(world.Store, "Failed"));

        Assert.Equal("jobs.collectionRunFailed", reason.Key);
        // ⛔ SEC (TIER2): тип і код каталогу, без `Message` (рядок підключення, шлях); повний
        // виняток — у журналі за номером прогону (77). Мутація: повернути
        // `GetBaseException().Message` у `Describe` — тест червоний.
        Assert.Equal(
            "InvalidOperationException (ECR-SYS-0500); the details are in the server log for run 77",
            reason.Params!["error"]);
    }

    [Theory]
    [MemberData(nameof(CollectorKeys))]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "U12")]
    public void Ключ_збирача_є_в_сіді(string key)
    {
        // ⚠ Ключа без рядка каталог не знайде, і читач побачить сам ключ замість
        // причини — рівно та сама непрозорість, що й сирий український текст.
        var seed = File.ReadAllText(SeedPath());

        Assert.Contains($"(N'{key}',", seed, StringComparison.Ordinal);
    }

    /// <summary>Розбирає конверт; не конверт — провал тесту з самим текстом.</summary>
    internal static JobProgressMessageEnvelope Decode(string? raw)
    {
        Assert.True(JobProgressMessageCodec.TryDecode(raw, out var envelope), $"Не конверт: «{raw}».");

        return envelope;
    }

    /// <summary>
    /// Чи це причина прогону з ключем <paramref name="reasonKey"/> — у рамці
    /// «код: причина» (<c>jobs.collectionRunReason</c>) або без неї.
    /// </summary>
    internal static bool IsReason(string? raw, string reasonKey, string? code = null)
    {
        if (!JobProgressMessageCodec.TryDecode(raw, out var envelope))
        {
            return false;
        }

        if (envelope.Key == "jobs.collectionRunReason")
        {
            return envelope.Inner?.Key == reasonKey
                   && (code is null || envelope.Params?.GetValueOrDefault("code") == code);
        }

        return envelope.Key == reasonKey && code is null;
    }

    private static List<string> ProgressMessages(IJobProgress progress)
        => [.. progress.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IJobProgress.ReportAsync))
            .Select(c => (string)c.GetArguments()[1]!)];

    private static string? FinishedMessage(ICollectionStore store, string status)
        => store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICollectionStore.FinishRunAsync)
                        && (string)c.GetArguments()[1]! == status)
            .Select(c => (string?)c.GetArguments()[3])
            .Single();

    private static string SeedPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("09-seed.sql не знайдено вгору від каталогу збірки.");
    }

    /// <summary>Сутність без мапінгів: збирач читає її саму.</summary>
    private sealed class World
    {
        public World()
        {
            var entity = new SourceEntity(dataSourceId: 5, "STACK-1", RegistrySourceKind.External);
            var dataSource = new DataSource(
                EcrCode.Create("PIAF"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
                ExternalTransport.PiSqlClient,
                "https://pi.example",
                "PiAf.Primary");

            Store.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
            Store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
            Store.GetFieldMapsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<EntityFieldMap>());

            // Покриття повне — працює лише запитаний інтервал, без наздоганяння.
            Store.GetCoverageAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(new[] { new TimeInterval(Now.AddDays(-60), Now) });
            Store.StartRunAsync(
                    Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(77L);
            Store.UpsertRawPointsAsync(
                    Arg.Any<long>(), Arg.Any<int>(),
                    Arg.Any<IReadOnlyList<SourceDataPoint>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<IReadOnlyList<SourceDataPoint>>(2).Count);

            Source.Transport.Returns(ExternalTransport.PiSqlClient);

            var units = Substitute.For<IUnitCatalog>();
            units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal)));

            Runner = new CollectionRunner(
                [Source],
                new SourceUnitConverter(new UnitConverter(), units),
                new CatchUpPlanner(Store, new TestClock(Now)),
                Store);
        }

        public ICollectionStore Store { get; } = Substitute.For<ICollectionStore>();

        public IExternalDataSource Source { get; } = Substitute.For<IExternalDataSource>();

        public IJobProgress Progress { get; } = Substitute.For<IJobProgress>();

        public CollectionRunner Runner { get; }
    }
}
